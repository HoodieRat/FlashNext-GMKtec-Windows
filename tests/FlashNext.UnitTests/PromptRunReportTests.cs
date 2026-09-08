using FlashNext.Core.Models;
using FlashNext.Core.Services;
using System.Text.Json;

namespace FlashNext.UnitTests;

public sealed class PromptRunReportTests
{
    [Fact]
    public void BestUsesQualityThenKnownSpeedThenRecencyAndExcludesIncompleteRuns()
    {
        PromptRunReport good = new() { Rating = 4, HasOutput = true, Status = PromptRunStatus.Completed, Metrics = new() { GenerationTokensPerSecond = 20 }, FinishedAtUtc = DateTimeOffset.UtcNow };
        PromptRunReport fastest = good with { Id = "fast", Rating = 3, Metrics = new() { GenerationTokensPerSecond = 60 } };
        PromptRunReport unknown = good with { Id = "unknown", Metrics = null };
        PromptRunReport older = good with { Id = "older", FinishedAtUtc = good.FinishedAtUtc!.Value.AddMinutes(-1) };
        List<PromptRunReport> runs = [good, fastest, unknown, older, good with { Id = "unrated", Rating = null }, good with { Id = "empty", Rating = 5, HasOutput = false }];
        foreach (PromptRunStatus status in Enum.GetValues<PromptRunStatus>().Where(value => value != PromptRunStatus.Completed))
            runs.Add(good with { Id = status.ToString(), Rating = 5, Status = status });
        Assert.Equal(good, PromptRunReport.Best(runs));
        Assert.Null(PromptRunReport.Best([good with { Rating = null }]));
        Assert.Equal(unknown, PromptRunReport.Best([unknown]));
    }

    [Fact]
    public async Task ConcurrentRatingsAndCompletionPreserveHistoryAndSessionRecency()
    {
        using TestDirectory directory = new();
        ConversationStore store = new(directory.Path);
        PromptRunReport running = new() { Prompt = "first prompt", Sampling = RunSamplingSettings.Capture(new() { Temperature = 0.2, PresencePenalty = 1.5 }) };
        ConversationDocument document = new() { Name = "original", Draft = "keep draft", Messages = [new("user", running.Prompt!)], Runs = [running] };
        await store.SaveDocumentAsync(document);
        DateTime timestamp = DateTime.UtcNow.AddHours(-2);
        File.SetLastWriteTimeUtc(directory.File("original.json"), timestamp);
        DateTimeOffset updated = document.UpdatedAtUtc;
        await store.SaveAsync("other", [new("user", "other prompt")]);
        PromptRunReport rating = await store.UpdateRatingAsync("original", running.Id, 5);
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(directory.File("original.json")));
        Assert.Equal(updated, (await store.LoadDocumentAsync("original")).UpdatedAtUtc);
        Assert.Equal("other", store.List(mostRecentFirst: true)[0]);

