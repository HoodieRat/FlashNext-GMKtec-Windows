using System.Text.Json;
using FlashNext.Core.Models;
using FlashNext.Core.Services;

namespace FlashNext.UnitTests;

public sealed class MtpAndBatchTests
{
    [Fact]
    public async Task AllSupportedDepthsAndBatchesRoundTripAndBuildExactArguments()
    {
        using TestDirectory directory = new();
        SettingsStore store = new(directory.File("settings.json"), directory.File("factory.json"));
        string model = directory.File("model");
        Directory.CreateDirectory(Path.Combine(model, "UD-Q4_K_XL"));
        Directory.CreateDirectory(Path.Combine(model, "MTP"));
        File.WriteAllText(Path.Combine(model, "UD-Q4_K_XL", "Qwen3.8-Flash-Next-UD-Q4_K_XL-00001-of-00004.gguf"), "GGUF");
        File.WriteAllText(Path.Combine(model, "MTP", "mtp-Qwen3.8-Flash-Next-shared-Q8_0.gguf"), "GGUF");
        string key = directory.File("key");
        File.WriteAllText(key, "test-key");
        string template = directory.File("chat template.jinja");
        File.WriteAllText(template, "fixture");
        const string help = "-m -md -ngl -fa -ctk -ctv -b -ub -t -tb --device --fit -ot --load-mode --lazy-mode --prio --prio-batch --ngram-on-disk --ngram-io-threads --ngram-cache --ngram-direct-io --no-context-shift --n-gpu-layers-draft --spec-type --spec-draft-p-min --spec-draft-n-max --spec-draft-n-min --spec-draft-adaptive --spec-draft-device --spec-draft-prio --spec-draft-prio-batch --spec-draft-type-k --spec-draft-type-v -c --host --port --api-key-file --metrics --jinja --no-agent --no-ui --cache-prompt --parallel --alias --chat-template-file";
        foreach (int depth in Enumerable.Range(1, 6))
        foreach (int batch in new[] { 1024, 2048, 4096 })
        foreach (int ubatch in new[] { 256, 512, 1024, 2048 })
        foreach (bool adaptive in new[] { false, true })
        {
            if (adaptive && depth < 2) continue;
            AppSettings settings = SettingsStoreTests.CreateSettings();
            settings.Paths.ModelDirectory = model;
            settings.GetActiveProfile().MtpNMax = depth;
            settings.Server.BatchSize = batch;
            settings.Server.UBatchSize = ubatch;
            if (adaptive) settings.Server.ExtraArguments = ["--spec-draft-adaptive", "--spec-draft-n-min", "2"];
            if (ubatch > batch)
            {
                Assert.Contains(store.Validate(settings), error => error.Contains("may not exceed"));
                Assert.Throws<InvalidDataException>(() => new ServerArgumentBuilder().Build(settings, directory.Path, key, help));
                continue;
            }
            await store.SaveAsync(settings);
            AppSettings restored = await store.LoadAsync();
            Assert.Equal(depth, restored.GetActiveProfile().MtpNMax);
            Assert.Equal(batch, restored.Server.BatchSize);
            Assert.Equal(ubatch, restored.Server.UBatchSize);
            List<string> arguments = [.. new ServerArgumentBuilder().Build(restored, directory.Path, key, help, template)];
            Assert.Equal("draft-mtp", arguments[arguments.IndexOf("--spec-type") + 1]);
            Assert.Equal(depth.ToString(), arguments[arguments.IndexOf("--spec-draft-n-max") + 1]);
            Assert.Equal(batch.ToString(), arguments[arguments.IndexOf("-b") + 1]);
            Assert.Equal(ubatch.ToString(), arguments[arguments.IndexOf("-ub") + 1]);
            Assert.Equal(adaptive, arguments.Contains("--spec-draft-adaptive"));
            if (adaptive) Assert.Equal("2", arguments[arguments.IndexOf("--spec-draft-n-min") + 1]);
            Assert.Contains("--cache-prompt", arguments);
            Assert.Equal(template, arguments[arguments.IndexOf("--chat-template-file") + 1]);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void RejectsDepthsOutsideSupportedRange(int depth)
    {
        using TestDirectory directory = new();
        AppSettings settings = SettingsStoreTests.CreateSettings();
        settings.GetActiveProfile().MtpNMax = depth;
        Assert.Contains(new SettingsStore(directory.File("settings.json"), directory.File("factory.json")).Validate(settings), error => error.Contains("MTP n-max"));
    }

    [Fact]
    public void NewProfilesDefaultToFixedFour()
    {
        Assert.Equal(4, new InferenceProfile().MtpNMax);
        Assert.Equal("draft-mtp", new ServerSettings().SpecType);
        Assert.Empty(new ServerSettings().ExtraArguments);
    }

    [Fact]
    public async Task ActiveTelemetrySurvivesPendingChangesAndCsvSchemaUpgrade()
    {
        using TestDirectory directory = new();
        AppSettings settings = SettingsStoreTests.CreateSettings();
        settings.GetActiveProfile().MtpNMax = 5;
        settings.Server.BatchSize = 4096;
        settings.Server.UBatchSize = 2048;
        settings.Server.SpecDraftPMin = 0.75;
        RuntimeConfiguration running = RuntimeConfiguration.Capture(settings);
        settings.Server.SpecDraftPMin = 0.80;
        settings.GetActiveProfile().MtpNMax = 6;
        settings.Server.UBatchSize = 512;
        settings.Server.ExtraArguments = ["--spec-draft-adaptive", "--spec-draft-n-min", "2"];
        RuntimeLaunchSnapshot launch = new()
        {
            RuntimeRepository = "https://example.invalid/llama.cpp",
            RuntimeCommit = "0123456789abcdef0123456789abcdef01234567",
            BuildManifestSha256 = new string('a', 64),
            Executable = "C:\\runtime\\llama-server.exe"
        };
        ResponseMetrics metrics = MetricCalculator.Build(new() { TotalPromptTokens = 100, PromptTokens = 10, EvaluatedPromptTokens = 10, CachedTokens = 90 }, new(), new(), new(), settings.ActiveProfile, 32768, settings, running, launch);
        Assert.Equal("Fixed 5", metrics.MtpMode);
        Assert.Equal(0.75, metrics.SpecDraftPMin);
        Assert.Contains("Spec Draft P-Min: 0.75", BenchmarkReportFormatter.Format(metrics));
        PromptRunReport run = new() { Runtime = running };
        Assert.Contains("Spec Draft P-Min: 0.75", PromptRunFormatter.Format(run));
        Assert.Equal(0.75, JsonSerializer.Deserialize<PromptRunReport>(JsonSerializer.Serialize(run))!.Runtime!.SpecDraftPMin);
        Assert.Equal(4096, metrics.BatchSize);
        Assert.Equal(2048, metrics.UBatchSize);
        Assert.Equal(10, metrics.NewlyEvaluatedPromptTokens);
        Assert.Equal(90, metrics.PromptCacheReusedTokens);
        Assert.Null(metrics.FirstPrefixDivergenceTokenIndex);
        ResponseMetrics pending = MetricCalculator.Build(new(), new(), new(), new(), settings.ActiveProfile, 32768, settings);
        Assert.Equal("Adaptive 2–6", pending.MtpMode);
        Assert.Equal(0.80, pending.SpecDraftPMin);
        string legacyCsv = directory.File($"metrics-{metrics.TimestampUtc:yyyy-MM-dd}.csv");
        await File.WriteAllTextAsync(legacyCsv, "old-header\nold-row\n");
        string legacyV2 = directory.File($"metrics-{metrics.TimestampUtc:yyyy-MM-dd}-v2.csv");
        await File.WriteAllTextAsync(legacyV2, "previous-header\nprevious-row\n");
        await new MetricsWriter(directory.Path, new()).WriteAsync(metrics);
        Assert.Equal("old-header\nold-row\n", await File.ReadAllTextAsync(legacyCsv));
        Assert.Equal("previous-header\nprevious-row\n", await File.ReadAllTextAsync(legacyV2));
        string[] csv = await File.ReadAllLinesAsync(directory.File($"metrics-{metrics.TimestampUtc:yyyy-MM-dd}-v3.csv"));
        string[] header = csv[0].Split(',');
        string[] row = csv[1].Split(',');
        Assert.Equal(header.Length, row.Length);
        Assert.Equal("2048", row[Array.IndexOf(header, "ubatchSize")]);
        Assert.Equal("0.75", row[Array.IndexOf(header, "specDraftPMin")]);
        Assert.Equal(launch.RuntimeCommit, row[Array.IndexOf(header, "runtimeCommit")]);
        Assert.Equal(launch.Executable, row[Array.IndexOf(header, "runtimeExecutable")]);
        using JsonDocument json = JsonDocument.Parse(await File.ReadAllTextAsync(directory.File($"metrics-{metrics.TimestampUtc:yyyy-MM-dd}.jsonl")));
        Assert.Equal("Fixed 5", json.RootElement.GetProperty("mtpMode").GetString());
        Assert.Equal(0.75, json.RootElement.GetProperty("specDraftPMin").GetDouble());
        Assert.Equal(launch.RuntimeCommit, json.RootElement.GetProperty("runtimeCommit").GetString());
        Assert.Contains("Runtime 0123456789ab", BenchmarkReportFormatter.Headline(metrics));
    }

    [Fact]
    public void UnknownExternalTelemetryDoesNotPretendSavedDefaultsAreActive()
    {
        ResponseMetrics metrics = MetricCalculator.Build(new() { PromptTokens = 100, CachedTokens = 90 }, new(), new(), new(), "test", 4096);
        Assert.Null(metrics.BatchSize);
        Assert.Null(metrics.UBatchSize);
        Assert.Null(metrics.SpecDraftPMin);
        Assert.Contains("Spec Draft P-Min: Unknown", BenchmarkReportFormatter.Format(metrics));
        Assert.Contains("Unknown", metrics.MtpMode);
        Assert.Equal(10, metrics.NewlyEvaluatedPromptTokens);
    }
}
