#!/usr/bin/env python3
"""Pinned, resumable, verified Hugging Face model acquisition for FlashNext."""
from __future__ import annotations

import argparse
import concurrent.futures
import contextlib
import datetime as dt
import hashlib
import json
import os
import pathlib
import shutil
import sys
import threading
import time
from dataclasses import dataclass
from typing import Any, Iterable, TextIO

os.environ.setdefault("HF_HUB_DISABLE_TELEMETRY", "1")
os.environ.setdefault("PYTHONUNBUFFERED", "1")

try:
    from huggingface_hub import HfApi, hf_hub_download
except ImportError as exc:
    raise SystemExit("The pinned huggingface_hub environment is missing. Rerun install.cmd.") from exc

GIB = 1024**3
GGUF_MAGIC = b"GGUF"
FORBIDDEN_REMOTE_SUFFIXES = (".exe", ".dll", ".py", ".ps1", ".bat", ".cmd", ".msi", ".com", ".scr", ".vbs", ".js")


class DownloadError(RuntimeError):
    pass


@dataclass(frozen=True)
class LockedFile:
    path: str
    sha256: str
    expected_bytes: int
    kind: str
    repository: str
    revision: str
    xet_sha256: str | None = None


class _TeeStream:
    def __init__(self, stream: TextIO, path: pathlib.Path | None) -> None:
        self._stream = stream
        self._file: TextIO | None = None
        if path is not None:
            path.parent.mkdir(parents=True, exist_ok=True)
            self._file = path.open("a", encoding="utf-8", newline="\n")

    def write(self, data: str) -> int:
        written = self._stream.write(data)
        self._stream.flush()
        if self._file is not None:
            self._file.write(data)
            self._file.flush()
        return written

    def flush(self) -> None:
        self._stream.flush()
        if self._file is not None:
            self._file.flush()

    def close(self) -> None:
        if self._file is not None:
            self._file.close()
            self._file = None

    @property
    def encoding(self) -> str:
        return getattr(self._stream, "encoding", "utf-8")


def emit(event: str, message: str, **data: Any) -> None:
    record = {"event": event, "message": message, "timestampUtc": dt.datetime.now(dt.timezone.utc).isoformat(), **data}
    print(json.dumps(record, ensure_ascii=False, separators=(",", ":")), flush=True)


def hugging_face_token() -> str | bool:
    for key in ("HF_TOKEN", "HUGGING_FACE_HUB_TOKEN"):
        value = os.environ.get(key, "").strip()
        if value:
            return value
    return False


def staged_payload_bytes(stage: pathlib.Path) -> int:
    total = 0
    if not stage.exists():
        return 0
    skip_names = {".incomplete.json", ".flashnext-model.lock.json", ".gitignore", "CACHEDIR.TAG"}
    for path in stage.rglob("*"):
        if not path.is_file() or path.name in skip_names:
            continue
        try:
            total += path.stat().st_size
        except OSError:
            continue
    return total


def watch_stage_progress(stage: pathlib.Path, expected_bytes: int, stop: threading.Event) -> None:
    while not stop.wait(15):
        staged = staged_payload_bytes(stage)
        emit(
            "download-progress",
            f"Staged {staged / GIB:.2f} GiB of {expected_bytes / GIB:.1f} GiB.",
            stagedBytes=staged,
            expectedBytes=expected_bytes,
        )


def load_json(path: pathlib.Path) -> dict[str, Any]:
    with path.open("r", encoding="utf-8") as handle:
        value = json.load(handle)
    if not isinstance(value, dict):
        raise DownloadError(f"'{path}' must contain a JSON object.")
    return value


def write_json_atomic(path: pathlib.Path, value: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + ".new-" + os.urandom(8).hex())
    with temporary.open("w", encoding="utf-8", newline="\n") as handle:
        json.dump(value, handle, ensure_ascii=False, indent=2)
        handle.write("\n")
        handle.flush()
        os.fsync(handle.fileno())
    os.replace(temporary, path)


