using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlashNext.Core.Models;

namespace FlashNext.Core.Services;

public sealed class LlamaApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public LlamaApiClient(Uri baseUri, string apiKey, TimeSpan timeout, HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, true);
        _ownsClient = true;
        _http.BaseAddress = baseUri;
        _http.Timeout = timeout;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using HttpResponseMessage response = await _http.GetAsync("/health", cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
    }

    public async Task<string> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await _http.GetAsync("/v1/models", cancellationToken).ConfigureAwait(false);
        string content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return content;
    }

    public async Task<MetricSnapshot> GetMetricsAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await _http.GetAsync("/metrics", cancellationToken).ConfigureAwait(false);
        string content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return PrometheusParser.Parse(content);
    }

    public async Task<PreparedChat> PrepareChatAsync(IReadOnlyList<ChatMessage> messages, InferenceProfile profile, CancellationToken cancellationToken = default, string? imageDataUrl = null)
    {
        if (profile.MaxOutputTokens < 1) throw new ArgumentException("Max output tokens must be at least 1.");
        using HttpResponseMessage propsResponse = await _http.GetAsync("/props", cancellationToken).ConfigureAwait(false);
        propsResponse.EnsureSuccessStatusCode();
        using JsonDocument props = JsonDocument.Parse(await propsResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        // Use the running server's per-slot capacity, not settings awaiting a restart.
        int context = props.RootElement.GetProperty("default_generation_settings").GetProperty("n_ctx").GetInt32();
        const int headroom = 32;
        if (context <= headroom) throw new InvalidOperationException("The server reported an invalid context size.");

        List<ChatMessage> working = [.. messages];
        int omitted = 0;
        while (true)
        {
            using JsonDocument template = await PostJsonAsync("/apply-template", BuildChatRequest(working, profile, imageDataUrl), cancellationToken).ConfigureAwait(false);
            using JsonDocument tokens = await PostJsonAsync("/tokenize", new
            {
                content = template.RootElement.GetProperty("prompt"), add_special = true, parse_special = true
            }, cancellationToken).ConfigureAwait(false);
            int promptTokens = tokens.RootElement.GetProperty("tokens").GetArrayLength();
            int available = context - promptTokens - headroom;
            int[] turns = working.Select((message, index) => (message, index)).Where(item => item.message.Role == "user").Select(item => item.index).ToArray();
            if (available < profile.MaxOutputTokens && turns.Length > 2)
            {
                // Remove whole oldest exchanges; keep the latest exchange for requests such as "continue".
                working.RemoveRange(turns[0], turns[1] - turns[0]);
                omitted++;
                continue;
            }
            if (available < 1)
                throw new InvalidOperationException($"The latest exchange needs {promptTokens:N0} input tokens, which leaves no reply space in the running server's {context:N0}-token context. Increase Context size and Restart, or shorten the message/system prompt. Your draft has been kept; no message was truncated.");
            return new PreparedChat(working, promptTokens, context, Math.Min(profile.MaxOutputTokens, available), omitted)
            {
                ServerGenerationDefaults = props.RootElement.GetProperty("default_generation_settings").Clone(),
                FormattedPromptSha256 = HashService.Sha256Text(template.RootElement.GetProperty("prompt").GetString() ?? string.Empty),
                PromptTokenIdsSha256 = HashService.Sha256Text(JsonSerializer.Serialize(tokens.RootElement.GetProperty("tokens"), JsonOptions)),
                ServerTemplateSha256 = props.RootElement.TryGetProperty("chat_template", out JsonElement serverTemplate) && serverTemplate.ValueKind == JsonValueKind.String
                    ? HashService.Sha256Text(serverTemplate.GetString()!) : null
            };
        }
    }

    // Same serialization as the wire request. Store parameters and a full-body hash,
    // without copying the transcript or image payload into the report.
    public static (JsonElement Parameters, string Sha256) CaptureRequest(IReadOnlyList<ChatMessage> messages, InferenceProfile profile)
    {
        string json = JsonSerializer.Serialize(BuildChatRequest(messages, profile), JsonOptions);
        using JsonDocument document = JsonDocument.Parse(json);
        Dictionary<string, JsonElement> parameters = document.RootElement.EnumerateObject()
            .Where(property => property.Name != "messages").ToDictionary(property => property.Name, property => property.Value);
        return (JsonSerializer.SerializeToElement(parameters, JsonOptions), HashService.Sha256Text(json));
    }

    // Use fixed-order request types so semantically identical turns serialize identically.
    // Message content is always emitted before per-request sampling metadata.
    private static ChatCompletionRequest BuildChatRequest(IReadOnlyList<ChatMessage> messages, InferenceProfile profile, string? imageDataUrl = null)
    {
        int imageMessage = -1;
        if (!string.IsNullOrWhiteSpace(imageDataUrl))
        {
            for (int index = messages.Count - 1; index >= 0; index--)
            {
                if (messages[index].Role.Equals("user", StringComparison.OrdinalIgnoreCase))
                {
                    imageMessage = index;
                    break;
                }
            }
        }
        ChatRequestMessage[] requestMessages = messages.Select((message, index) =>
        {
            string? messageImage = message.ImageDataUrl ?? (index == imageMessage ? imageDataUrl : null);
            return new ChatRequestMessage
            {
                Role = message.Role,
                Content = messageImage is null
                    ? message.Content
                    : new object[]
                    {
                        new TextContentPart { Text = message.Content },
                        new ImageContentPart { ImageUrl = new ImageUrl { Url = messageImage } }
                    },
                ReasoningContent = message.ReasoningContent
            };
        }).ToArray();
        return new ChatCompletionRequest
        {
            Messages = requestMessages,
            Model = "Qwen3.8-Flash-Next",
            Stream = true,
            StreamOptions = new StreamOptions { IncludeUsage = true },
            Temperature = profile.Temperature,
            TopP = profile.TopP,
            TopK = profile.TopK,
            MinP = profile.MinP,
            PresencePenalty = profile.PresencePenalty,
            RepeatPenalty = profile.RepetitionPenalty,
            MaxTokens = profile.MaxOutputTokens,
            CachePrompt = true,
            IdSlot = 0,
            ChatTemplateKwargs = new ChatTemplateKwargs { EnableThinking = profile.Thinking },
            ReasoningEffort = profile.ReasoningEffort,
            Seed = profile.Seed
        };
    }

    private sealed class ChatCompletionRequest
    {
        [JsonPropertyOrder(0)] public required ChatRequestMessage[] Messages { get; init; }
        [JsonPropertyOrder(1)] public required string Model { get; init; }
        [JsonPropertyOrder(2)] public bool Stream { get; init; }
        [JsonPropertyOrder(3), JsonPropertyName("stream_options")] public required StreamOptions StreamOptions { get; init; }
        [JsonPropertyOrder(4)] public double Temperature { get; init; }
        [JsonPropertyOrder(5), JsonPropertyName("top_p")] public double TopP { get; init; }
        [JsonPropertyOrder(6), JsonPropertyName("top_k")] public int TopK { get; init; }
        [JsonPropertyOrder(7), JsonPropertyName("min_p")] public double MinP { get; init; }
        [JsonPropertyOrder(8), JsonPropertyName("presence_penalty")] public double PresencePenalty { get; init; }
        [JsonPropertyOrder(9), JsonPropertyName("repeat_penalty")] public double RepeatPenalty { get; init; }
        [JsonPropertyOrder(10), JsonPropertyName("max_tokens")] public int MaxTokens { get; init; }
        [JsonPropertyOrder(11), JsonPropertyName("cache_prompt")] public bool CachePrompt { get; init; }
        [JsonPropertyOrder(12), JsonPropertyName("chat_template_kwargs")] public required ChatTemplateKwargs ChatTemplateKwargs { get; init; }
        [JsonPropertyOrder(13), JsonPropertyName("reasoning_effort")] public required string ReasoningEffort { get; init; }
        [JsonPropertyOrder(14), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Seed { get; init; }
        // The managed server has one slot. Keep compatible requests on its live KV state.
        [JsonPropertyOrder(15), JsonPropertyName("id_slot")] public int IdSlot { get; init; }
        // The pinned runtime returns the resolved sampler settings on the final chunk.
        // Restrict debug output to settings so it does not echo the prompt/answer.
        [JsonPropertyOrder(16)] public bool Verbose { get; init; } = true;
        [JsonPropertyOrder(17), JsonPropertyName("response_fields")] public string[] ResponseFields { get; init; } = ["generation_settings"];
    }

    private sealed class ChatRequestMessage
    {
        [JsonPropertyOrder(0)] public required string Role { get; init; }
        [JsonPropertyOrder(1)] public required object Content { get; init; }
        [JsonPropertyOrder(2), JsonPropertyName("reasoning_content"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ReasoningContent { get; init; }
    }

    private sealed class StreamOptions
    {
        [JsonPropertyOrder(0), JsonPropertyName("include_usage")] public bool IncludeUsage { get; init; }
    }

    private sealed class ChatTemplateKwargs
    {
        [JsonPropertyOrder(0), JsonPropertyName("enable_thinking")] public bool EnableThinking { get; init; }
        // Removing old reasoning changes the rendered prefix on every following turn.
        [JsonPropertyOrder(1), JsonPropertyName("preserve_thinking")] public bool PreserveThinking { get; } = true;
    }

    private sealed class TextContentPart
    {
        [JsonPropertyOrder(0)] public string Type { get; } = "text";
        [JsonPropertyOrder(1)] public required string Text { get; init; }
    }

    private sealed class ImageContentPart
    {
        [JsonPropertyOrder(0)] public string Type { get; } = "image_url";
        [JsonPropertyOrder(1), JsonPropertyName("image_url")] public required ImageUrl ImageUrl { get; init; }
    }

    private sealed class ImageUrl
    {
        [JsonPropertyOrder(0)] public required string Url { get; init; }
    }

    private async Task<JsonDocument> PostJsonAsync(string path, object body, CancellationToken cancellationToken)
    {
        using StringContent content = new(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _http.PostAsync(path, content, cancellationToken).ConfigureAwait(false);
        string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw ApiError(response, json);
        return JsonDocument.Parse(json);
    }

    private static HttpRequestException ApiError(HttpResponseMessage response, string body)
    {
        string detail = response.ReasonPhrase ?? "Request failed";
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.Object)
            {
                if (error.TryGetProperty("message", out JsonElement message) && message.ValueKind == JsonValueKind.String)
                    detail = message.GetString() ?? detail;
                if (error.TryGetProperty("type", out JsonElement type) && type.ValueKind == JsonValueKind.String && type.GetString() == "exceed_context_size_error")
                    detail = $"The conversation exceeds the running server's context ({ReadInt(error, "n_prompt_tokens"):N0} input tokens / {ReadInt(error, "n_ctx"):N0} capacity). Increase Context size and Restart, or shorten the message. The transcript has been kept.";
            }
        }
        catch (JsonException) { }
        return new HttpRequestException($"HTTP {(int)response.StatusCode}: {detail}", null, response.StatusCode);
    }

    public async Task<ChatCompletionResult> StreamChatAsync(IReadOnlyList<ChatMessage> messages, InferenceProfile profile, Func<StreamChunk, Task> onChunk, CancellationToken cancellationToken = default, string? imageDataUrl = null)
    {
        string json = JsonSerializer.Serialize(BuildChatRequest(messages, profile, imageDataUrl), JsonOptions);
        using HttpRequestMessage message = new(HttpMethod.Post, "/v1/chat/completions") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        Stopwatch stopwatch = Stopwatch.StartNew();
        using HttpResponseMessage response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw ApiError(response, error);
        }

        ChatCompletionResult result = new();
        bool sawFirstToken = false;
        StringBuilder contentBuilder = new();
        StringBuilder reasoningBuilder = new();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using StreamReader reader = new(stream, Encoding.UTF8, true, 8192, false);
        while (true)
        {
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) break;
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            string payload = line[5..].Trim();
            if (payload.Length == 0) continue;
            if (payload == "[DONE]") break;
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            ExtractUsageAndTimings(root, result);
            if (root.TryGetProperty("choices", out JsonElement choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("finish_reason", out JsonElement finish) && finish.ValueKind == JsonValueKind.String)
                result.FinishReason = finish.GetString();
            string content = ExtractDelta(root, "content");
            string reasoning = ExtractDelta(root, "reasoning_content");
            if (!sawFirstToken && (!string.IsNullOrEmpty(content) || !string.IsNullOrEmpty(reasoning)))
            {
                sawFirstToken = true;
                result.TimeToFirstTokenMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            }
            if (content.Length > 0) contentBuilder.Append(content);
            if (reasoning.Length > 0) reasoningBuilder.Append(reasoning);
            await onChunk(new StreamChunk(content, reasoning, false, null)).ConfigureAwait(false);
            result.RawFinalPayload = root.Clone();
        }
        stopwatch.Stop();
        result.Content = contentBuilder.ToString();
        result.ReasoningContent = reasoningBuilder.ToString();
        result.TotalElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        if (!sawFirstToken) result.TimeToFirstTokenMilliseconds = result.TotalElapsedMilliseconds;
        await onChunk(new StreamChunk(string.Empty, string.Empty, true, result.RawFinalPayload)).ConfigureAwait(false);
        return result;
    }

    private static string ExtractDelta(JsonElement root, string property)
    {
        if (!root.TryGetProperty("choices", out JsonElement choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) return string.Empty;
        JsonElement choice = choices[0];
        if (!choice.TryGetProperty("delta", out JsonElement delta) || !delta.TryGetProperty(property, out JsonElement value)) return string.Empty;
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
    }

    private static void ExtractUsageAndTimings(JsonElement root, ChatCompletionResult result)
    {
        if (root.TryGetProperty("system_fingerprint", out JsonElement fingerprint) && fingerprint.ValueKind == JsonValueKind.String)
            result.ServerFingerprint = fingerprint.GetString();
        JsonElement settingsSource = root.TryGetProperty("__verbose", out JsonElement verbose) && verbose.ValueKind == JsonValueKind.Object ? verbose : root;
        if (settingsSource.TryGetProperty("generation_settings", out JsonElement generationSettings) && generationSettings.ValueKind == JsonValueKind.Object)
            result.EffectiveGenerationSettings = generationSettings.Clone();
        if (root.TryGetProperty("usage", out JsonElement usage) && usage.ValueKind == JsonValueKind.Object)
        {
            result.PromptTokens = ReadInt(usage, "prompt_tokens") ?? result.PromptTokens;
            result.TotalPromptTokens = ReadInt(usage, "prompt_tokens") ?? result.TotalPromptTokens;
            result.HasPromptUsage |= ReadInt(usage, "prompt_tokens").HasValue;
            result.CompletionTokens = ReadInt(usage, "completion_tokens") ?? result.CompletionTokens;
            result.TotalTokens = ReadInt(usage, "total_tokens") ?? (result.PromptTokens + result.CompletionTokens);
            if (usage.TryGetProperty("prompt_tokens_details", out JsonElement details)) result.CachedTokens = ReadInt(details, "cached_tokens") ?? result.CachedTokens;
            result.VisualTokens = ReadAnyInt(usage, "image_tokens", "visual_tokens", "vision_tokens") ?? result.VisualTokens;
            if (usage.TryGetProperty("prompt_tokens_details", out JsonElement visualDetails)) result.VisualTokens = ReadAnyInt(visualDetails, "image_tokens", "visual_tokens", "vision_tokens") ?? result.VisualTokens;
        }
        if (root.TryGetProperty("timings", out JsonElement timings) && timings.ValueKind == JsonValueKind.Object)
        {
            result.PromptTokensPerSecond = ReadDouble(timings, "prompt_per_second") ?? ReadDouble(timings, "prompt_tokens_per_second") ?? result.PromptTokensPerSecond;
            result.GenerationTokensPerSecond = ReadDouble(timings, "predicted_per_second") ?? ReadDouble(timings, "tokens_per_second") ?? result.GenerationTokensPerSecond;
            result.PromptMilliseconds = ReadDouble(timings, "prompt_ms") ?? result.PromptMilliseconds;
            result.PredictedMilliseconds = ReadDouble(timings, "predicted_ms") ?? result.PredictedMilliseconds;
            result.PromptTokens = ReadInt(timings, "prompt_n") ?? result.PromptTokens;
            result.EvaluatedPromptTokens = ReadInt(timings, "prompt_n") ?? result.EvaluatedPromptTokens;
            result.CompletionTokens = ReadInt(timings, "predicted_n") ?? result.CompletionTokens;
            result.CachedTokens = ReadInt(timings, "cache_n") ?? result.CachedTokens;
            result.DraftedTokens = ReadInt(timings, "draft_n") ?? result.DraftedTokens;
            result.AcceptedTokens = ReadInt(timings, "draft_n_accepted") ?? result.AcceptedTokens;
            result.VisualTokens = ReadAnyInt(timings, "image_tokens", "visual_tokens", "vision_tokens") ?? result.VisualTokens;
            result.TotalTokens = Math.Max(result.TotalTokens, result.PromptTokens + result.CompletionTokens);
        }
        if (!result.HasPromptUsage) result.TotalPromptTokens = result.PromptTokens + (result.EvaluatedPromptTokens.HasValue ? result.CachedTokens : 0);
    }

    private static int? ReadInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value)) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : null;
    }

    private static int? ReadAnyInt(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (ReadInt(element, name) is int value) return value;
        }
        return null;
    }

    private static double? ReadDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number)) return number;
        if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return number;
        return null;
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
