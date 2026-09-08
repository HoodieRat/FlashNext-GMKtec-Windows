using FlashNext.Core.Models;
using FlashNext.Core.Services;

namespace FlashNext.UnitTests;

public sealed class SecurityAndPersistenceTests
{
    [Fact]
    public void RedactsLiteralBearerAndJsonSecrets()
    {
        SecretRedactor redactor = new();
        redactor.Register("abcdefghijklmnopqrstuvwxyz123456");
        string value = redactor.Redact("Bearer abcdefghijklmnop and abcdefghijklmnopqrstuvwxyz123456 and {\"apiKey\":\"anothersecretvalue\"}");
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz123456", value);
        Assert.DoesNotContain("abcdefghijklmnop", value);
        Assert.Contains("[REDACTED]", value);
    }

    [Fact]
    public async Task MetricsWriterNeverAddsConversationContent()
    {
        using TestDirectory directory = new();
        MetricsWriter writer = new(directory.Path, new MetricsSettings { WriteJsonl = true, WriteCsv = true, IncludePromptOrResponse = false });
        await writer.WriteAsync(new ResponseMetrics { Profile = "test", GeneratedTokens = 3 });
        string all = string.Join("\n", Directory.GetFiles(directory.Path).Select(File.ReadAllText));
        Assert.DoesNotContain("promptText", all);
        Assert.DoesNotContain("responseText", all);
    }

    [Fact]
    public async Task ConversationStoreRejectsTraversal()
    {
        using TestDirectory directory = new();
        ConversationStore store = new(directory.Path);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync("../outside", [new ChatMessage("user", "hello")]));
    }

    [Fact]
    public async Task SessionsKeepSeparateTranscriptsDraftsAndPartialReplyMetadata()
    {
        using TestDirectory directory = new();
        ConversationStore store = new(directory.Path);
        ConversationDocument game = new()
        {
            Name = "game", Draft = "continue the game",
            Messages = [new("system", "rules"), new("user", "write game"), new("assistant", "partial code") { ReasoningContent = "thinking", Status = "Stopped. Partial reply retained." }]
        };
        await store.SaveDocumentAsync(game);
        await store.SaveAsync("other", [new("user", "different task")]);
        ConversationStore reopened = new(directory.Path);
        ConversationDocument restored = await reopened.LoadDocumentAsync("game");
        Assert.Equal(game.Messages, restored.Messages);
        Assert.Equal(game.Draft, restored.Draft);
        Assert.Equal("different task", Assert.Single(await reopened.LoadAsync("other")).Content);
        Assert.Equal(new[] { "game", "other" }, reopened.List());
        restored.Messages.Add(new("user", "continue"));
        await reopened.SaveDocumentAsync(restored);
        Assert.Equal(4, (await store.LoadAsync("game")).Count);
        Assert.Equal("game", store.List(mostRecentFirst: true)[0]);
        Assert.Single(await store.LoadAsync("other"));
    }

    [Fact]
    public async Task LoadsLegacyConversationWithoutSessionMetadata()
    {
        using TestDirectory directory = new();
        await AtomicFile.WriteTextAsync(directory.File("legacy.json"), """{"version":1,"messages":[{"role":"user","content":"hello"}]}""");
        ConversationDocument document = await new ConversationStore(directory.Path).LoadDocumentAsync("legacy");
        Assert.Equal("legacy", document.Name);
        Assert.Equal(string.Empty, document.Draft);
        Assert.Equal(new ChatMessage("user", "hello"), Assert.Single(document.Messages));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[{\"role\":\"invalid\",\"content\":\"x\"}]")]
    [InlineData("[{\"role\":\"user\",\"content\":null}]")]
    public async Task RejectsMalformedSessionMessages(string messages)
    {
        using TestDirectory directory = new();
        await AtomicFile.WriteTextAsync(directory.File("broken.json"), "{\"version\":1,\"messages\":" + messages + "}");
        await Assert.ThrowsAsync<InvalidDataException>(() => new ConversationStore(directory.Path).LoadDocumentAsync("broken"));
    }
}