def sha256_file(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(8 * 1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def validate_lock(lock: dict[str, Any]) -> None:
    revision = str(lock.get("revision", ""))
    if len(revision) != 40 or any(char not in "0123456789abcdef" for char in revision.lower()):
        raise DownloadError("Model lock revision is not a full immutable commit SHA.")
    files = lock.get("files")
    if not isinstance(files, list) or len(files) != 6:
        raise DownloadError("Model lock must contain exactly four UD-Q4_K_XL target shards, one MTP sidecar, and one vision projector.")
    paths = [str(item.get("path", "")) for item in files if isinstance(item, dict)]
    expected_targets = [f"UD-Q4_K_XL/Qwen3.8-Flash-Next-UD-Q4_K_XL-{index:05d}-of-00004.gguf" for index in range(1, 5)]
    expected = expected_targets + ["MTP/mtp-Qwen3.8-Flash-Next-shared-Q8_0.gguf", "mmproj-F16.gguf"]
    if paths != expected:
        raise DownloadError("Model lock file allowlist does not match the factory model and vision-projector set.")
    for item in files:
        digest = str(item.get("sha256", ""))
        if len(digest) != 64 or any(char not in "0123456789abcdef" for char in digest.lower()):
            raise DownloadError(f"Invalid SHA-256 in model lock for '{item.get('path')}'.")
        source_revision = str(item.get("revision", revision))
        if len(source_revision) != 40 or any(char not in "0123456789abcdef" for char in source_revision.lower()):
            raise DownloadError(f"Invalid immutable source revision for '{item.get('path')}'.")
    projector = files[-1]
    if projector.get("kind") != "mmproj" or source_for(lock, projector)[0] != "unsloth/Qwen3.8-Flash-Next-GGUF":
        raise DownloadError("Model lock must pin the approved Flash-Next F16 vision projector source.")


def remote_sha(entry: Any) -> str | None:
    lfs = getattr(entry, "lfs", None)
    if isinstance(lfs, dict):
        for key in ("sha256", "oid"):
            value = lfs.get(key)
            if isinstance(value, str):
                return value.removeprefix("sha256:").lower()
    if lfs is not None:
        for key in ("sha256", "oid"):
            value = getattr(lfs, key, None)
            if isinstance(value, str):
                return value.removeprefix("sha256:").lower()
    return None


def assert_downloaded_files_are_safe(filenames: Iterable[str]) -> None:
    for raw in filenames:
        name = str(raw).replace("\\", "/")
        suffix = pathlib.PurePosixPath(name.lower()).suffix
        if suffix in FORBIDDEN_REMOTE_SUFFIXES:
            raise DownloadError(f"Refusing to download executable or script content '{name}'. Acquisition stopped without executing it.")


def source_for(lock: dict[str, Any], item: dict[str, Any]) -> tuple[str, str]:
    repository = str(item.get("repository", lock["repository"]))
    revision = str(item.get("revision", lock["revision"]))
    return repository, revision


def resolve_locked_files(lock: dict[str, Any], api: HfApi) -> tuple[list[LockedFile], list[str]]:
    snapshots: dict[tuple[str, str], dict[str, Any]] = {}
    for item in lock["files"]:
        repository, revision = source_for(lock, item)
        key = (repository, revision)
        if key in snapshots:
            continue
        emit("metadata", "Resolving exact byte counts from an immutable model snapshot.", repository=repository, revision=revision)
        info = api.model_info(repo_id=repository, revision=revision, files_metadata=True)
        if str(info.sha) != revision:
            raise DownloadError(f"Hugging Face resolved '{revision}' to unexpected commit '{info.sha}'.")
        snapshots[key] = {entry.rfilename: entry for entry in (info.siblings or [])}
    resolved: list[LockedFile] = []
    for item in lock["files"]:
        path = str(item["path"])
        repository, revision = source_for(lock, item)
        siblings = snapshots[(repository, revision)]
        entry = siblings.get(path)
        if entry is None:
            raise DownloadError(f"Immutable model snapshot is missing required file '{path}'.")
        size = getattr(entry, "size", None)
        if not isinstance(size, int) or size <= 0:
            raise DownloadError(f"Immutable metadata did not provide a positive byte count for '{path}'.")
        pinned_sha = str(item["sha256"]).lower()
        published_sha = remote_sha(entry)
        if published_sha is not None and published_sha != pinned_sha:
            raise DownloadError(f"Published LFS identity for '{path}' differs from model.lock.json.")
        resolved.append(LockedFile(path, pinned_sha, size, str(item.get("kind", "target")), repository, revision, item.get("xetSha256")))
    metadata = [str(name) for name in lock.get("metadataFiles", []) if str(name) in siblings]
    assert_downloaded_files_are_safe([item.path for item in resolved] + metadata)
    return resolved, metadata


def installed_files(lock: dict[str, Any], destination: pathlib.Path) -> tuple[list[LockedFile], list[str]] | None:
    installed_path = destination / ".flashnext-model.lock.json"
    if not installed_path.is_file():
        return None
    installed = load_json(installed_path)
    if installed.get("repository") != lock.get("repository") or installed.get("revision") != lock.get("revision"):
        return None
    items = installed.get("files")
    if not isinstance(items, list) or len(items) != len(lock.get("files", [])):
        return None
    result: list[LockedFile] = []
    lock_items = {str(item.get("path", "")): item for item in lock["files"] if isinstance(item, dict)}
    for item in items:
        try:
            path = str(item["path"])
            source = lock_items.get(path)
            if source is None:
                return None
            repository, revision = source_for(lock, source)
            if str(item.get("repository", repository)) != repository or str(item.get("revision", revision)) != revision:
                return None
            result.append(LockedFile(path, str(item["sha256"]).lower(), int(item["expectedBytes"]), str(item.get("kind", "target")), repository, revision, item.get("xetSha256")))
        except (KeyError, TypeError, ValueError):
            return None
    return result, [str(name) for name in installed.get("metadataFiles", [])]


def verify_path(path: pathlib.Path, locked: LockedFile, *, compute_hash: bool = True) -> tuple[bool, str]:
    if not path.is_file():
        return False, "missing"
    size = path.stat().st_size
    if size != locked.expected_bytes:
        return False, f"size mismatch: expected {locked.expected_bytes}, found {size}"
    if locked.kind in {"target", "mtp", "mmproj"}:
        with path.open("rb") as handle:
            magic = handle.read(4)
        if magic != GGUF_MAGIC:
            return False, "not a GGUF file"
    if compute_hash:
        actual = sha256_file(path)
        if actual.lower() != locked.sha256.lower():
            return False, f"SHA-256 mismatch: expected {locked.sha256}, found {actual}"
    return True, "verified"


def verify_all(destination: pathlib.Path, files: Iterable[LockedFile]) -> tuple[bool, list[dict[str, Any]]]:
    results: list[dict[str, Any]] = []
    success = True
    for locked in files:
        path = destination / pathlib.PurePosixPath(locked.path)
        emit("verify-start", f"Verifying {locked.path}.", path=locked.path, expectedBytes=locked.expected_bytes)
        valid, detail = verify_path(path, locked)
        results.append({"path": locked.path, "success": valid, "message": detail, "expectedBytes": locked.expected_bytes, "sha256": locked.sha256})
        emit("verify-file", f"{locked.path}: {detail}", path=locked.path, success=valid)
        success = success and valid
    return success, results


def quarantine(destination: pathlib.Path, relative: str, reason: str) -> None:
    source = destination / pathlib.PurePosixPath(relative)
    if not source.exists():
        return
    stamp = dt.datetime.now().strftime("%Y%m%d-%H%M%S")
    target = destination.parent / (destination.name + ".quarantine") / stamp / pathlib.PurePosixPath(relative)
    target.parent.mkdir(parents=True, exist_ok=True)
    os.replace(source, target)
    emit("quarantine", f"Moved invalid file '{relative}' to quarantine.", path=relative, reason=reason, quarantine=str(target))


def link_or_copy(source: pathlib.Path, target: pathlib.Path) -> None:
    target.parent.mkdir(parents=True, exist_ok=True)
    with contextlib.suppress(FileNotFoundError):
        target.unlink()
    try:
        os.link(source, target)
    except OSError:
        shutil.copy2(source, target)


def ensure_space(destination: pathlib.Path, minimum_gib: int, recommended_gib: int) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    free = shutil.disk_usage(destination.parent).free
    emit("disk-space", f"Selected drive has {free / GIB:.1f} GiB free; {minimum_gib} GiB is required and {recommended_gib} GiB is recommended.", availableBytes=free, minimumBytes=minimum_gib * GIB, recommendedBytes=recommended_gib * GIB)
    if free < minimum_gib * GIB:
        raise DownloadError(f"Insufficient free space: {free / GIB:.1f} GiB available; {minimum_gib} GiB required.")


def download_one(repo: str, revision: str, relative: str, cache: pathlib.Path, stage: pathlib.Path) -> pathlib.Path:
    emit("download-start", f"Downloading {relative}.", path=relative)
    result = hf_hub_download(
        repo_id=repo,
        filename=relative,
        revision=revision,
        cache_dir=str(cache),
        local_dir=str(stage),
        token=hugging_face_token(),
    )
    path = pathlib.Path(result)
    emit("download-finished", f"Transfer completed for {relative}.", path=relative, localPath=str(path))
    return path


def prepare_stage(destination: pathlib.Path, lock: dict[str, Any], files: list[LockedFile]) -> pathlib.Path:
    stage = destination.parent / (destination.name + ".staging")
    marker = stage / ".incomplete.json"
    reuse = False
    if stage.exists() and marker.is_file():
        try:
            previous = load_json(marker)
            reuse = previous.get("repository") == lock.get("repository") and previous.get("revision") == lock.get("revision")
        except (OSError, DownloadError, json.JSONDecodeError, ValueError):
            reuse = False
    if stage.exists() and not reuse:
        shutil.rmtree(stage)
    stage.mkdir(parents=True, exist_ok=True)
    if reuse:
        emit("resume", "Reusing the existing staging directory and Hugging Face cache. Resumable cache data was preserved.")
        for locked in files:
            candidate = stage / pathlib.PurePosixPath(locked.path)
            if not candidate.exists():
                continue
            valid, detail = verify_path(candidate, locked, compute_hash=False)
            if valid:
                emit("reuse-staged", f"Keeping complete staged file {locked.path}.", path=locked.path)
            else:
                candidate.unlink()
                emit("discard-partial", f"Removed incomplete staged file {locked.path}: {detail}.", path=locked.path)
    write_json_atomic(marker, {"repository": lock["repository"], "revision": lock["revision"], "startedAtUtc": dt.datetime.now(dt.timezone.utc).isoformat()})
    return stage


def promote(stage: pathlib.Path, destination: pathlib.Path) -> None:
    backup = destination.parent / (destination.name + ".previous")
    if backup.exists():
        shutil.rmtree(backup)
    if destination.exists():
        os.replace(destination, backup)
    try:
        os.replace(stage, destination)
    except BaseException:
        if not destination.exists() and backup.exists():
            os.replace(backup, destination)
        raise
    if backup.exists():
        shutil.rmtree(backup)


def installed_lock_value(lock: dict[str, Any], files: list[LockedFile], metadata: list[str], accepted: bool, results: list[dict[str, Any]]) -> dict[str, Any]:
    return {
        "manifestVersion": "1.0.0",
        "repository": lock["repository"],
        "revision": lock["revision"],
        "verifiedAtUtc": dt.datetime.now(dt.timezone.utc).isoformat(),
        "license": "Qwen Community License 1.0",
        "licenseAccepted": accepted,
        "qualityDisclosure": lock.get("qualityDisclosure"),
        "visionIncluded": any(item.kind == "mmproj" for item in files),
        "files": [{"path": item.path, "expectedBytes": item.expected_bytes, "sha256": item.sha256, "xetSha256": item.xet_sha256, "kind": item.kind, "repository": item.repository, "revision": item.revision} for item in files],
        "metadataFiles": metadata,
        "verification": results,
    }


def primary_files_are_retained(destination: pathlib.Path, primary: list[LockedFile], lock: dict[str, Any]) -> bool:
    installed_path = destination / ".flashnext-model.lock.json"
    if not installed_path.is_file():
        return False
    try:
        installed = load_json(installed_path)
        items = {str(item.get("path", "")): item for item in installed.get("files", []) if isinstance(item, dict)}
    except (OSError, DownloadError, json.JSONDecodeError, ValueError):
        return False
    for locked in primary:
        item = items.get(locked.path)
        if item is None or str(item.get("sha256", "")).lower() != locked.sha256 or int(item.get("expectedBytes", -1)) != locked.expected_bytes:
            return False
        valid, _ = verify_path(destination / pathlib.PurePosixPath(locked.path), locked, compute_hash=False)
        if not valid:
            return False
    return True


def overlay_additions(lock: dict[str, Any], destination: pathlib.Path, cache: pathlib.Path, workers: int, accept_license: bool, files: list[LockedFile], metadata: list[str]) -> int:
    if not accept_license:
        raise DownloadError("License acceptance is required for model acquisition.")
    primary = [item for item in files if item.kind != "mmproj"]
    additions = [item for item in files if item.kind == "mmproj"]
    if not additions:
        raise DownloadError("No additive model artifacts were found.")
    if not primary_files_are_retained(destination, primary, lock):
        raise DownloadError("Existing target and MTP files are not covered by their installed verified lock; refusing to replace them while adding the vision projector.")

    stage = destination.parent / (destination.name + ".vision.staging")
    if stage.exists():
        shutil.rmtree(stage)
    stage.mkdir(parents=True, exist_ok=True)
    try:
        for locked in additions:
            target = destination / pathlib.PurePosixPath(locked.path)
            valid, _ = verify_path(target, locked) if target.exists() else (False, "missing")
            if valid:
                emit("reuse", f"Reused verified vision artifact {locked.path}.", path=locked.path)
                continue
            staged = download_one(locked.repository, locked.revision, locked.path, cache, stage)
            expected = stage / pathlib.PurePosixPath(locked.path)
            if staged.resolve() != expected.resolve() and staged.is_file():
                expected.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(staged, expected)
            valid, detail = verify_path(expected, locked)
            if not valid:
                raise DownloadError(f"Downloaded '{locked.path}' failed verification: {detail}")
            target.parent.mkdir(parents=True, exist_ok=True)
            if target.exists():
                quarantine(destination, locked.path, "vision projector did not match the new immutable lock")
            os.replace(expected, target)
            emit("verified-file", f"Verified and added {locked.path} without replacing target or MTP files.", path=locked.path, expectedBytes=locked.expected_bytes, sha256=locked.sha256)

        results = [{"path": item.path, "success": True, "message": "retained from previous verified installation", "expectedBytes": item.expected_bytes, "sha256": item.sha256} for item in primary]
        complete, addition_results = verify_all(destination, additions)
        results.extend(addition_results)
        if not complete:
            raise DownloadError("Added vision projector failed final verification.")
        write_json_atomic(destination / ".flashnext-model.lock.json", installed_lock_value(lock, files, metadata, True, results))
        emit("complete", "Vision projector download, verification, and in-place integration completed without replacing target or MTP files.", success=True, destination=str(destination))
        return 0
    finally:
        if stage.exists():
            shutil.rmtree(stage)


def run_verify(lock: dict[str, Any], destination: pathlib.Path, api: HfApi) -> int:
    cached = installed_files(lock, destination)
    if cached is not None:
        files, _ = cached
        emit("metadata", "Using the installed immutable lock for offline-capable verification.")
    else:
        files, _ = resolve_locked_files(lock, api)
    success, results = verify_all(destination, files)
    emit("verified" if success else "verification-failed", "Model verification complete." if success else "Model verification failed.", success=success, files=results)
    return 0 if success else 2


def run_download(lock: dict[str, Any], destination: pathlib.Path, cache: pathlib.Path, workers: int, accept_license: bool, api: HfApi) -> int:
    if not accept_license:
        raise DownloadError("License acceptance is required for model acquisition.")
    ensure_space(destination, int(lock.get("minimumFreeGiB", 115)), int(lock.get("recommendedFreeGiB", 140)))
    files, metadata = resolve_locked_files(lock, api)
    if destination.exists() and any(item.kind == "mmproj" for item in files):
        return overlay_additions(lock, destination, cache, workers, accept_license, files, metadata)
    if destination.exists():
        complete, results = verify_all(destination, files)
        if complete:
            current = installed_lock_value(lock, files, metadata, True, results)
            write_json_atomic(destination / ".flashnext-model.lock.json", current)
            emit("complete", "All active model files are already verified.", success=True)
            return 0

    stage = prepare_stage(destination, lock, files)

    pending: list[LockedFile] = []
    for locked in files:
        staged = stage / pathlib.PurePosixPath(locked.path)
        if staged.exists():
            valid, detail = verify_path(staged, locked, compute_hash=False)
            if valid:
                emit("reuse", f"Reused complete staged file {locked.path}.", path=locked.path)
                continue
        active = destination / pathlib.PurePosixPath(locked.path)
        valid, detail = verify_path(active, locked) if active.exists() else (False, "missing")
        if valid:
            link_or_copy(active, staged)
            emit("reuse", f"Reused verified active file {locked.path}.", path=locked.path)
        else:
            if active.exists():
                quarantine(destination, locked.path, detail)
            pending.append(locked)

    cache.mkdir(parents=True, exist_ok=True)
    os.environ["HF_HUB_CACHE"] = str(cache)
    expected_total = sum(item.expected_bytes for item in files)
    stop_progress = threading.Event()
    progress_thread = threading.Thread(target=watch_stage_progress, args=(stage, expected_total, stop_progress), name="flashnext-progress", daemon=True)
    progress_thread.start()
    try:
        with concurrent.futures.ThreadPoolExecutor(max_workers=workers, thread_name_prefix="flashnext-download") as executor:
            future_map = {executor.submit(download_one, item.repository, item.revision, item.path, cache, stage): item for item in pending}
            for future in concurrent.futures.as_completed(future_map):
                locked = future_map[future]
                try:
                    downloaded = future.result()
                    expected = stage / pathlib.PurePosixPath(locked.path)
                    if downloaded.resolve() != expected.resolve() and downloaded.is_file():
                        expected.parent.mkdir(parents=True, exist_ok=True)
                        shutil.copy2(downloaded, expected)
                    valid, detail = verify_path(expected, locked)
                    if not valid:
                        raise DownloadError(f"Downloaded '{locked.path}' failed verification: {detail}")
                    emit("verified-file", f"Verified downloaded file {locked.path}.", path=locked.path, expectedBytes=locked.expected_bytes, sha256=locked.sha256)
                except BaseException:
                    for other in future_map:
                        other.cancel()
                    raise
    finally:
        stop_progress.set()
        progress_thread.join(timeout=1)

    for relative in metadata:
        download_one(str(lock["repository"]), str(lock["revision"]), relative, cache, stage)
    complete, results = verify_all(stage, files)
    if not complete:
        raise DownloadError("Staged model verification failed; active model files were not changed.")
    with contextlib.suppress(FileNotFoundError):
        (stage / ".incomplete.json").unlink()
    write_json_atomic(stage / ".flashnext-model.lock.json", installed_lock_value(lock, files, metadata, True, results))
    promote(stage, destination)
    emit("complete", "Model download, verification, and atomic promotion completed.", success=True, destination=str(destination))
    return 0


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Acquire or verify the pinned FlashNext GGUF model.")
    parser.add_argument("--mode", choices=("download", "verify"), required=True)
    parser.add_argument("--lock", type=pathlib.Path, required=True)
    parser.add_argument("--destination", type=pathlib.Path, required=True)
    parser.add_argument("--cache", type=pathlib.Path, required=True)
    parser.add_argument("--workers", type=int, default=2)
    parser.add_argument("--accept-license", action="store_true")
    return parser.parse_args(argv)


def install_log_tee() -> _TeeStream | None:
    raw = os.environ.get("FLASHNEXT_DOWNLOAD_LOG", "").strip()
    if not raw:
        return None
    tee = _TeeStream(sys.stdout, pathlib.Path(raw))
    sys.stdout = tee
    return tee


def main(argv: list[str]) -> int:
    args = parse_args(argv)
    if args.workers not in (1, 2):
        raise DownloadError("Worker count must be one or two.")
    endpoint = os.environ.get("HF_ENDPOINT", "https://huggingface.co").rstrip("/")
    if endpoint != "https://huggingface.co":
        raise DownloadError("A custom Hugging Face endpoint is not permitted for the factory model.")
    lock = load_json(args.lock.resolve())
    validate_lock(lock)
    destination = args.destination.expanduser().resolve()
    cache = args.cache.expanduser().resolve()
    os.environ["HF_HUB_CACHE"] = str(cache)
    api = HfApi(endpoint="https://huggingface.co")
    if args.mode == "verify":
        return run_verify(lock, destination, api)
    return run_download(lock, destination, cache, args.workers, args.accept_license, api)


if __name__ == "__main__":
    tee = install_log_tee()
    try:
        raise SystemExit(main(sys.argv[1:]))
    except KeyboardInterrupt:
        emit("cancelled", "Model operation was cancelled. Resumable cache data was preserved; no unverified staged directory was promoted.", success=False)
        raise SystemExit(130)
    except Exception as exc:
        emit("error", str(exc), errorType=type(exc).__name__, success=False)
        raise SystemExit(1)
    finally:
        if tee is not None:
            tee.close()
