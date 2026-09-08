# MTP controls, batches, and prefix reuse

Fixed 6 is the default for new factory profiles. Existing
saved choices are preserved. Safe-fallback retains its conservative depth 2.

| Setting | Choices | Runtime arguments |
| --- | --- | --- |
| MTP Off | Off | `--spec-type none`, no draft model/depth arguments |
| Fixed MTP | 1–6 | `--spec-type draft-mtp --spec-draft-n-max <depth>` |
| Adaptive MTP | Maximum 2–6, minimum 2 | Fixed arguments plus `--spec-draft-adaptive --spec-draft-n-min 2` |
| MTP Draft Confidence (Spec Draft P-Min) | 0.00 (Off), 0.50, 0.65, 0.75 (default), 0.80 | `--spec-draft-p-min <value>` for Fixed and Adaptive MTP |
| Batch | 1024, 2048, 4096 | `-b <batch>` |
| UBatch | 256, 512, 1024, 2048 | `-ub <ubatch>` |

All these controls require a server restart. MTP Draft Confidence stops extending
an MTP draft when draft-token confidence falls below the threshold. Higher values
can reduce wasted speculative work at deeper MTP settings. This server setting
(`server.specDraftPMin`) is independent of sampler Min-P. Missing settings default
to 0.75. The selected threshold is always explicitly passed, including with MTP
Off (where it has no effect). Active snapshots, performance reports, JSONL,
and CSV record the launched value, not edits awaiting restart. Older snapshots
show an unknown threshold instead of assuming a default.

UBatch must not exceed Batch: the
1024/2048 pairing is rejected instead of silently clamped. Defaults remain
Batch 2048 and UBatch 2048. Settings JSON, dashboard, console, and control API
accept the same supported values. `ubatchSize` is the canonical JSON spelling;
older saved `uBatchSize` spelling remains readable.

Telemetry records the running process's launch values, separately from saved
changes awaiting restart. Repeated saves do not clear a pending restart. The
dashboard, console metrics, control status, JSONL, and CSV include active MTP,
Batch, UBatch, runtime repository, runtime commit, runtime build-manifest hash, and
runtime executable. When attaching to an external server without verifiable launch
metadata, active values are unknown rather than inferred from saved settings.
CSV files with the previous header remain untouched; new rows use a `-v2.csv`
file on days containing the old schema (or the next free version if that file
also contains an older schema).

## Prompt and KV reuse

Requests retain historical assistant reasoning, image data, whitespace, message
order, and reasoning-only interrupted turns. UI status labels never enter the
prompt. Sampling settings remain outside message content. Requests explicitly
enable prompt caching, preserve thinking, and select the managed server's single
slot (0).

`config/flashnext-chat.jinja` is a separate application override of the bundled
Qwen3.8 chat template. It preserves assistant content and reasoning whitespace,
including reasoning newlines already returned by the runtime parser. The server
loads it with `--chat-template-file`; neither target nor MTP GGUF files change.
Other formatting, vision, tools, and system/user normalization remain unchanged.
Restart once to load this override.

The pinned runtime computes the longest common token prefix. On this hybrid
model it may need an earlier available recurrent-state checkpoint; a fully
cached prompt also requires at least one token evaluated to obtain logits.
Changing the system prompt, thinking instructions, historical content, or the
context window can legitimately shorten reuse. KV state is never moved across
a changed prefix. Telemetry distinguishes cached tokens from newly evaluated
tokens and does not mistake a checkpoint boundary for a token divergence.

## MTP request-state lifecycle

The hash-locked runtime patch resets the MTP target-hidden carry row whenever a
slot starts a full sequence at position zero, and includes that carry row in
prompt checkpoint save/restore. It also saves and restores the carry row with
the per-verification target/draft checkpoint, so a partially rejected draft is
replayed from the same hidden-state boundary as the recurrent tensors.
Speculative verification rolls back every accepted token after the first EOG
instead of leaving an accepted tail in the target, draft, or recurrent memories.

Qwen4exp's pooled QSA key cache must advance its valid-row watermark only by the
rows repooled in the current graph. Speculative draft cells can extend beyond
that graph's query maximum; treating those extra rows as valid makes later
attention consume stale pooled keys. The patch includes Laurent commit
`c659bd6d0c515e4f33f432139c71f3dfc19551de`'s compatible bounded-watermark fix
while retaining the repository's pinned base commit. A separate locked patch corrects
GDN normalization to the upstream reference formula.

## Verification without inference

Verified the installed Vulkan runtime at commit
`9a6f17ec8261712f755446f9ba1c2850ee0e5356`, with the hash-locked FlashNext
runtime patches recorded in `runtime.lock.json` and `runtime.build.json`.
Its parser accepts depths 5 and 6. The MTP GGUF
metadata reports one prediction layer; the runtime's multi-head-only depth clamp
does not apply to this model's iterative MTP path.

`scripts/verify_runtime_controls.py` checks all 121 supported fixed/adaptive
depth and Batch/UBatch combinations using `--version` after the flags. A negative
depth check verifies that validation actually ran. The model-free C++ checker
in `scripts/runtime-template-check` links against the bundled `llama-common.dll`
and checks parser/template replay with thinking on and off. Its rendered fixtures
can then be tokenized by the running server without submitting completions.

Evidence is saved under `artifacts/mtp-controls-verification`. No model loading,
completion requests, runtime replacement, or performance benchmarks are part of
these checks. Throughput, acceptance rates, memory use, and live KV-hit behavior
are left for manual testing.
