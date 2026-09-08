using System.Net;
using System.Text.Json;
using FlashNext.Core.Models;
using FlashNext.Core.Services;

namespace FlashNext.IntegrationTests;

public sealed class LlamaApiClientTests
{
    [Fact]
    public async Task RepeatedRequestsAndSavedHistoryPreservePrefixFieldsAcrossSamplingChanges()
    {
        FakeHttpHandler handler = new(_ => FakeHttpHandler.Text(HttpStatusCode.OK, "data: [DONE]\n\n", "text/event-stream"));
        using LlamaApiClient client = new(new Uri("http://localhost"), "key", TimeSpan.FromSeconds(10), handler);
        ChatMessage[] history = [new("system", "stable rules"), new("user", "question"), new("assistant", "answer\n") { ReasoningContent = "reasoning\n\n", Status = "Stopped", RunId = "private-report-id" }, new("user", "follow up")];
        InferenceProfile profile = new() { Thinking = true, ReasoningEffort = "medium" };
        await client.StreamChatAsync(history, profile, _ => Task.CompletedTask);
        using JsonDocument first = JsonDocument.Parse(handler.LastBody!);
        profile.Temperature = 0.2;
        profile.MaxOutputTokens = 17;
        await client.StreamChatAsync(history, profile, _ => Task.CompletedTask);
        using JsonDocument next = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal(first.RootElement.GetProperty("messages").GetRawText(), next.RootElement.GetProperty("messages").GetRawText());
        Assert.Equal("answer\n", next.RootElement.GetProperty("messages")[2].GetProperty("content").GetString());
        Assert.Equal("reasoning\n\n", next.RootElement.GetProperty("messages")[2].GetProperty("reasoning_content").GetString());
        Assert.True(next.RootElement.GetProperty("chat_template_kwargs").GetProperty("preserve_thinking").GetBoolean());
        Assert.Equal(0, next.RootElement.GetProperty("id_slot").GetInt32());
        Assert.DoesNotContain("Stopped", handler.LastBody);
        Assert.DoesNotContain("private-report-id", handler.LastBody);
        Assert.DoesNotContain("runId", handler.LastBody);
    }

    [Fact]
    public async Task TimingsOnlyStreamSeparatesCachedAndEvaluatedTokens()
    {
        const string sse = "data: {\"timings\":{\"prompt_n\":0,\"cache_n\":90}}\n\ndata: {\"timings\":{\"prompt_n\":10,\"cache_n\":90}}\n\ndata: [DONE]\n\n";
        FakeHttpHandler handler = new(_ => FakeHttpHandler.Text(HttpStatusCode.OK, sse, "text/event-stream"));
        using LlamaApiClient client = new(new Uri("http://localhost"), "key", TimeSpan.FromSeconds(10), handler);
        ChatCompletionResult result = await client.StreamChatAsync([new("user", "test")], new(), _ => Task.CompletedTask);
        Assert.Equal(100, result.TotalPromptTokens);
        Assert.Equal(10, result.EvaluatedPromptTokens);
        Assert.Equal(90, result.CachedTokens);
    }

    [Fact]
    public async Task StreamsUnicodeContentAndReadsFinalUsageAndTimings()
    {
        string sse = "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"Think → \"}}]}\n\n" +
                     "data: {\"choices\":[{\"delta\":{\"content\":\"Hello 世界\"}}]}\n\n" +
                     "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"length\"}]}\n\n" +
                     "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2,\"total_tokens\":12,\"prompt_tokens_details\":{\"cached_tokens\":4}},\"timings\":{\"prompt_per_second\":100.0,\"predicted_per_second\":25.0}}\n\n" +
                     "data: [DONE]\n\n";
        FakeHttpHandler handler = new(_ => FakeHttpHandler.Text(HttpStatusCode.OK, sse, "text/event-stream"));
        using LlamaApiClient client = new(new Uri("http://127.0.0.1:8080"), "test-key", TimeSpan.FromSeconds(10), handler);
        List<string> chunks = [];
        ChatCompletionResult result = await client.StreamChatAsync([new ChatMessage("user", "hi")], new InferenceProfile { MaxOutputTokens = 10 }, chunk => { chunks.Add(chunk.Content + chunk.ReasoningContent); return Task.CompletedTask; });
        Assert.Equal("Hello 世界", result.Content);
        Assert.Equal("Think → ", result.ReasoningContent);
        Assert.Equal("length", result.FinishReason);
        Assert.Equal(10, result.PromptTokens);
        Assert.Equal(4, result.CachedTokens);
        Assert.Equal(2, result.CompletionTokens);
        Assert.Equal(25, result.GenerationTokensPerSecond);
        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("test-key", handler.LastRequest.Headers.Authorization.Parameter);
        using JsonDocument request = JsonDocument.Parse(handler.LastBody!);
        Assert.True(request.RootElement.GetProperty("cache_prompt").GetBoolean());
    }