        document.Runs = [running with { Status = PromptRunStatus.Completed, HasOutput = true, Metrics = new() { GenerationTokensPerSecond = 35 } }, new() { Prompt = "second prompt" }];
        document.Messages.Add(new("assistant", "code") { RunId = running.Id });
        await Task.WhenAll(new ConversationStore(directory.Path).SaveDocumentAsync(document), store.UpdateRatingAsync("original", running.Id, 4));
        ConversationDocument restored = await store.LoadDocumentAsync("original");
        Assert.Equal(2, restored.Runs.Count);
        Assert.Equal(4, restored.Runs[0].Rating);
        Assert.Equal(PromptRunStatus.Completed, restored.Runs[0].Status);
        Assert.Equal(35, restored.Runs[0].Metrics!.GenerationTokensPerSecond);
        Assert.Equal(1.5, restored.Runs[0].Sampling!.PresencePenalty);
        Assert.Equal("keep draft", restored.Draft);
        Assert.Equal("code", restored.Messages[^1].Content);
        await store.UpdateRatingAsync("original", running.Id, null);
        Assert.Null((await store.LoadDocumentAsync("original")).Runs[0].Rating);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.UpdateRatingAsync("original", running.Id, 6));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.UpdateRatingAsync("original", "missing", 1));
    }

    [Fact]
    public async Task ReportsSurviveConsoleStyleSavesAndMetricRetentionWithoutLeakingIntoLogs()
    {
        using TestDirectory directory = new();
        ConversationStore store = new(directory.File("conversations"));
        PromptRunReport run = new() { Prompt = "PRIVATE PROMPT", SystemPrompt = "PRIVATE SYSTEM", Sampling = RunSamplingSettings.Capture(new()), Status = PromptRunStatus.Completed };
        await store.SaveDocumentAsync(new() { Name = "saved", Messages = [new("user", run.Prompt!)], Runs = [run] });
        await store.SaveAsync("saved", [new("user", run.Prompt!), new("assistant", "answer")]);
        string metricsDirectory = directory.File("metrics");
        Directory.CreateDirectory(metricsDirectory);
        string old = Path.Combine(metricsDirectory, "metrics-2000-01-01.jsonl");
        await File.WriteAllTextAsync(old, "{}");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-60));
        await new MetricsWriter(metricsDirectory, new()).WriteAsync(new());
        Assert.False(File.Exists(old));
        Assert.Equal(run.Id, Assert.Single((await store.LoadDocumentAsync("saved")).Runs).Id);
        Assert.DoesNotContain("PRIVATE", string.Join("", Directory.GetFiles(metricsDirectory).Select(File.ReadAllText)));
    }

    [Fact]
    public async Task NewReportsOmitPromptTextButKeepHistoryHashesAndConfiguration()
    {
        using TestDirectory directory = new();
        ConversationStore store = new(directory.Path);
        PromptRunReport run = PromptRunCapture.Begin("saved system instructions", "test", new() { Seed = 12345 }, new()) with
        {
            RequestSha256 = "request hash", FormattedPromptSha256 = "formatted hash", PromptTokenIdsSha256 = "token hash"
        };
        await store.SaveDocumentAsync(new()
        {
            Name = "linked", Runs = [run],
            Messages = [new("user", "unique user input") { RunId = run.Id }, new("assistant", "unique model output") { RunId = run.Id }]
        });
        using JsonDocument json = JsonDocument.Parse(await File.ReadAllTextAsync(directory.File("linked.json")));
        JsonElement storedRun = json.RootElement.GetProperty("runs")[0];
        Assert.False(storedRun.TryGetProperty("prompt", out _));
        Assert.DoesNotContain("unique user input", storedRun.GetRawText());
        Assert.DoesNotContain("unique model output", storedRun.GetRawText());
        PromptRunReport restored = Assert.Single((await store.LoadDocumentAsync("linked")).Runs);
        Assert.Null(restored.Prompt);
        Assert.Equal(run, restored);
        string formatted = PromptRunFormatter.Format(restored);
        Assert.Contains("saved system instructions", formatted);
        Assert.Contains("12345", formatted);
        Assert.Contains("request hash", formatted);
        Assert.Contains("formatted hash", formatted);
        Assert.Contains("token hash", formatted);
        Assert.DoesNotContain("unique user input", formatted);
        Assert.DoesNotContain("unique model output", formatted);
        await store.UpdateRatingAsync("linked", run.Id, 5);
        Assert.Null(Assert.Single((await store.LoadDocumentAsync("linked")).Runs).Prompt);
    }

    [Fact]
    public void BenchmarkStoresOneSharedPromptInsteadOfRepeatingItForEachRun()
    {
        const string prompt = "shared benchmark input";
        BenchmarkReport report = new()
        {
            Prompt = prompt, PromptSha256 = HashService.Sha256Text(prompt),
            Candidates = [new() { Runs = [new() { Report = new() }, new() { Report = new() }] }]
        };
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(prompt, json.RootElement.GetProperty("prompt").GetString());
        Assert.Equal(report.PromptSha256, json.RootElement.GetProperty("promptSha256").GetString());
        foreach (JsonElement item in json.RootElement.GetProperty("candidates")[0].GetProperty("runs").EnumerateArray())
            Assert.False(item.GetProperty("report").TryGetProperty("prompt", out _));
    }

    [Fact]
    public void FormatterOmitsLegacyPromptTextWithoutDiscardingTheStoredFallback()
    {
        const string legacyJson = """{"id":"legacy","prompt":"legacy user input","systemPrompt":"system instructions"}""";
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
        PromptRunReport run = JsonSerializer.Deserialize<PromptRunReport>(legacyJson, options)!;
        Assert.Equal("legacy user input", run.Prompt);
        Assert.DoesNotContain("legacy user input", PromptRunFormatter.Format(run));
        Assert.Contains("legacy user input", JsonSerializer.Serialize(run, options));
        Assert.Contains("system instructions", PromptRunFormatter.Format(run));
    }

    [Fact]
    public void SnapshotAndPartialReportDoNotInventMeasurements()
    {
        InferenceProfile profile = new() { Temperature = 0.2, PresencePenalty = 1.5 };
        RunSamplingSettings snapshot = RunSamplingSettings.Capture(profile);
        profile.Temperature = 0.7;
        PromptRunReport run = new() { Sampling = snapshot, ElapsedMilliseconds = 250 };
        Assert.Equal(0.2, run.Sampling.Temperature);
        Assert.Equal(PromptRunStatus.Interrupted, run.RecoverInterrupted().Status);
        string text = PromptRunFormatter.Format(run.RecoverInterrupted());
        Assert.Contains("TG N/A", text);
        Assert.Contains("unknown (external", text);
        Assert.Contains("0.25 s", text);
    }
}
