#!/usr/bin/env python3
"""Verify deterministic recurrent-cache reuse and post-warmup memory plateau."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any

from inference_validation_client import Client, process_snapshot, sha256_text, write_json


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--base-url", required=True)
    parser.add_argument("--server-pid", required=True, type=int)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--runtime-repository", required=True)
    parser.add_argument("--runtime-commit", required=True)
    parser.add_argument("--runtime-manifest-sha256", required=True)
    parser.add_argument("--runtime-executable", required=True)
    parser.add_argument("--warmup-runs", type=int, default=5)
    parser.add_argument("--measured-runs", type=int, default=40)
    args = parser.parse_args()

    runtime = {
        "repository": args.runtime_repository,
        "commit": args.runtime_commit,
        "buildManifestSha256": args.runtime_manifest_sha256,
        "executable": args.runtime_executable,
    }
    body: dict[str, Any] = {
        "model": "Qwen3.8-Flash-Next",
        "messages": [
            {"role": "system", "content": "Reply with the requested phrase repeatedly."},
            {"role": "user", "content": "Write the phrase 'stable recurrent cache path' exactly four times, one per line."},
        ],
        "max_tokens": 32,
        "temperature": 0,
        "seed": 42,
        "cache_prompt": True,
        "stream": False,
    }
    client = Client(args.base_url, "")

    warmups: list[dict[str, Any]] = []
    for run_number in range(1, args.warmup_runs + 1):
        value, elapsed = client.post("/v1/chat/completions", body)
        content = str((((value.get("choices") or [{}])[0]).get("message") or {}).get("content") or "")
        warmups.append({"runtime": runtime, "runNumber": run_number, "wallTimeMs": elapsed, "contentSha256": sha256_text(content)})

    snapshots = [{"afterMeasuredRun": 0, **process_snapshot(args.server_pid)}]
    runs: list[dict[str, Any]] = []
    midpoint = max(1, args.measured_runs // 2)
    for run_number in range(1, args.measured_runs + 1):
        value, elapsed = client.post("/v1/chat/completions", body)
        choice = (value.get("choices") or [{}])[0]
        content = str((choice.get("message") or {}).get("content") or "")
        runs.append(
            {
                "runtime": runtime,
                "runNumber": run_number,
                "wallTimeMs": elapsed,
                "finishReason": choice.get("finish_reason"),
                "contentSha256": sha256_text(content),
                "timings": value.get("timings") or {},
            }
        )
        if run_number in (midpoint, args.measured_runs):
            snapshots.append({"afterMeasuredRun": run_number, **process_snapshot(args.server_pid)})

    private = [int(item.get("privateBytes", 0)) for item in snapshots]
    first_half_growth = private[1] - private[0]
    second_half_growth = private[2] - private[1]
    all_hashes = {item["contentSha256"] for item in warmups + runs}
    report = {
        "runtime": runtime,
        "warmupRuns": warmups,
        "measuredRuns": runs,
        "memorySnapshots": snapshots,
        "firstHalfPrivateGrowthBytes": first_half_growth,
        "secondHalfPrivateGrowthBytes": second_half_growth,
        "allOutputsIdentical": len(all_hashes) == 1,
        "passed": len(runs) == args.measured_runs and len(all_hashes) == 1 and second_half_growth < 256 * 1024 * 1024,
    }
    write_json(args.output, report)
    print(json.dumps({"output": str(args.output), "passed": report["passed"], "firstHalfGrowth": first_half_growth, "secondHalfGrowth": second_half_growth}))
    return 0 if report["passed"] else 2


if __name__ == "__main__":
    raise SystemExit(main())