    [Fact]
    public async Task HealthReturnsFalseForConnectionStyleFailure()
    {
        FakeHttpHandler handler = new(_ => throw new HttpRequestException("offline"));
        using LlamaApiClient client = new(new Uri("http://127.0.0.1:8080"), "key", TimeSpan.FromSeconds(1), handler);
        Assert.False(await client.IsHealthyAsync());
    }

    [Fact]
    public async Task BudgetsUsingRunningContextAndRemovesOnlyWholeOldestExchanges()
    {
        ChatMessage[] history = [new("system", "rules"), new("user", "old task"), new("assistant", "old answer"),
            new("user", "write game"), new("assistant", "game code"), new("user", "continue")];
        ContextHandler handler = new(8192, messages => messages.Length == 6 ? 8208 : 3600);
        using LlamaApiClient client = new(new Uri("http://localhost"), "key", TimeSpan.FromSeconds(10), handler);
        InferenceProfile profile = new() { MaxOutputTokens = 4096, ContextSize = 65536, Thinking = true, ReasoningEffort = "high" };
        PreparedChat prepared = await client.PrepareChatAsync(history, profile);
        Assert.Equal(8192, prepared.ContextSize);
        Assert.Equal(3600, prepared.PromptTokens);
        Assert.Equal(4096, prepared.MaxOutputTokens);
        Assert.Equal(1, prepared.OmittedTurns);
        Assert.Equal(new[] { history[0], history[3], history[4], history[5] }, prepared.Messages);
        Assert.Equal(6, history.Length); // Archival history is untouched.
        Assert.Equal(65536, profile.ContextSize);
        foreach (JsonElement request in handler.Templates)
        {
            Assert.True(request.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
            Assert.Equal("high", request.GetProperty("reasoning_effort").GetString());
        }
        Assert.DoesNotContain("/v1/chat/completions", handler.Paths);
    }

    [Theory]
    [InlineData(100, 4096)]
    [InlineData(4064, 4096)]
    [InlineData(4065, 4095)]
    [InlineData(8000, 160)]
    public async Task PreservesLatestExchangeAndReducesReplyBudgetOnlyWhenNecessary(int tokens, int expectedReply)
    {
        ChatMessage[] history = [new("system", "rules"), new("user", "write game"), new("assistant", "complete code exactly"), new("user", "continue")];
        ContextHandler handler = new(8192, _ => tokens);
        using LlamaApiClient client = new(new Uri("http://localhost"), "key", TimeSpan.FromSeconds(10), handler);
        InferenceProfile profile = new() { MaxOutputTokens = 4096 };
        PreparedChat prepared = await client.PrepareChatAsync(history, profile);
        Assert.Equal(history, prepared.Messages);
        Assert.Equal(expectedReply, prepared.MaxOutputTokens);
        Assert.Equal(0, prepared.OmittedTurns);
        Assert.True(prepared.PromptTokens + prepared.MaxOutputTokens < prepared.ContextSize);
        Assert.Equal(4096, profile.MaxOutputTokens);
    }

    [Fact]
    public async Task RemovesMultipleOldExchangesWhenNeeded()
    {
        ChatMessage[] history = [new("system", "rules"), new("user", "one"), new("assistant", "one"), new("user", "two"),
            new("assistant", "two"), new("user", "three"), new("assistant", "three"), new("user", "four"), new("assistant", "four"), new("user", "five")];
        ContextHandler handler = new(1000, messages => messages.Length * 100);
        using LlamaApiClient client = new(new Uri("http://localhost"), "key", TimeSpan.FromSeconds(10), handler);
        PreparedChat prepared = await client.PrepareChatAsync(history, new InferenceProfile { MaxOutputTokens = 200 });
        Assert.Equal(2, prepared.OmittedTurns);
        Assert.Equal("three", prepared.Messages[1].Content);
        Assert.Equal("five", prepared.Messages[^1].Content);
        Assert.Equal(3, handler.Templates.Count);
    }

    [Fact]
    public async Task OversizedLatestExchangeIsNotSilentlyTruncatedOrSubmitted()
    {
        ChatMessage[] history = [new("system", "rules"), new("user", "large message")];
        ContextHandler handler = new(8192, _ => 8208);
        using LlamaApiClient client = new(new Uri("http://localhost"), "key", TimeSpan.FromSeconds(10), handler);
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.PrepareChatAsync(history, new InferenceProfile { MaxOutputTokens = 4096 }));
        Assert.Contains("Increase Context size and Restart", error.Message);
        Assert.Equal("large message", history[1].Content);
        Assert.DoesNotContain("/v1/chat/completions", handler.Paths);
    }

