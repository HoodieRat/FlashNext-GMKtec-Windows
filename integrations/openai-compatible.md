# OpenAI-compatible integration

Base URL: `http://127.0.0.1:8080/v1`

Model: `Qwen3.8-Flash-Next`

Read the API key from `%LOCALAPPDATA%\FlashNextManager\api-key.txt`, or place it in `FLASHNEXT_API_KEY` for a child process. Do not copy it into source control.

## Chat request

```json
{
  "model": "Qwen3.8-Flash-Next",
  "messages": [
    { "role": "system", "content": "You are a precise coding assistant." },
    { "role": "user", "content": "Explain this function." }
  ],
  "stream": true,
  "temperature": 0.7,
  "top_p": 0.8,
  "top_k": 20,
  "max_tokens": 2048,
  "chat_template_kwargs": { "enable_thinking": false }
}
```

Endpoints commonly used by local applications:

- `GET /health`
- `GET /metrics`
- `GET /v1/models`
- `POST /v1/chat/completions`
- `POST /v1/completions`
- `POST /v1/embeddings`

The exact feature surface belongs to the pinned runtime commit. Integration clients should use normal HTTP timeouts of at least several minutes for large local generations and should support SSE when streaming.

**Wait for the model before chatting.** llama-server can take about a minute to become ready after Start. Poll `GET http://127.0.0.1:8080/health` (same bearer key) until it returns 200, then call `/v1/chat/completions`. Use `:18081` only to start/stop/PATCH. Helpers: `wait_until_healthy()` / `start_and_wait()` in `integrations/python/control.py`, and `FlashNextClient.WaitUntilHealthyAsync` / `StartAndWaitAsync` in `integrations/csharp/Control.cs`.

Completed dashboard answers are also streamed to `%LOCALAPPDATA%\FlashNextManager\last-answer.txt` while generating (answer body only).

## Control API (dashboard)

When FlashNext Dashboard is running, a loopback control API is available at `http://127.0.0.1:18081` using the same API key. It can start/stop the inference server and PATCH settings.

| Change | Where | Restart? |
|---|---|---|
| `temperature`, `top_p`, `top_k`, `max_tokens`, `chat_template_kwargs.enable_thinking` | `POST http://127.0.0.1:8080/v1/chat/completions` | No |
| Same sampling fields via PATCH on the active profile | `PATCH http://127.0.0.1:18081/flashnext/settings` | No — next `/v1` request |
| `mtpNMax`, `contextSize`, `server.port`, `server.specType`, `server.extraArguments` (including `--flash-attn on`) | `PATCH` then `POST /flashnext/server/restart` | Yes |

Factory speculation is MTP (`draft-mtp`). `ngram-simple` is experimental. `draft-dflash` is rejected.

- `GET /health`
- `GET /flashnext/status`
- `GET /flashnext/settings`
- `PATCH /flashnext/settings`
- `POST /flashnext/server/start`
- `POST /flashnext/server/stop`
- `POST /flashnext/server/restart`

Examples: `integrations/python/control.py` and `integrations/csharp/Control.cs`. AgentWorkbench contract (URLs, bearer, per-request defaults, MTP PATCH): `integrations/agentworkbench-flashnext.md`. Optional speed check when the server is already Running: `scripts/Measure-FlashAttn.ps1` (skips if `/health` is down; never starts the model).

Do not run the tray dashboard and the console manager as two live hosts at the same time.
