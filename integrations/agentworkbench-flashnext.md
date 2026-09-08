# AgentWorkbench ↔ FlashNext contract

AgentWorkbench (`C:\AgentWorkBenchWithJSONPlanning`) is a separate app. FlashNext is not LM Studio. Do not call LM Studio native routes against these ports.

## Operator workflow

1. Start FlashNext tray (`FlashNext.Dashboard`).
2. Click **Start**. Wait until the header says **Running** (model load can take about a minute).
3. Leave the tray running. Quitting it stops llama-server.
4. Other programs talk HTTP to the ports below.

Do not run the tray dashboard and the console manager as two live hosts at the same time. Do not load a second giant model while FlashNext is loaded. llama-server `--parallel` is **1**.

## Ports

| Role | URL |
|---|---|
| Inference | `http://127.0.0.1:8080` |
| Chat | `POST http://127.0.0.1:8080/v1/chat/completions` |
| Models | `GET http://127.0.0.1:8080/v1/models` |
| Inference health | `GET http://127.0.0.1:8080/health` |
| Control (tray must be running) | `http://127.0.0.1:18081` |
| Control health | `GET http://127.0.0.1:18081/health` |
| Status / settings | `GET /flashnext/status`, `GET /flashnext/settings` |
| Patch settings | `PATCH /flashnext/settings` |
| Lifecycle | `POST /flashnext/server/start`, `stop`, `restart` |

Poll `/health` on **:8080** until HTTP 200 before any chat. If inference is down and control is up, `POST /flashnext/server/start` then wait (900 s). If control is down, start the tray.

## Auth (both ports)

`Authorization: Bearer <key>`

Key file: `%LOCALAPPDATA%\FlashNextManager\api-key.txt` (UTF-8, trim). Optional: `FLASHNEXT_API_KEY`. Missing or wrong key → 401. Do not commit the key.

## Model id

Exact string: `Qwen3.8-Flash-Next`

Do not use LM Studio keys (for example `qwen3.6-35b-a3b-mtp`). Do not send `draft-dflash`. Allowlisted speculation: `draft-mtp` (factory) or `ngram-simple` (experimental).

## Per-request defaults (no restart)

Send these on every `POST /v1/chat/completions` unless the client UI overrode a slider. Request body wins over FlashNext’s saved `coding-balanced` profile (that profile has thinking on and temperature 1.0).

| Setting | JSON | Default |
|---|---|---|
| Reasoning | `chat_template_kwargs.enable_thinking` | `false` |
| Reasoning effort | `reasoning_effort` | `"none"` |
| Temperature | `temperature` | `0.7` |
| Top P | `top_p` | `0.8` |
| Top K | `top_k` | `20` |
| Min P | `min_p` | `0.0` |
| Presence penalty | `presence_penalty` | `1.0` |
| Repetition penalty | `repeat_penalty` | `1.0` |
| Max tokens | `max_tokens` | `32768` (context is 131072) |
| Prompt cache | `cache_prompt` | `true` |
| Model | `model` | `Qwen3.8-Flash-Next` |

```json
{
  "model": "Qwen3.8-Flash-Next",
  "messages": [
    { "role": "system", "content": "..." },
    { "role": "user", "content": "..." }
  ],
  "stream": false,
  "temperature": 0.7,
  "top_p": 0.8,
  "top_k": 20,
  "min_p": 0.0,
  "presence_penalty": 1.0,
  "repeat_penalty": 1.0,
  "max_tokens": 32768,
  "cache_prompt": true,
  "chat_template_kwargs": { "enable_thinking": false },
  "reasoning_effort": "none"
}
```

Thinking off is `enable_thinking: false` plus `reasoning_effort: "none"`. Do not rely on `/no_think` in the prompt.

Streaming: SSE `data: {json}` then `data: [DONE]`. Answer is `choices[0].delta.content`. If thinking is on, `choices[0].delta.reasoning_content` is not the answer.

Chat timeout: at least 30 minutes. Start/restart wait: 900 seconds.

## Restart required (PATCH then POST restart, then wait :8080/health)

| Setting | PATCH | Default |
|---|---|---|
| Speculation | `server.specType` | `draft-mtp` |
| MTP n-max | `profiles.coding-balanced.mtpNMax` | `6` (1–6) |
| Context | `profiles.coding-balanced.contextSize` | `131072` |
| Adaptive MTP | `server.extraArguments` | GET-merge-PATCH; replacing the array wipes other extras |

```json
{"profiles":{"coding-balanced":{"thinking":false,"reasoningEffort":"none","temperature":0.7,"topP":0.8,"topK":20,"minP":0.0,"presencePenalty":1.5,"repetitionPenalty":1.0,"maxOutputTokens":32768,"contextSize":131072,"mtpNMax":6}}}
```

```json
{"server":{"specType":"draft-mtp"}}
```

PATCH response includes `restartRequired`. Unknown keys return 400. Control API is loopback-only.

## Client helpers in this repo

- `integrations/python/control.py` — `wait_until_healthy()`, `start_and_wait()`, `request()`
- `integrations/csharp/Control.cs` — `FlashNextClient.WaitUntilHealthyAsync`, `StartAndWaitAsync`, `PatchSettingsAsync`

Do not add a project reference from AgentWorkbench to FlashNext; copy the HTTP pattern.
