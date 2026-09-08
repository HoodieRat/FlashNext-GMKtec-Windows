# Operations guide

## Normal startup

Launch FlashNext Manager from the Start menu. Select `Start server and chat`. The manager verifies factory hardware and model files, starts the pinned server, waits for health, and opens chat.

## Server states

- `Stopped`: no managed or attached server.
- `Starting`: process exists but health is not ready.
- `Running`: healthy manager-owned server.
- `External`: healthy server discovered at the configured endpoint; it is not owned or stopped by the manager.
- `Stopping`: graceful shutdown and bounded process exit are in progress.
- `Faulted`: startup or runtime lifecycle failed; a classified message and redacted log are available.

## Metrics interpretation

Time to first token measures request start to first content or reasoning delta. Prompt and generation rates prefer runtime-reported timing fields. MTP values are counter deltas captured immediately around each request, not process-lifetime totals. A missing reliable GPU performance source is displayed as `N/A`.

## Settings

Profile changes that affect only request sampling apply to the next request. Context size, MTP n-max, host, and port are server-process settings and require restart. The manager calls out restart-sensitive changes.

## Installation diagnostics

Run `diagnose-install.cmd` after an installation failure. It produces one text report under `%LOCALAPPDATA%\FlashNextManager\diagnostics` containing WinGet/App Installer status, the durable install stage, structured dependency/build/deployment results, and relevant log tails. Revision 1.0.5 also records direct Vulkan probe JSON, process timeouts, and process-tree termination in the same diagnostics bundle. An earlier `%ProgramData%\FlashNextManager\logs\machine-install-*.log` is still detected so a failure from a pre-1.0.2 attempt is not hidden.

The normal installer log is mirrored by the deployment-only administrator helper. Native command failures include an output tail in their structured stage message.

## Runtime logs and support

Manager logs are structured JSONL with secret redaction. Server logs are redacted before storage. Support bundles include settings, hardware, immutable manifests, recent manager logs, sanitized server tails, and benchmark reports. Conversation files and raw request/response content are excluded.

## Runtime maintenance

`Verify current` hashes required executables and confirms pinned server features. `Build pinned runtime` compiles the exact lock and performs full staged smoke tests. `Roll back` validates the previous slot before swapping it into current.

## Model maintenance

`Verify` reads the installed immutable lock and hashes all six active files. `Download or repair` preserves verified files, quarantines invalid files, reacquires only missing/invalid content, verifies the complete staged set, and then promotes it.
