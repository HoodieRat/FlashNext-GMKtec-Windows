using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

string key = Environment.GetEnvironmentVariable("FLASHNEXT_API_KEY") ?? ReadLocalKey();
using HttpClient client = new() { BaseAddress = new Uri("http://127.0.0.1:8080"), Timeout = TimeSpan.FromMinutes(30) };
client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
var payload = new
{
    model = "Qwen3.8-Flash-Next",
    messages = new[] { new { role = "system", content = "You are a precise coding assistant." }, new { role = "user", content = "Write a safe C# file hashing method." } },
    stream = true,
    temperature = 0.2,
    top_p = 0.9,
    max_tokens = 1024,
    chat_template_kwargs = new { enable_thinking = true }
};
using HttpRequestMessage request = new(HttpMethod.Post, "/v1/chat/completions") { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };
using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
response.EnsureSuccessStatusCode();
await using Stream stream = await response.Content.ReadAsStreamAsync();
using StreamReader reader = new(stream, Encoding.UTF8);
while (await reader.ReadLineAsync() is string line)
{
    if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
    string data = line[5..].Trim();
    if (data == "[DONE]") break;
    if (data.Length == 0) continue;
    using JsonDocument document = JsonDocument.Parse(data);
    JsonElement choices = document.RootElement.GetProperty("choices");
    if (choices.GetArrayLength() == 0) continue;
    JsonElement delta = choices[0].GetProperty("delta");
    if (delta.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.String) Console.Write(content.GetString());
}
Console.WriteLine();

static string ReadLocalKey()
{
    string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlashNextManager", "api-key.txt");
    if (!File.Exists(path)) throw new FileNotFoundException("Start FlashNext Manager once to create local API security material.", path);
    string value = File.ReadAllText(path).Trim();
    if (value.Length < 32) throw new InvalidDataException("FlashNext API key file is invalid.");
    return value;
}
