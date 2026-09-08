using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

// Wait for GET http://127.0.0.1:8080/health before POST /v1/chat/completions.
// Sampling fields may be sent on each chat request with no restart.
// PATCH mtpNMax / contextSize / specType / port / extraArguments, then POST flashnext/server/restart.
string status = await FlashNextClient.ControlGetAsync("flashnext/status");
Console.WriteLine(status);
using JsonDocument patched = await FlashNextClient.PatchSettingsAsync("""{"profiles":{"coding-balanced":{"temperature":0.8}}}""");
Console.WriteLine("restartRequired " + patched.RootElement.GetProperty("restartRequired").GetBoolean());

public static class FlashNextClient
{
    public static string ApiKey()
    {
        string? env = Environment.GetEnvironmentVariable("FLASHNEXT_API_KEY");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlashNextManager", "api-key.txt");
        string value = File.ReadAllText(path).Trim();
        if (value.Length < 32) throw new InvalidDataException("FlashNext API key is invalid.");
        return value;
    }

    public static HttpClient InferenceClient(TimeSpan? timeout = null)
    {
        HttpClient client = new() { BaseAddress = new Uri("http://127.0.0.1:8080/"), Timeout = timeout ?? TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey());
        return client;
    }

    public static HttpClient ControlClient(TimeSpan? timeout = null)
    {
        HttpClient client = new() { BaseAddress = new Uri("http://127.0.0.1:18081/"), Timeout = timeout ?? TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey());
        return client;
    }

    public static async Task WaitUntilHealthyAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(900);
        DateTimeOffset deadline = DateTimeOffset.UtcNow + limit;
        Exception? last = null;
        using HttpClient client = InferenceClient(TimeSpan.FromSeconds(5));
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using HttpResponseMessage response = await client.GetAsync("/health", cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return;
                last = new HttpRequestException("Inference /health returned " + (int)response.StatusCode);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                last = ex;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException("FlashNext inference server was not healthy within " + limit + ". " + last?.Message);
    }

    public static async Task StartAndWaitAsync(CancellationToken cancellationToken = default)
    {
        using HttpClient control = ControlClient();
        using HttpResponseMessage start = await control.PostAsync("flashnext/server/start", null, cancellationToken).ConfigureAwait(false);
        start.EnsureSuccessStatusCode();
        await WaitUntilHealthyAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public static async Task<string> ControlGetAsync(string path, CancellationToken cancellationToken = default)
    {
        using HttpClient control = ControlClient();
        return await control.GetStringAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<JsonDocument> PatchSettingsAsync(string json, CancellationToken cancellationToken = default)
    {
        using HttpClient control = ControlClient();
        using HttpRequestMessage patch = new(HttpMethod.Patch, "flashnext/settings")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        using HttpResponseMessage response = await control.SendAsync(patch, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }
}
