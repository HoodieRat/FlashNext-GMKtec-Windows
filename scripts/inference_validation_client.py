#!/usr/bin/env python3
"""Exercise a running FlashNext llama-server with reproducible correctness workloads.

The report deliberately repeats the runtime identity beside every measured scenario so
saved numbers cannot be separated from the binary that produced them.
"""

from __future__ import annotations

import argparse
import ctypes
import hashlib
import json
import pathlib
import time
import urllib.error
import urllib.request
from typing import Any


def sha256_text(value: str) -> str:
    return hashlib.sha256(value.encode("utf-8")).hexdigest()


def read_json(path: pathlib.Path) -> dict[str, Any]:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError(f"Expected an object in {path}")
    return value


def write_json(path: pathlib.Path, value: Any) -> None:
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    temporary.replace(path)


class Client:
    def __init__(self, base_url: str, key: str) -> None:
        self.base_url = base_url.rstrip("/")
        self.headers = {"Authorization": f"Bearer {key}", "Content-Type": "application/json"}

    def post(self, path: str, body: dict[str, Any], timeout: int = 7200) -> tuple[dict[str, Any], float]:
        request = urllib.request.Request(
            self.base_url + path,
            data=json.dumps(body, separators=(",", ":")).encode("utf-8"),
            headers=self.headers,
            method="POST",
        )
        started = time.perf_counter()
        with urllib.request.urlopen(request, timeout=timeout) as response:
            value = json.loads(response.read().decode("utf-8"))
        elapsed_ms = (time.perf_counter() - started) * 1000.0
        if not isinstance(value, dict):
            raise ValueError(f"Unexpected response from {path}")
        return value, elapsed_ms

    def stream_chat(self, body: dict[str, Any], timeout: int = 7200) -> dict[str, Any]:
        body = dict(body)
        body["stream"] = True
        body["stream_options"] = {"include_usage": True}
        request = urllib.request.Request(
            self.base_url + "/v1/chat/completions",
            data=json.dumps(body, separators=(",", ":")).encode("utf-8"),
            headers=self.headers,
            method="POST",
        )
        started = time.perf_counter()
        first_token_ms: float | None = None
        content: list[str] = []
        reasoning: list[str] = []
        finish_reason: str | None = None
        usage: dict[str, Any] = {}
        timings: dict[str, Any] = {}
        with urllib.request.urlopen(request, timeout=timeout) as response:
            for raw_line in response:
                line = raw_line.decode("utf-8", errors="replace").strip()
                if not line.startswith("data:"):
                    continue
                payload = line[5:].strip()
                if not payload or payload == "[DONE]":
                    continue
                event = json.loads(payload)
                if isinstance(event.get("usage"), dict):
                    usage = event["usage"]
                if isinstance(event.get("timings"), dict):
                    timings = event["timings"]
                choices = event.get("choices") or []
                if not choices:
                    continue
                choice = choices[0]
                delta = choice.get("delta") or {}
                piece = delta.get("content")
                thought = delta.get("reasoning_content") or delta.get("reasoning")
                emitted = False
                if isinstance(piece, str) and piece:
                    content.append(piece)
                    emitted = True
                if isinstance(thought, str) and thought:
                    reasoning.append(thought)
                    emitted = True
                if delta.get("tool_calls"):
                    emitted = True
                if emitted and first_token_ms is None:
                    first_token_ms = (time.perf_counter() - started) * 1000.0
                if choice.get("finish_reason") is not None:
                    finish_reason = str(choice["finish_reason"])
        elapsed_ms = (time.perf_counter() - started) * 1000.0
        text = "".join(content)
        return {
            "timeToFirstTokenMs": first_token_ms,
            "wallTimeMs": elapsed_ms,
            "finishReason": finish_reason,
            "content": text,
            "reasoning": "".join(reasoning),
            "contentSha256": sha256_text(text),
            "usage": usage,
            "timings": timings,
        }

    def tokenize(self, content: str) -> int:
        value, _ = self.post("/tokenize", {"content": content, "add_special": False})
        tokens = value.get("tokens")
        if not isinstance(tokens, list):
            raise ValueError("/tokenize did not return a token list")
        return len(tokens)


def process_snapshot(pid: int) -> dict[str, Any]:
    if not hasattr(ctypes, "windll"):
        return {"available": False, "reason": "Windows process counters unavailable"}

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
    kernel32.OpenProcess.argtypes = [ctypes.c_ulong, ctypes.c_int, ctypes.c_ulong]
    kernel32.OpenProcess.restype = ctypes.c_void_p
    kernel32.CloseHandle.argtypes = [ctypes.c_void_p]
    kernel32.CloseHandle.restype = ctypes.c_int
    kernel32.GetProcessIoCounters.argtypes = [ctypes.c_void_p, ctypes.POINTER(IoCounters)]
    kernel32.GetProcessIoCounters.restype = ctypes.c_int
    psapi.GetProcessMemoryInfo.argtypes = [ctypes.c_void_p, ctypes.POINTER(MemoryCounters), ctypes.c_ulong]
    psapi.GetProcessMemoryInfo.restype = ctypes.c_int
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


