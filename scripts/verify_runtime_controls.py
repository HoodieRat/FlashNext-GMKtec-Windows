"""Verify bundled MTP/Batch controls and template prefix stability without inference.

Only reads model metadata, invokes --version after parsing flags, and optionally
calls /apply-template and /tokenize. Never calls a completion or benchmark route.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import struct
import subprocess
import urllib.request


def metadata(path):
    sizes = {0: 1, 1: 1, 2: 2, 3: 2, 4: 4, 5: 4, 6: 4, 7: 1, 10: 8, 11: 8, 12: 8}
    formats = {0: "B", 1: "b", 2: "H", 3: "h", 4: "I", 5: "i", 6: "f", 7: "?", 10: "Q", 11: "q", 12: "d"}
    with path.open("rb") as stream:
        def number(fmt):
            return struct.unpack("<" + fmt, stream.read(struct.calcsize(fmt)))[0]

        def string(keep=True):
            length = number("Q")
            if keep:
                return stream.read(length).decode("utf-8")
            stream.seek(length, 1)

        def value(kind, keep=False):
            if kind == 8:
                return string(keep)
            if kind == 9:
                subtype, count = number("I"), number("Q")
                if subtype in sizes:
                    stream.seek(sizes[subtype] * count, 1)
                else:
                    for _ in range(count):
                        value(subtype)
                return None
            if keep:
                return number(formats[kind])
            stream.seek(sizes[kind], 1)

        assert stream.read(4) == b"GGUF", "Invalid GGUF magic"
        assert number("I") in (2, 3), "Unsupported GGUF version"
        number("Q")  # tensor count; tensor contents are never read
        count = number("Q")
        selected = {}
        for _ in range(count):
            key = string()
            kind = number("I")
            keep = key == "general.architecture" or "nextn" in key or key.endswith("block_count")
            result = value(kind, keep)
            if keep:
                selected[key] = result
        return selected


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runtime", type=Path, required=True)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--mtp", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--api-url")
    parser.add_argument("--api-key-file", type=Path)
    parser.add_argument("--template-checker", type=Path, required=True)
    parser.add_argument("--chat-template", type=Path, required=True)
    args = parser.parse_args()
    build = json.loads((args.runtime / "runtime.build.json").read_text(encoding="utf-8-sig"))
    source_commit = subprocess.check_output(["git", "-C", str(args.source), "rev-parse", "HEAD"], text=True).strip()
    assert source_commit == build["commit"]
    repository_root = Path(__file__).resolve().parents[1]
    expected_changed_paths = set()
    verified_patches = []
    for patch in build.get("patches", []):
        patch_path = repository_root / patch["path"]
        digest = hashlib.sha256(patch_path.read_bytes()).hexdigest()
        assert digest == patch["sha256"], f"Runtime patch hash mismatch: {patch['path']}"
        reverse = subprocess.run(["git", "-C", str(args.source), "apply", "--reverse", "--check", str(patch_path)], capture_output=True, text=True)
        assert reverse.returncode == 0, f"Runtime patch is not applied: {patch['path']}: {reverse.stderr}"
        for line in patch_path.read_text(encoding="utf-8").splitlines():
            if line.startswith("+++ b/"):
                expected_changed_paths.add(line[6:])
        verified_patches.append(dict(path=patch["path"], sha256=digest))
    subprocess.run(["git", "-C", str(args.source), "diff", "--check"], check=True)
    status = subprocess.check_output(["git", "-C", str(args.source), "status", "--porcelain"], text=True).splitlines()
    actual_changed_paths = {line[3:].replace("\\", "/") for line in status if len(line) > 3}
    assert actual_changed_paths == expected_changed_paths, "Runtime source contains changes outside the locked patch set"
    hashes = {}
    for filename in ("llama-server.exe", "llama-server-impl.dll", "llama-common.dll", "llama.dll"):
        digest = hashlib.sha256((args.runtime / filename).read_bytes()).hexdigest()
        assert digest == next(item["sha256"] for item in build["files"] if item["name"] == filename)
        hashes[filename] = digest
    mtp_before = args.mtp.stat()
    mtp_metadata = metadata(args.mtp)
    layers = [v for k, v in mtp_metadata.items() if k.endswith("nextn_predict_layers")]
    assert layers == [1], "Inspect multi-head clamp before exposing larger depths for another model"
    source = (args.source / "common" / "speculative.cpp").read_text(encoding="utf-8")
    assert "chain_heads   = n_mtp_layers > 1 && !is_mem_shared;" in source
    assert "this->params.n_max = std::min(this->params.n_max, n_mtp_layers);" in source
    results = []
    executable = args.runtime / "llama-server.exe"
    for depth in range(1, 7):
        for batch in (1024, 2048, 4096):
            for ubatch in (256, 512, 1024, 2048):
                if ubatch > batch:
                    continue
                for adaptive in (False, True):
                    if adaptive and depth < 2:
                        continue
                    flags = ["--device", "Vulkan0", "--fit", "off", "--load-mode", "mmap", "--ngram-on-disk", "--ngram-io-threads", "64", "--ngram-cache", "256", "--ngram-direct-io", "--no-context-shift", "--spec-type", "draft-mtp", "--spec-draft-n-max", str(depth), "--spec-draft-type-k", "q8_0", "--spec-draft-type-v", "q8_0", "-b", str(batch), "-ub", str(ubatch), "--chat-template-file", str(args.chat_template.resolve())]
                    if adaptive:
                        flags += ["--spec-draft-adaptive", "--spec-draft-n-min", "2"]
                    process = subprocess.run([str(executable), *flags, "--version"], capture_output=True, text=True, timeout=15, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
                    assert process.returncode == 0, process.stderr
                    results.append(dict(depth=depth, batch=batch, ubatch=ubatch, adaptive=adaptive, exitCode=process.returncode))
    invalid = subprocess.run([str(executable), "--spec-draft-n-max", "-1", "--version"], capture_output=True, text=True, timeout=15, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    assert invalid.returncode != 0, "--version must execute argument validation"
    environment = dict(os.environ)
    environment["PATH"] = str(args.runtime.resolve()) + os.pathsep + environment.get("PATH", "")
    rendered = subprocess.run([str(args.template_checker.resolve()), str(args.chat_template.resolve())], capture_output=True, text=True, timeout=30, env=environment, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    assert rendered.returncode == 0, rendered.stderr
    fixtures = json.loads(rendered.stdout)
    prefixes = []
    if args.api_url:
        assert args.api_key_file, "API key file is required with API URL"
        key = args.api_key_file.read_text(encoding="utf-8-sig").strip()

        def post(route, body):
            assert route in ("/apply-template", "/tokenize")
            request = urllib.request.Request(args.api_url.rstrip("/") + route, json.dumps(body).encode(), {"Content-Type": "application/json", "Authorization": "Bearer " + key})
            with urllib.request.urlopen(request, timeout=15) as response:
                return json.load(response)

        for fixture in fixtures:
            completed, following = fixture["completed"], fixture["following"]
            previous_tokens = post("/tokenize", dict(content=completed, add_special=True, parse_special=True))["tokens"]
            next_tokens = post("/tokenize", dict(content=following, add_special=True, parse_special=True))["tokens"]
            assert next_tokens[:len(previous_tokens)] == previous_tokens, "Tokenized prefix changed"
            prefixes.append(dict(thinking=fixture["thinking"], preservedPrefixTokens=len(previous_tokens)))
    mtp_after = args.mtp.stat()
    assert (mtp_before.st_size, mtp_before.st_mtime_ns) == (mtp_after.st_size, mtp_after.st_mtime_ns)
    report = dict(runtimeRepository=build["repository"], runtimeCommit=source_commit,
                  runtimeBuildManifestSha256=hashlib.sha256((args.runtime / "runtime.build.json").read_bytes()).hexdigest(),
                  runtimeExecutable=str(executable.resolve()), verifiedPatches=verified_patches,
                  verifiedBinaryHashes=hashes, mtpMetadata=mtp_metadata,
                  parserChecks=results, negativeDepthRejected=True, prefixChecks=prefixes,
                  templateSha256=hashlib.sha256(args.chat_template.read_bytes()).hexdigest(),
                  templateParserChecks=len(fixtures), inferenceRequests=0, benchmarksRun=0, modelFilesModified=False)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(f"PASS: {len(results)} argument combinations, {len(prefixes)} template/token prefix checks; no inference or benchmarks.")


if __name__ == "__main__":
    main()
