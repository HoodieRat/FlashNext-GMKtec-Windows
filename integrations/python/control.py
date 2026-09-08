#!/usr/bin/env python3
from __future__ import annotations
import json, os, pathlib, time, urllib.error, urllib.request

INFERENCE = "http://127.0.0.1:8080"
CONTROL = "http://127.0.0.1:18081"


def api_key() -> str:
    value = os.environ.get("FLASHNEXT_API_KEY")
    if value:
        return value
    path = pathlib.Path(os.environ["LOCALAPPDATA"]) / "FlashNextManager" / "api-key.txt"
    value = path.read_text(encoding="utf-8").strip()
    if len(value) < 32:
        raise RuntimeError("FlashNext API key is invalid.")
    return value


def request(method: str, path: str, payload: dict | None = None, timeout: float = 120, base: str = CONTROL) -> dict | str:
    data = None if payload is None else json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(
        base + path,
        data=data,
        headers={"Authorization": f"Bearer {api_key()}", "Content-Type": "application/json"},
        method=method,
    )
    with urllib.request.urlopen(req, timeout=timeout) as response:
        body = response.read().decode("utf-8")
        if not body:
            return {}
        try:
            return json.loads(body)
        except json.JSONDecodeError:
            return body


def wait_until_healthy(timeout_seconds: float = 900, poll_seconds: float = 2) -> None:
    """Poll GET http://127.0.0.1:8080/health until llama-server is ready. Does not start the model."""
    deadline = time.time() + timeout_seconds
    last_error = "inference /health was never reached"
    while time.time() < deadline:
        try:
            request("GET", "/health", timeout=min(5, timeout_seconds), base=INFERENCE)
            return
        except (urllib.error.URLError, urllib.error.HTTPError, TimeoutError, OSError) as ex:
            last_error = str(ex)
            time.sleep(poll_seconds)
    raise TimeoutError(f"FlashNext inference server was not healthy within {timeout_seconds:.0f}s: {last_error}")


def start_and_wait(timeout_seconds: float = 900) -> dict | str:
    """POST control start, then wait until :8080/health succeeds."""
    started = request("POST", "/flashnext/server/start", timeout=timeout_seconds)
    wait_until_healthy(timeout_seconds=timeout_seconds)
    return started


if __name__ == "__main__":
    # Sampling (temperature, thinking, max_tokens) can also be sent on each
    # POST http://127.0.0.1:8080/v1/chat/completions with no restart.
    # PATCH mtpNMax / contextSize / specType / port then POST /flashnext/server/restart.
    status = request("GET", "/flashnext/status")
    print(status)
    patched = request("PATCH", "/flashnext/settings", {"profiles": {"coding-balanced": {"temperature": 0.8}}})
    print("restartRequired", patched.get("restartRequired") if isinstance(patched, dict) else patched)