def common_body(messages: list[dict[str, Any]], max_tokens: int) -> dict[str, Any]:
    return {
        "model": "Qwen3.8-Flash-Next",
        "messages": messages,
        "temperature": 0.0,
        "top_p": 1.0,
        "top_k": 1,
        "min_p": 0.0,
        "presence_penalty": 0.0,
        "repeat_penalty": 1.0,
        "seed": 24680,
        "max_tokens": max_tokens,
        "cache_prompt": True,
        "chat_template_kwargs": {"enable_thinking": False},
        "reasoning_effort": "none",
    }


def summarize_stream(value: dict[str, Any], runtime: dict[str, Any]) -> dict[str, Any]:
    return {
        "runtime": runtime,
        "timeToFirstTokenMs": value["timeToFirstTokenMs"],
        "wallTimeMs": value["wallTimeMs"],
        "finishReason": value["finishReason"],
        "contentSha256": value["contentSha256"],
        "usage": value["usage"],
        "timings": value["timings"],
    }


def extract_html(content: str) -> str:
    stripped = content.strip()
    if "```" in stripped:
        first = stripped.find("```")
        start = stripped.find("\n", first)
        end = stripped.rfind("```")
        if start >= 0 and end > start:
            stripped = stripped[start + 1 : end].strip()
    doctype = stripped.lower().find("<!doctype html")
    if doctype >= 0:
        stripped = stripped[doctype:]
    return stripped