    [Fact]
    public async Task ContextErrorsHaveAnActionableMessageInsteadOfRawJson()
    {
        const string json = """{"error":{"code":400,"message":"request too big","type":"exceed_context_size_error","n_prompt_tokens":8208,"n_ctx":8192}}""";
        FakeHttpHandler handler = new(_ => FakeHttpHandler.Text(HttpStatusCode.BadRequest, json));
        using LlamaApiClient client = new(new Uri("http://localhost"), "key", TimeSpan.FromSeconds(10), handler);
        HttpRequestException error = await Assert.ThrowsAsync<HttpRequestException>(() => client.StreamChatAsync([new("user", "hi")], new(), _ => Task.CompletedTask));
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Contains("Increase Context size and Restart", error.Message);
        Assert.DoesNotContain("{", error.Message);
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("[]")]
    [InlineData("{\"error\":{\"type\":3}}")]
    public async Task MalformedServerErrorsStillProduceHttpExceptions(string body)
    {
        FakeHttpHandler handler = new(_ => FakeHttpHandler.Text(HttpStatusCode.BadRequest, body));
        using LlamaApiClient client = new(new Uri("http://localhost"), "key", TimeSpan.FromSeconds(10), handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.StreamChatAsync([new("user", "hi")], new(), _ => Task.CompletedTask));
    }

    [Fact]
    public async Task ReplaysAssistantReasoningButNotDashboardStatus()
    {
        ContextHandler handler = new(8192, _ => 100);
        using LlamaApiClient client = new(new Uri("http://localhost"), "key", TimeSpan.FromSeconds(10), handler);
        await client.StreamChatAsync([new("assistant", "answer") { ReasoningContent = "private thinking", Status = "Stopped" }, new("user", "continue")], new(), _ => Task.CompletedTask);
        Assert.Contains("private thinking", handler.CompletionBody);
        Assert.DoesNotContain("Stopped", handler.CompletionBody);
        Assert.Contains("answer", handler.CompletionBody);
    }

    [Fact]
    public async Task ReplaysHistoricImagesAndReasoningInStableMessageOrder()
    {
        FakeHttpHandler handler = new(_ => FakeHttpHandler.Text(HttpStatusCode.OK, "data: [DONE]\n\n", "text/event-stream"));
        using LlamaApiClient client = new(new Uri("http://localhost"), "key", TimeSpan.FromSeconds(10), handler);
        await client.StreamChatAsync(
        [
            new ChatMessage("user", "inspect") { ImageDataUrl = "data:image/png;base64,aGVsbG8=" },
            new ChatMessage("assistant", "done") { ReasoningContent = "checked" },
            new ChatMessage("user", "continue")
        ], new(), _ => Task.CompletedTask);

        using JsonDocument request = JsonDocument.Parse(handler.LastBody!);
        JsonElement messages = request.RootElement.GetProperty("messages");
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("image_url", messages[0].GetProperty("content")[1].GetProperty("type").GetString());
        Assert.Equal("checked", messages[1].GetProperty("reasoning_content").GetString());
        Assert.Equal("continue", messages[2].GetProperty("content").GetString());
        Assert.True(request.RootElement.GetProperty("cache_prompt").GetBoolean());
    }

    [Fact]
    public async Task SendsAttachedImageAsOpenAiCompatibleContentArray()
    {
        FakeHttpHandler handler = new(_ => FakeHttpHandler.Text(HttpStatusCode.OK, "data: [DONE]\n\n", "text/event-stream"));
        using LlamaApiClient client = new(new Uri("http://localhost"), "key", TimeSpan.FromSeconds(10), handler);
        await client.StreamChatAsync([new ChatMessage("user", "describe this")], new(), _ => Task.CompletedTask, imageDataUrl: "data:image/png;base64,aGVsbG8=");

        using JsonDocument request = JsonDocument.Parse(handler.LastBody!);
        JsonElement content = request.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("describe this", content[0].GetProperty("text").GetString());
        Assert.Equal("image_url", content[1].GetProperty("type").GetString());
        Assert.Equal("data:image/png;base64,aGVsbG8=", content[1].GetProperty("image_url").GetProperty("url").GetString());
    }

    private sealed class ContextHandler(int context, Func<ChatMessage[], int> count) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public List<JsonElement> Templates { get; } = [];
        public string CompletionBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            if (path == "/props") return FakeHttpHandler.Text(HttpStatusCode.OK, JsonSerializer.Serialize(new { default_generation_settings = new { n_ctx = context } }));
            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using JsonDocument document = JsonDocument.Parse(body);
            if (path == "/apply-template")
            {
                Templates.Add(document.RootElement.Clone());
                return FakeHttpHandler.Text(HttpStatusCode.OK, JsonSerializer.Serialize(new { prompt = document.RootElement.GetProperty("messages").GetRawText() }));
            }
            if (path == "/tokenize")
            {
                Assert.True(document.RootElement.GetProperty("add_special").GetBoolean());
                Assert.True(document.RootElement.GetProperty("parse_special").GetBoolean());
                ChatMessage[] messages = JsonSerializer.Deserialize<ChatMessage[]>(document.RootElement.GetProperty("content").GetString()!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                return FakeHttpHandler.Text(HttpStatusCode.OK, JsonSerializer.Serialize(new { tokens = Enumerable.Range(0, count(messages)) }));
            }
            Assert.Equal("/v1/chat/completions", path);
            CompletionBody = body;
            return FakeHttpHandler.Text(HttpStatusCode.OK, "data: [DONE]\n\n", "text/event-stream");
        }
    }
}
