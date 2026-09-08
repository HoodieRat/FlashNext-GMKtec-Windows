# Security model

## Defaults

- Loopback-only binding at `127.0.0.1`.
- API-key authentication required for every API client.
- Built-in server web UI and agent mode disabled.
- Runtime CURL support disabled at compilation.
- Automatic runtime/model updates disabled.
- No model or runtime source is selected from a moving branch during installation.

## Secrets

The manager generates a cryptographically random API key. `secrets.json` and `api-key.txt` have inherited ACLs removed and grant access to the current user, SYSTEM, and local Administrators. The complete key is not printed by the manager. Logs redact registered values, bearer tokens, and common JSON key fields.

## Process safety

The server is launched directly with an argument vector. User-supplied prompts never become command-line arguments. Extra server arguments reject nulls and line breaks. A per-user mutex prevents competing managers, and a Job Object kills the manager-owned server if its owner disappears.

## Downloads

The model downloader permits only the official Hugging Face endpoint, exact repository, exact snapshot, exact six GGUF paths, and optional metadata named in the lock. Byte counts are resolved at the immutable revision. SHA-256 values are pinned in the repository. Error pages fail the GGUF magic check. Unverified staging data cannot replace active files.

## LAN mode

LAN mode requires explicit typed confirmation and an elevation prompt. The firewall rule is bound to the installed manager executable, exact TCP port, Private profile, and selected remote scope. CORS uses exact origins. API authentication remains enabled. Disabling LAN removes the rule and restores loopback settings.

## Diagnostics

Support bundles exclude conversations and raw request/response bodies. Server lines that may include prompt, content, response, or request material are filtered from the sanitized tail. The bundle is generated locally and is not uploaded automatically.