def build_near_limit_prompt(client: Client, target_tokens: int) -> tuple[str, int]:
    sentence = "The ordinary archive row contains reversible payload data, a checksum, and no validation code. "
    begin = "BEGIN-CODE ALPHA-7319. "
    middle = " MIDDLE-CODE BRAVO-2846. "
    end = " END-CODE CHARLIE-9052. List the three validation codes in order and nothing else."
    sample_tokens = max(1, client.tokenize(sentence * 100))
    repeats = max(1, int(target_tokens * 100 / sample_tokens))
    for _ in range(4):
        left = sentence * (repeats // 2)
        right = sentence * (repeats - repeats // 2)
        prompt = begin + left + middle + right + end
        count = client.tokenize(prompt)
        delta = target_tokens - count
        if abs(delta) <= 256:
            return prompt, count
        repeats = max(1, int(repeats * target_tokens / max(1, count)))
    return prompt, count


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--base-url", required=True)
    parser.add_argument("--api-key-file", type=pathlib.Path, required=True)
    parser.add_argument("--runtime-directory", type=pathlib.Path, required=True)
    parser.add_argument("--model-directory", type=pathlib.Path, required=True)
    parser.add_argument("--server-pid", type=int, required=True)
    parser.add_argument("--output-directory", type=pathlib.Path, required=True)
    parser.add_argument("--near-limit-tokens", type=int, default=124000)
    parser.add_argument("--mtp-n-max", type=int, required=True)
    parser.add_argument("--mtp-p-min", type=float, default=0.75)
    parser.add_argument("--batch-size", type=int, required=True)
    parser.add_argument("--ubatch-size", type=int, required=True)
    args = parser.parse_args()

    args.output_directory.mkdir(parents=True, exist_ok=True)
    build_path = args.runtime_directory / "runtime.build.json"
    build = read_json(build_path)
    model_lock = read_json(args.model_directory / ".flashnext-model.lock.json")
    runtime = {
        "repository": build.get("repository"),
        "commit": build.get("commit"),
        "buildManifestSha256": hashlib.sha256(build_path.read_bytes()).hexdigest(),
        "executable": str(args.runtime_directory / "llama-server.exe"),
    }
    report_path = args.output_directory / "correctness-report.json"
    report: dict[str, Any] = {
        "timestampUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "runtime": runtime,
        "model": {"repository": model_lock.get("repository"), "revision": model_lock.get("revision")},
        "effectiveConfiguration": {
            "contextSize": 131072,
            "mtpNMax": args.mtp_n_max,
            "mtpPMin": args.mtp_p_min,
            "batchSize": args.batch_size,
            "ubatchSize": args.ubatch_size,
            "targetKv": "q8_0/q8_0",
            "draftKv": "q8_0/q8_0",
            "ple": "per_layer_token_embd.weight on CPU; mmap with lazy on-demand reads",
        },
        "memoryBefore": process_snapshot(args.server_pid),
        "scenarios": {},
    }
    write_json(report_path, report)
    key = args.api_key_file.read_text(encoding="utf-8").strip()
    client = Client(args.base_url, key)

    # A repeatable, mostly-cold prefill followed by the identical cached request.
    cache_corpus = " ".join(
        f"Ledger {index} committed checksum {index * 7919 % 104729} before acknowledgement."
        for index in range(1, 2401)
    )
    cache_messages = [
        {"role": "system", "content": "Answer deterministically and concisely."},
        {"role": "user", "content": cache_corpus + " What is the final ledger number?"},
    ]
    cache_body = common_body(cache_messages, 64)
    cold = client.stream_chat(cache_body)
    continuation_body = common_body(
        cache_messages
        + [
            {"role": "assistant", "content": cold["content"]},
            {"role": "user", "content": "Now state the first ledger number."},
        ],
        64,
    )
    cached_continuation = client.stream_chat(continuation_body)
    exact_repeat = client.stream_chat(cache_body)
    report["scenarios"]["promptCache"] = {
        "runtime": runtime,
        "cold": summarize_stream(cold, runtime),
        "cachedGrowingContinuation": summarize_stream(cached_continuation, runtime),
        "exactRepeat": summarize_stream(exact_repeat, runtime),
        "exactAnswerIdentical": cold["content"] == exact_repeat["content"],
        "passed": (
            "2400" in exact_repeat["content"]
            and cold["content"] == exact_repeat["content"]
            and bool(cached_continuation["content"].strip())
        ),
    }
    write_json(report_path, report)

    # Reproduce the upstream #28425 failure shape: identical requests whose
    # generated tail exceeds the bounded recurrent rollback planes. The patched
    # server must stay healthy and must not show unbounded process-memory growth.
    stress_messages = [
        {"role": "system", "content": "Reply with the requested phrase repeatedly."},
        {"role": "user", "content": "Write the phrase 'stable recurrent cache path' exactly four times, one per line."},
    ]
    stress_body = common_body(stress_messages, 32)
    # Prime the small-shape recurrent graphs before measuring retained memory.
    # Lazy PLE pages and Vulkan graph caches are expected first-use allocations,
    # so counting those as repeat-request growth produces a false leak signal.
    stress_warmups: list[dict[str, Any]] = []
    for run_number in range(1, 6):
        value, elapsed = client.post("/v1/chat/completions", stress_body)
        choice = (value.get("choices") or [{}])[0]
        text = str((choice.get("message") or {}).get("content") or "")
        stress_warmups.append(
            {
                "runtime": runtime,
                "runNumber": run_number,
                "wallTimeMs": elapsed,
                "contentSha256": sha256_text(text),
            }
        )
    stress_before = process_snapshot(args.server_pid)
    stress_runs: list[dict[str, Any]] = []
    for run_number in range(1, 21):
        value, elapsed = client.post("/v1/chat/completions", stress_body)
        choice = (value.get("choices") or [{}])[0]
        text = str((choice.get("message") or {}).get("content") or "")
        stress_runs.append(
            {
                "runtime": runtime,
                "runNumber": run_number,
                "wallTimeMs": elapsed,
                "finishReason": choice.get("finish_reason"),
                "contentSha256": sha256_text(text),
                "usage": value.get("usage") or {},
                "timings": value.get("timings") or {},
            }
        )
    stress_after = process_snapshot(args.server_pid)
    private_growth = None
    if stress_before.get("available") and stress_after.get("available"):
        private_growth = int(stress_after.get("privateBytes", 0)) - int(stress_before.get("privateBytes", 0))
    report["scenarios"]["recurrentCacheStress"] = {
        "runtime": runtime,
        "issue": "https://github.com/ggml-org/llama.cpp/issues/28425",
        "warmupRuns": stress_warmups,
        "runs": stress_runs,
        "memoryBefore": stress_before,
        "memoryAfter": stress_after,
        "privateMemoryGrowthBytes": private_growth,
        "allOutputsIdentical": len({run["contentSha256"] for run in stress_runs}) == 1,
        "passed": (
            len(stress_runs) == 20
            and len({run["contentSha256"] for run in stress_runs}) == 1
            and (private_growth is None or private_growth < 512 * 1024 * 1024)
        ),
    }
    write_json(report_path, report)

    # Force a real tool call and then continue the conversation using its result.
    tool_body = common_body(
        [
            {"role": "system", "content": "Use tools exactly when requested."},
            {"role": "user", "content": "Call write_text_file with path diagnostic.txt and content runtime-ok."},
        ],
        256,
    )
    tool_body["tools"] = [
        {
            "type": "function",
            "function": {
                "name": "write_text_file",
                "description": "Write exact text to a relative file path.",
                "parameters": {
                    "type": "object",
                    "properties": {"path": {"type": "string"}, "content": {"type": "string"}},
                    "required": ["path", "content"],
                    "additionalProperties": False,
                },
            },
        }
    ]
    tool_body["tool_choice"] = "required"
    tool_response, tool_elapsed = client.post("/v1/chat/completions", tool_body)
    message = ((tool_response.get("choices") or [{}])[0]).get("message") or {}
    tool_calls = message.get("tool_calls") or []
    tool_valid = False
    arguments: dict[str, Any] = {}
    if tool_calls:
        function = tool_calls[0].get("function") or {}
        try:
            arguments = json.loads(function.get("arguments") or "{}")
        except json.JSONDecodeError:
            arguments = {}
        tool_valid = (
            function.get("name") == "write_text_file"
            and arguments.get("path") == "diagnostic.txt"
            and arguments.get("content") == "runtime-ok"
        )
    continuation_valid = False
    continuation: dict[str, Any] = {}
    if tool_calls:
        continuation_body = common_body(
            tool_body["messages"]
            + [message, {"role": "tool", "tool_call_id": tool_calls[0].get("id"), "content": "written"}],
            128,
        )
        continuation = client.stream_chat(continuation_body)
        continuation_valid = bool(continuation["content"].strip())
    report["scenarios"]["toolConversation"] = {
        "runtime": runtime,
        "wallTimeMs": tool_elapsed,
        "toolCallValid": tool_valid,
        "arguments": arguments,
        "continuation": summarize_stream(continuation, runtime) if continuation else None,
        "passed": tool_valid and continuation_valid,
    }
    write_json(report_path, report)

    # Generate a complete artifact with enough behavior for browser-level testing.
    game_messages = [
        {
            "role": "system",
            "content": "You are an expert browser-game engineer. Return complete runnable artifacts without omissions.",
        },
        {
            "role": "user",
            "content": (
                "Create a polished, complete single-file HTML5 canvas game named Meteor Courier. "
                "It must run offline with no external assets; support keyboard movement, collision, score, lives, "
                "a start screen, game-over state, and an in-page restart button. Include all CSS and JavaScript. "
                "Keep the implementation lean, under 450 lines and under 6,000 tokens; omit commentary and code comments. "
                "Return only the raw HTML document, beginning with <!doctype html> and ending with </html>."
            ),
        },
    ]
    game = client.stream_chat(common_body(game_messages, 8192))
    html = extract_html(game["content"])
    game_path = args.output_directory / "meteor-courier.html"
    game_path.write_text(html, encoding="utf-8")
    lower = html.lower()
    structural = {
        "doctype": lower.startswith("<!doctype html"),
        "closingHtml": lower.rstrip().endswith("</html>"),
        "canvas": "<canvas" in lower,
        "script": "<script" in lower,
        "keyboard": "keydown" in lower or "keyup" in lower,
        "animation": "requestanimationframe" in lower,
        "restart": "restart" in lower,
    }
    report["scenarios"]["htmlGame"] = {
        "runtime": runtime,
        "benchmark": summarize_stream(game, runtime),
        "artifact": str(game_path),
        "bytes": game_path.stat().st_size,
        "structuralChecks": structural,
        "passedStructuralChecks": all(structural.values()),
        "browserFunctionalCheck": "pending",
    }
    write_json(report_path, report)

    # Exercise the configured window while retaining at least ~7k tokens for output.
    long_prompt, token_count = build_near_limit_prompt(client, args.near_limit_tokens)
    long_messages = [
        {"role": "system", "content": "Follow the final retrieval instruction exactly."},
        {"role": "user", "content": long_prompt},
    ]
    long_result = client.stream_chat(common_body(long_messages, 256))
    normalized = long_result["content"].upper()
    codes_found = [code for code in ("ALPHA-7319", "BRAVO-2846", "CHARLIE-9052") if code in normalized]
    report["scenarios"]["nearContextLimit"] = {
        "runtime": runtime,
        "userContentTokens": token_count,
        "configuredContextTokens": 131072,
        "reservedHeadroomTokens": 131072 - token_count,
        "benchmark": summarize_stream(long_result, runtime),
        "codesFound": codes_found,
        "passed": len(codes_found) == 3 and long_result["finishReason"] in ("stop", "length"),
    }
    report["memoryAfter"] = process_snapshot(args.server_pid)
    report["passed"] = all(
        bool(scenario.get("passed", scenario.get("passedStructuralChecks", False)))
        for scenario in report["scenarios"].values()
    )
    write_json(report_path, report)
    print(report_path)
    return 0 if report["passed"] else 2


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, urllib.error.URLError) as exc:
        raise SystemExit(f"validation failed: {exc}") from exc
