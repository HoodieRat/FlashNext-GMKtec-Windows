using FlashNext.Core.Models;
using FlashNext.Core.Services;

namespace FlashNext.UnitTests;

public sealed class BenchmarkReportFormatterTests
{
    [Fact]
    public void FormatsHeadlineCardWithMeasuredAndNaRows()
    {
        ResponseMetrics metrics = MetricCalculator.Build(
            new ChatCompletionResult
            {
                PromptTokens = 12438,
                CompletionTokens = 2048,
                TotalTokens = 14486,
                PromptTokensPerSecond = 1842,
                GenerationTokensPerSecond = 54.3,
                TimeToFirstTokenMilliseconds = 6910,
                TotalElapsedMilliseconds = 35400,
                PromptMilliseconds = 6750,
                PredictedMilliseconds = 28650
            },
            new MetricSnapshot { Values = { ["llamacpp:draft_tokens"] = 10, ["llamacpp:accept_tokens"] = 5 } },
            new MetricSnapshot { Values = { ["llamacpp:draft_tokens"] = 110, ["llamacpp:accept_tokens"] = 73 } },
            new SystemTelemetry { WorkingSetBytes = 48L * 1024 * 1024 * 1024, PeakWorkingSetBytes = 49L * 1024 * 1024 * 1024 },
            "coding-balanced",
            16384,
            new AppSettings
            {
                ActiveProfile = "coding-balanced",
                Profiles = new Dictionary<string, InferenceProfile>(StringComparer.OrdinalIgnoreCase)
                {
                    ["coding-balanced"] = new() { ContextSize = 16384, MtpNMax = 3, MaxOutputTokens = 2048 }
                },
                Server = new ServerSettings { SpecType = "draft-mtp" }
            });

        string card = BenchmarkReportFormatter.Format(metrics);
        Assert.Contains("Model: Qwen3.8-Flash-Next", card);
        Assert.Contains("Quant: UD-Q4_K_XL", card);
        Assert.Contains("Runtime: Unknown (launch not recorded) @ Unknown (launch not recorded)", card);
        Assert.StartsWith("Runtime Unknown", BenchmarkReportFormatter.Headline(metrics));
        Assert.Contains("Vision Detail: Balanced", card);
        Assert.Contains("Image/visual tokens:", card);
        Assert.Contains("Total prompt tokens:", card);
        Assert.Contains("PP:", card);
        Assert.Contains("TTFT:", card);
        Assert.Contains("TG:", card);
        Assert.Contains("Effective TPS:", card);
        Assert.Contains("MTP mode: Fixed 3", card);
        Assert.Contains("MTP acceptance:", card);
        Assert.Contains("Graph reuse:", card);
        Assert.Contains("Prompt-cache reuse:", card);
        Assert.Contains("Newly evaluated prompt:", card);
        Assert.Contains("Cache reuse:", card);
        Assert.Contains("UBatch:", card);
        Assert.Contains("MTP speedup: N/A", card);
        Assert.Contains("Compile: N/A", card);
        Assert.Contains("Time-to-solution:", card);
        Assert.Contains("Headline:", card);
        Assert.True(metrics.EffectiveTokensPerSecond is > 50 and < 60);
        Assert.Equal(68, Math.Round(metrics.AcceptancePercent ?? 0));
    }
}
