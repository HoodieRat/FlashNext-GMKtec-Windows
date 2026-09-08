# Persistent prompt reports

Every new dashboard prompt captures its settings before sending. When a reply finishes, its full settings and performance report appears in the center **Run report** panel below the transcript. The panel restores the selected session's latest run.

Open **Reports** to search session names or prompt text. Select a session and run, sort the run columns, or choose **Open in chat** to find the reply. Browsing and rating do not switch the chat or change its draft, settings or startup session.

Ratings are **0 (unrated)** or **1–5** for output quality, and save immediately. A session's best run is its highest-rated completed run with answer text, then its highest generation tokens/second, then its latest completion. Missing speed sorts last within an equal rating. Unrated, stopped, failed, interrupted, token-limited and empty-answer runs remain inspectable but do not automatically win.

## Storage and accuracy

- Reports and reply links live in the existing `%LOCALAPPDATA%\FlashNextManager\conversations\*.json` files. They do not expire with the optional 30-day metrics logs.
- New runs with a retained user message do not duplicate its prompt in the run record. The Reports list/search resolves it from session history by run ID, including temporary sessions. The center panel and report details omit the user-prompt block; model answers/reasoning also remain in history, not in reports. The per-run system prompt, configuration and input hashes are unchanged.
- Legacy prompt fields remain readable and are not mass-migrated. They are used only when no linked user message is available. A failed/stopped attempt with no output still restores its draft and removes its unanswered turn from model history; it retains a prompt fallback for search because there is no transcript message to reference. This fallback is not printed in the report details.
- Sampling settings are captured at request start. Launch settings come from the running server's recorded configuration, never pending settings. Unverified external-server settings and identities remain unknown.
- Each newly launched managed server records its exact argument array (credentials redacted), GGML/LLAMA environment overrides, runtime commit and build-manifest hash, executable/DLL hashes, template path/hash, FA and K/V cache types, and every loaded model shard, MTP sidecar and projector path. Weight entries include size, modification time and pinned manifest hash; these are **not** a new content-hash verification of the large weights.
- Each request retains the exact parameters sent, the server's generation defaults, and returned effective generation settings (including sampler order and inherited penalties/defaults). Request-body, formatted-prompt and token-ID hashes distinguish equal-length but different inputs. The pinned runtime returns settings through `verbose` with `response_fields=["generation_settings"]`; prompt/answer debug echoes are excluded.
- A blank or `-1` seed stays random between prompts, but FlashNext resolves and saves the explicit per-request seed before sending. A specified seed is reused. Clearing the Seed field returns to random behavior without needing Save. Fixed seed `12345` is already used by the built-in benchmark; it does not change normal chat settings or guarantee identical Vulkan/MTP output across configurations.
- Benchmark JSON includes the same configuration/metrics report for each warm-up and measured run and is saved atomically after each result, including failures/stops. Its shared prompt and prompt hash are saved once at the file level, not repeated in each run, because benchmarks have no session transcript. The benchmark's runtime identity comes from the launched process, not the repository's desired runtime lock.
- New launch/file metadata requires an updated dashboard/manager **and a managed server restart**. An already-running older server or an external server remains explicitly incomplete; missing provenance is never backfilled from current settings. Failed/stopped requests may lack final server-returned settings but retain their captured request and launch snapshot.
- The report includes requested and effective output limits, actual context, omitted exchanges and model/runtime/template identities. The transcript remains in the session, linked by run ID; it is not copied into every report.
- Saves use the existing atomic writer. Rating updates reload the latest document under the same save lock, preserving newer replies, reports and session recency.
- An unfinished saved run is shown as **Interrupted** after reopening. Missing measurements show **N/A**. Legacy replies have no saved report; old logs are not guessed into reports.
- If conversation saving is disabled, new reports and ratings are temporary. The Reports tab says so. A save error retains the current report in memory and reports the failure; later normal session saves retry persistence.
- Optional metric-log failures cannot turn a completed reply into a failed run. Prompt/report metadata is excluded from inference requests, prompt-prefix calculations, metric logs and support bundles.
- Console load/save preserves reports and reply links; new capture and ratings are dashboard features.

## Verification

Focused core, dashboard and HTTP-stub integration tests cover immutable snapshots, pending versus active settings, multiple prompts, reopening, ratings and concurrent saves, ranking, interruptions, token limits, missing metrics, legacy conversations, disk failures and optional log failures. The WPF test exercises real bindings and renders the Reports tab. These tests do not run model completions or performance benchmarks.
