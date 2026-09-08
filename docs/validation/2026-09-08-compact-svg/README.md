# Compact SVG validation — September 8, 2026

## Result

One fresh, single-request session produced a complete, readable standalone SVG with MTP enabled and thinking disabled. It finished naturally with `finish_reason=stop`; no output repair or continuation was applied.

| Measurement | Result |
|---|---|
| Generated tokens | 1,425 |
| Prompt / cached prompt tokens | 256 / 0 |
| Generation speed (server decode timing) | 32.55 tok/s |
| Wall time / time to first token | 46.20 s / 2.45 s |
| MTP accepted / drafted tokens | 1,031 / 1,141 (90.36%) |
| Reasoning tokens / characters | 0 / 0 |
| Structural checks | Valid XML, closing SVG, 51 elements, 2 keyframes |

Browser inspection found a recognizable robot, readable labels, the tilted NO card, and no clipping or text overlap. Both CSS animations have matching uses; a complete animation cycle was not separately measured.

## Request and runtime

- [Exact request](request.json): practical artifact-focused system prompt; one modest SVG with explicit feature limits, a 2,500-token size target, and an 8,192-token ceiling.
- Thinking off; temperature 0.7, top-p 0.8, top-k 20, min-p 0, presence penalty 1.5, repeat penalty 1.0.
- Fixed MTP 6, draft p-min 0.75, Vulkan, Q8 KV, context 131,072, batch/ubatch 2,048/2,048.
- GMKtec EVO-X2, Ryzen AI Max+ 395 / Radeon 8060S, 128 GB RAM and 96 GB BIOS UMA.
- Runtime and model identities plus verified runtime file hashes are recorded in [metrics.json](metrics.json).
- [Generated SVG](output.svg) is unedited. Git newline normalization can change its on-disk hash; metrics retain the original captured-content hash.

The request contained no conversation history, set `cache_prompt=false`, and reported zero cached tokens. The server/model remained loaded; this was **not** a cold model load or a flush of OS/device caches. The request omitted the seed and did not capture its resolved value, so it is not a deterministic replay fixture. The Dashboard's normal run reports already capture per-request seeds; future comparable tests should use that capture or explicitly record the seed.

## What changed, and what this does not prove

The earlier ornate SVG looped through numbered keyframes until its 32,000-token cutoff. A later run with revised sampling stopped at the 8,192-token ceiling with coherent but incomplete SVG. This compact run changed both prompt scope and the output-size target, while retaining MTP and the 8,192 ceiling. It demonstrates that the current runtime can complete this bounded task. It does **not** isolate sampling, prompt complexity, or MTP as the sole cause of earlier failures.

High TPS and high MTP acceptance are not quality checks: repeated output can score well on both. This run's 32.55 tok/s is below the user's previously observed stable 36+ useful tok/s, and a different prompt cannot establish a like-for-like performance regression or recovery.

The passing request is not the repository's untouched `coding-balanced` profile. That profile defaults to thinking on, temperature 1.0, top-p 0.95, and presence penalty 0. Live user settings can differ again. This documentation/release update does not overwrite saved settings, change runtime/model pins, or turn MTP off.

## Next decision

Keep this one installed runtime/model setup and MTP enabled. First make request settings and budgeting explicit and consistent between the UI, effective request, and report. Then validate a small agreed set of useful tasks before making any production or sustained-speed claim. Do not proliferate setups based on this single result.
