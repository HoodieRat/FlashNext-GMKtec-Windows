#!/usr/bin/env python3
"""Run a controlled llama-server generation benchmark with runtime provenance."""

from __future__ import annotations

import argparse
import ctypes
import hashlib
import json
import pathlib
import statistics
import time
import urllib.request
from typing import Any


def sha256_file(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def process_snapshot(pid: int) -> dict[str, Any]:
    class MemoryCounters(ctypes.Structure):
        _fields_ = [
            ("cb", ctypes.c_ulong),
            ("pageFaultCount", ctypes.c_ulong),
            ("peakWorkingSetBytes", ctypes.c_size_t),
            ("workingSetBytes", ctypes.c_size_t),
            ("quotaPeakPagedPoolBytes", ctypes.c_size_t),
            ("quotaPagedPoolBytes", ctypes.c_size_t),
            ("quotaPeakNonPagedPoolBytes", ctypes.c_size_t),
            ("quotaNonPagedPoolBytes", ctypes.c_size_t),
            ("pagefileBytes", ctypes.c_size_t),
            ("peakPagefileBytes", ctypes.c_size_t),
            ("privateBytes", ctypes.c_size_t),
        ]

    class IoCounters(ctypes.Structure):
        _fields_ = [
            ("readOperationCount", ctypes.c_ulonglong),
            ("writeOperationCount", ctypes.c_ulonglong),
            ("otherOperationCount", ctypes.c_ulonglong),
            ("readTransferBytes", ctypes.c_ulonglong),
            ("writeTransferBytes", ctypes.c_ulonglong),
            ("otherTransferBytes", ctypes.c_ulonglong),
        ]

    kernel32 = ctypes.windll.kernel32
    psapi = ctypes.windll.psapi
    handle = kernel32.OpenProcess(0x1000, False, pid)
    if not handle:
        return {"available": False, "reason": f"OpenProcess failed: {ctypes.get_last_error()}"}
    try:
        memory = MemoryCounters()
        memory.cb = ctypes.sizeof(memory)
        io = IoCounters()
        memory_ok = bool(psapi.GetProcessMemoryInfo(handle, ctypes.byref(memory), memory.cb))
        io_ok = bool(kernel32.GetProcessIoCounters(handle, ctypes.byref(io)))
        result: dict[str, Any] = {"available": memory_ok or io_ok}
        if memory_ok:
            for name, _ in memory._fields_[1:]:
                result[name] = int(getattr(memory, name))
        if io_ok:
            for name, _ in io._fields_:
                result[name] = int(getattr(io, name))
        return result
    finally:
        kernel32.CloseHandle(handle)


def post_json(url: str, body: dict[str, Any]) -> tuple[dict[str, Any], float]:
    request = urllib.request.Request(
        url,
        data=json.dumps(body, separators=(",", ":")).encode("utf-8"),
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    started = time.perf_counter()
    with urllib.request.urlopen(request, timeout=7200) as response:
        result = json.loads(response.read().decode("utf-8"))
    return result, time.perf_counter() - started


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--base-url", default="http://127.0.0.1:53183")
    parser.add_argument("--server-pid", type=int, required=True)
    parser.add_argument("--runtime-manifest", type=pathlib.Path, required=True)
    parser.add_argument("--executable", type=pathlib.Path, required=True)
    parser.add_argument("--request-json", type=pathlib.Path)
    parser.add_argument("--assistant-content-file", type=pathlib.Path)
    parser.add_argument("--followup-message")
    parser.add_argument("--output", type=pathlib.Path, required=True)
    parser.add_argument("--content-output", type=pathlib.Path)
    parser.add_argument("--measured-runs", type=int, default=3)
    parser.add_argument("--max-tokens", type=int, default=512)
    parser.add_argument("--context-tokens", type=int, default=65536)
    parser.add_argument("--batch-size", type=int, default=2048)
    parser.add_argument("--ubatch-size", type=int, default=2048)
    parser.add_argument("--load-mode", default="mmap")
    parser.add_argument("--lazy-mode", choices=("on", "off", "unsupported"), default="on")
    # These describe the already-running server and are written into provenance;
    # this revision does not support changing speculative settings per request.
    parser.add_argument("--mtp-n-max", type=int, default=7, help="effective server launch value")
    parser.add_argument("--mtp-n-min", type=int, default=0, help="effective server launch value")
    parser.add_argument("--mtp-p-min", type=float, default=0.75, help="effective server launch value")
    parser.add_argument(
        "--mtp-mode", choices=("adaptive", "fixed", "off"), default="adaptive", help="effective server launch mode"
    )
    parser.add_argument("--workload", choices=("cache", "html-game"), default="cache")
    args = parser.parse_args()

    request_template: dict[str, Any] | None = None
    if args.request_json:
        request_template = json.loads(args.request_json.read_text(encoding="utf-8"))

    manifest = json.loads(args.runtime_manifest.read_text(encoding="utf-8"))
    manifest_runtime = manifest.get("runtime", manifest)
    runtime = {
        "repository": manifest_runtime["repository"],
        "commit": manifest_runtime["commit"],
        "sourceTree": manifest_runtime.get("tree", manifest_runtime.get("sourceTree")),
        "runtimeManifest": str(args.runtime_manifest.resolve()),
        "runtimeManifestSha256": sha256_file(args.runtime_manifest),
        "executable": str(args.executable.resolve()),
        "executableSha256": sha256_file(args.executable),
    }
    configuration = {
        "contextTokens": args.context_tokens,
        "batchSize": args.batch_size,
        "ubatchSize": args.ubatch_size,
        "slots": 1,
        "flashAttention": True,
        "targetKv": "q8_0/q8_0",
        "draftKv": "q8_0/q8_0",
        "mtp": {
            "mode": args.mtp_mode,
            "min": args.mtp_n_min if args.mtp_mode != "off" else None,
            "max": args.mtp_n_max if args.mtp_mode != "off" else None,
            "pMin": args.mtp_p_min if args.mtp_mode != "off" else None,
        },
        "ple": {
            "tensorOverride": "per_layer_token_embd\\.weight=CPU",
            "loadMode": args.load_mode,
            "lazyMode": args.lazy_mode,
        },
        "sampling": {
            "temperature": 0.0,
            "topP": 1.0,
            "topK": 1,
            "minP": 0.0,
            "repeatPenalty": 1.0,
            "seed": 12345,
            "cachePrompt": False,
        },
        "workload": args.workload,
    }
    if request_template is not None:
        configuration["workload"] = f"request-json:{args.request_json.name}"
        configuration["sampling"] = {
            "temperature": request_template.get("temperature"),
            "topP": request_template.get("top_p"),
            "topK": request_template.get("top_k"),
            "minP": request_template.get("min_p"),
            "presencePenalty": request_template.get("presence_penalty"),
            "repeatPenalty": request_template.get("repeat_penalty"),
            "dryMultiplier": request_template.get("dry_multiplier"),
            "seed": request_template.get("seed"),
            "cachePrompt": request_template.get("cache_prompt", False),
        }
    if args.workload == "html-game":
        messages = [
            {
                "role": "system",
                "content": (
                    "You are a deterministic browser-game engineer. Output only raw HTML, CSS, and JavaScript. "
                    "Continue adding required implementation details until the output limit is reached."
                ),
            },
            {
                "role": "user",
                "content": (
                    "Create a complete single-file HTML5 canvas game named Meteor Courier. It must run offline with "
                    "no external assets; support keyboard movement, collision, score, lives, a start screen, game-over "
                    "state, and an in-page restart button. Include all CSS and JavaScript, begin with <!doctype html>, "
                    "and use the full output budget."
                ),
            },
        ]
    else:
        messages = [
            {
                "role": "system",
                "content": (
                    "You are a deterministic C# systems programmer. Output only compilable C# source. "
                    "Continue adding required implementation details until the output limit is reached."
                ),
            },
            {
                "role": "user",
                "content": (
                    "Implement a production-quality bounded asynchronous memoization cache named AsyncMemoCache<TKey,TValue>. "
                    "It must coalesce concurrent factory calls per key, support cancellation without cancelling shared work, "
                    "use monotonic TTL expiration, perform deterministic LRU eviction, dispose replaced IDisposable values, "
                    "avoid callbacks while holding locks, expose TryRemove and Clear, and include concise XML documentation. "
                    "Return one self-contained source file and use the full output budget."
                ),
            },
        ]

    if request_template is not None:
        messages = request_template["messages"]
        if args.assistant_content_file:
            if not args.followup_message:
                parser.error("--followup-message is required with --assistant-content-file")
            messages = list(messages) + [
                {"role": "assistant", "content": args.assistant_content_file.read_text(encoding="utf-8")},
                {"role": "user", "content": args.followup_message},
            ]

    def body(max_tokens: int) -> dict[str, Any]:
        if request_template is not None:
            result = dict(request_template)
            result["messages"] = messages
            result["max_tokens"] = max_tokens
            return result
        return {
            "model": "Qwen3.8-Flash-Next",
            "messages": messages,
            "temperature": 0.0,
            "top_p": 1.0,
            "top_k": 1,
            "min_p": 0.0,
            "presence_penalty": 0.0,
            "repeat_penalty": 1.0,
            "seed": 12345,
            "max_tokens": max_tokens,
            "cache_prompt": False,
            "chat_template_kwargs": {"enable_thinking": False},
            "reasoning_effort": "none",
        }

    output: dict[str, Any] = {
        "timestampUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "runtime": runtime,
        "configuration": configuration,
        "memoryBefore": process_snapshot(args.server_pid),
        "warmup": None,
        "runs": [],
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    if args.content_output:
        args.content_output.parent.mkdir(parents=True, exist_ok=True)

    warmup_response, warmup_wall = post_json(args.base_url.rstrip("/") + "/v1/chat/completions", body(128))
    warmup_choice = (warmup_response.get("choices") or [{}])[0]
    output["warmup"] = {
        "runtime": runtime,
        "benchmark": {
            "requestedTokens": 128,
            "wallSeconds": warmup_wall,
            "finishReason": warmup_choice.get("finish_reason"),
            "usage": warmup_response.get("usage", {}),
            "timings": warmup_response.get("timings", {}),
        },
    }

    for run_number in range(1, args.measured_runs + 1):
        memory_before = process_snapshot(args.server_pid)
        response, wall = post_json(args.base_url.rstrip("/") + "/v1/chat/completions", body(args.max_tokens))
        memory_after = process_snapshot(args.server_pid)
        choice = (response.get("choices") or [{}])[0]
        content = str((choice.get("message") or {}).get("content") or "")
        record = {
            "runtime": runtime,
            "benchmark": {
                "runNumber": run_number,
                "requestedTokens": args.max_tokens,
                "wallSeconds": wall,
                "finishReason": choice.get("finish_reason"),
                "contentSha256": hashlib.sha256(content.encode("utf-8")).hexdigest(),
                "contentCharacters": len(content),
                "usage": response.get("usage", {}),
                "timings": response.get("timings", {}),
                "memoryBefore": memory_before,
                "memoryAfter": memory_after,
            },
        }
        output["runs"].append(record)
        if args.content_output:
            content_path = args.content_output.with_name(
                f"{args.content_output.stem}.run-{run_number}{args.content_output.suffix or '.txt'}"
            )
            content_path.write_text(content, encoding="utf-8")
            record["benchmark"]["contentPath"] = str(content_path.resolve())
        args.output.write_text(json.dumps(output, indent=2) + "\n", encoding="utf-8")

    rates = [float(item["benchmark"]["timings"]["predicted_per_second"]) for item in output["runs"]]
    completions = [int(item["benchmark"]["usage"].get("completion_tokens", 0)) for item in output["runs"]]
    hashes = {item["benchmark"]["contentSha256"] for item in output["runs"]}
    output["memoryAfter"] = process_snapshot(args.server_pid)
    output["summary"] = {
        "runtime": runtime,
        "benchmark": {
            "generationTokensPerSecond": rates,
            "medianGenerationTokensPerSecond": statistics.median(rates),
            "minimumGenerationTokensPerSecond": min(rates),
            "completionTokens": completions,
            "identicalOutput": len(hashes) == 1,
            "passesTokenCount": all(value >= args.max_tokens for value in completions),
            "passesRepeatability": len(hashes) == 1,
            "passesSpeedGate": statistics.median(rates) >= 40.0,
        },
    }
    output["passed"] = all(
        (
            output["summary"]["benchmark"]["passesTokenCount"],
            output["summary"]["benchmark"]["passesRepeatability"],
            output["summary"]["benchmark"]["passesSpeedGate"],
        )
    )
    args.output.write_text(json.dumps(output, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(output["summary"], indent=2))
    return 0 if output["passed"] else 2


if __name__ == "__main__":
    raise SystemExit(main())
