#!/usr/bin/env python3
from __future__ import annotations
import json, os, pathlib, urllib.request

def api_key() -> str:
    value = os.environ.get("FLASHNEXT_API_KEY")
    if value: return value
    path = pathlib.Path(os.environ["LOCALAPPDATA"]) / "FlashNextManager" / "api-key.txt"
    value = path.read_text(encoding="utf-8").strip()
    if len(value) < 32: raise RuntimeError("FlashNext API key is invalid.")
    return value

payload = {
    "model": "Qwen3.8-Flash-Next",
    "messages": [
        {"role": "system", "content": "You are a precise coding assistant."},
        {"role": "user", "content": "Explain how to validate JSON safely."},
    ],
    "stream": True,
    "temperature": 0.2,
    "top_p": 0.9,
    "max_tokens": 1024,
    "chat_template_kwargs": {"enable_thinking": True},
}
request = urllib.request.Request(
    "http://127.0.0.1:8080/v1/chat/completions",
    data=json.dumps(payload).encode("utf-8"),
    headers={"Authorization": f"Bearer {api_key()}", "Content-Type": "application/json"},
    method="POST",
)
with urllib.request.urlopen(request, timeout=1800) as response:
    for raw in response:
        line = raw.decode("utf-8").strip()
        if not line.startswith("data:"): continue
        data = line[5:].strip()
        if data == "[DONE]": break
        if not data: continue
        event = json.loads(data)
        choices = event.get("choices", [])
        if not choices: continue
        content = choices[0].get("delta", {}).get("content")
        if content: print(content, end="", flush=True)
print()
