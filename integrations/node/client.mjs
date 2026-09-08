import fs from "node:fs";
import os from "node:os";
import path from "node:path";

function apiKey() {
  if (process.env.FLASHNEXT_API_KEY) return process.env.FLASHNEXT_API_KEY;
  const keyPath = path.join(process.env.LOCALAPPDATA ?? path.join(os.homedir(), "AppData", "Local"), "FlashNextManager", "api-key.txt");
  const value = fs.readFileSync(keyPath, "utf8").trim();
  if (value.length < 32) throw new Error("FlashNext API key is invalid.");
  return value;
}

const response = await fetch("http://127.0.0.1:8080/v1/chat/completions", {
  method: "POST",
  headers: { Authorization: `Bearer ${apiKey()}`, "Content-Type": "application/json" },
  body: JSON.stringify({
    model: "Qwen3.8-Flash-Next",
    messages: [
      { role: "system", content: "You are a precise coding assistant." },
      { role: "user", content: "Show a robust JavaScript retry strategy." }
    ],
    stream: true,
    temperature: 0.2,
    top_p: 0.9,
    max_tokens: 1024,
    chat_template_kwargs: { enable_thinking: true }
  })
});
if (!response.ok || !response.body) throw new Error(`FlashNext request failed: ${response.status} ${await response.text()}`);
const reader = response.body.getReader();
const decoder = new TextDecoder();
let buffer = "";
for (;;) {
  const { value, done } = await reader.read();
  buffer += decoder.decode(value ?? new Uint8Array(), { stream: !done });
  const lines = buffer.split(/\r?\n/);
  buffer = lines.pop() ?? "";
  for (const line of lines) {
    if (!line.startsWith("data:")) continue;
    const data = line.slice(5).trim();
    if (!data || data === "[DONE]") continue;
    const event = JSON.parse(data);
    const content = event.choices?.[0]?.delta?.content;
    if (content) process.stdout.write(content);
  }
  if (done) break;
}
process.stdout.write("\n");
