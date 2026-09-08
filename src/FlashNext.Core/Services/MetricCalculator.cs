using System.Globalization;
using FlashNext.Core.Models;

namespace FlashNext.Core.Services;

public static class MetricCalculator
{
    public static ResponseMetrics Build(ChatCompletionResult completion, MetricSnapshot before, MetricSnapshot after, SystemTelemetry telemetry, string profile, int contextMaximum, AppSettings? settings = null, RuntimeConfiguration? runtime = null, RuntimeLaunchSnapshot? launch = null)
    {
        (long drafted, long accepted, double? percent) = completion.DraftedTokens > 0
            ? (completion.DraftedTokens, completion.AcceptedTokens, completion.AcceptedTokens * 100.0 / completion.DraftedTokens)
            : PrometheusParser.SpeculativeDelta(before, after);
        double? uptimeSeconds = PrometheusParser.UptimeSeconds(after);
        runtime ??= settings is null ? null : RuntimeConfiguration.Capture(settings);

        double totalMs = completion.TotalElapsedMilliseconds;
        double prefillMs = completion.PromptMilliseconds ?? completion.TimeToFirstTokenMilliseconds;
        double decodeMs = completion.PredictedMilliseconds ?? Math.Max(0, totalMs - completion.TimeToFirstTokenMilliseconds);
        int generated = completion.CompletionTokens;
        double? effective = totalMs > 0 && generated > 0 ? generated / (totalMs / 1000.0) : null;
        double? tg = completion.GenerationTokensPerSecond ?? (decodeMs > 0 && generated > 0 ? generated / (decodeMs / 1000.0) : null);
        int totalPromptTokens = Math.Max(completion.PromptTokens, completion.TotalPromptTokens);
        int reusedPromptTokens = Math.Clamp(completion.CachedTokens, 0, totalPromptTokens);
        int newlyEvaluatedPromptTokens = completion.EvaluatedPromptTokens ?? Math.Max(0, totalPromptTokens - reusedPromptTokens);
        int mtpNMax = runtime?.MtpNMax ?? 0;
        bool mtpOn = runtime is not null
            ? string.Equals(runtime.SpecType, "draft-mtp", StringComparison.OrdinalIgnoreCase)
            : drafted > 0;
        int? batch = runtime?.BatchSize;
        int? ubatch = runtime?.UBatchSize;
        return new ResponseMetrics
        {
            Profile = profile,
            Model = runtime is null ? "Unknown (external server)" : "Qwen3.8-Flash-Next",
            Quant = runtime is null ? "Unknown" : "UD-Q4_K_XL",
            RuntimeRepository = launch?.RuntimeRepository ?? "Unknown (launch not recorded)",
            RuntimeCommit = launch?.RuntimeCommit ?? "Unknown (launch not recorded)",
            RuntimeBuildManifestSha256 = launch?.BuildManifestSha256 ?? "Unknown (launch not recorded)",
            RuntimeExecutable = launch?.Executable ?? "Unknown (launch not recorded)",
            ContextUsed = Math.Max(completion.TotalTokens, totalPromptTokens + generated),
            ContextMaximum = contextMaximum,
            PromptTokens = totalPromptTokens,
            CachedTokens = reusedPromptTokens,
            PromptCacheReusedTokens = reusedPromptTokens,
            NewlyEvaluatedPromptTokens = newlyEvaluatedPromptTokens,
            PromptCacheReusePercent = totalPromptTokens > 0 ? reusedPromptTokens * 100.0 / totalPromptTokens : null,
            // Hybrid recurrent state may restore an earlier checkpoint. cache_n
            // reports reused state, not the first token where two prompts differ.
            FirstPrefixDivergenceTokenIndex = null,
            VisualTokens = completion.VisualTokens,
            GeneratedTokens = generated,
            TimeToFirstTokenMilliseconds = completion.TimeToFirstTokenMilliseconds,
            PromptTokensPerSecond = completion.PromptTokensPerSecond,
            GenerationTokensPerSecond = tg,
            EffectiveTokensPerSecond = effective,
            TotalElapsedMilliseconds = totalMs,
            PrefillMilliseconds = prefillMs,
            DecodeMilliseconds = decodeMs,
            InterTokenLatencyMilliseconds = generated > 0 && decodeMs > 0 ? decodeMs / generated : null,
            DraftedTokens = drafted,
            AcceptedTokens = accepted,
            AcceptancePercent = percent,
            // The server log provides the exact verification-step count through "mean len".
            // Do not approximate a draft-token mean from attempted tokens and n-max.
            AverageAcceptedDraftLength = null,
            MtpNMax = mtpNMax,
            SpecDraftPMin = runtime?.SpecDraftPMin,
            MtpOn = mtpOn,
            MtpMode = runtime?.MtpMode ?? "Unknown (external server)",
            VisionDetail = DescribeVisionDetail(runtime?.VisionDetail),
            MtpSpeedup = null,
            FlashAttention = launch?.FlashAttention ?? "Unknown (launch not recorded)",
            KvCacheType = launch is { CacheTypeK: not null, CacheTypeV: not null } ? $"K={launch.CacheTypeK} V={launch.CacheTypeV}" : "Unknown (launch not recorded)",
            BatchSize = batch,
            UBatchSize = ubatch,
            ServerUptime = uptimeSeconds is double uptime ? TimeSpan.FromSeconds(uptime) : null,
            ProcessWorkingSetBytes = telemetry.WorkingSetBytes,
            PeakWorkingSetBytes = telemetry.PeakWorkingSetBytes,
            AvailableSystemMemoryBytes = telemetry.AvailableSystemMemoryBytes,
            PhysicalMemoryBytes = telemetry.PhysicalMemoryBytes,
            GpuMemoryUsedBytes = telemetry.GpuMemoryUsedBytes,
            GpuUtilizationPercent = telemetry.GpuUtilizationPercent,
            CpuUtilizationPercent = telemetry.CpuUtilizationPercent,
            PowerWatts = telemetry.PowerWatts,
            TemperatureCelsius = telemetry.TemperatureCelsius,
            CompileSuccess = "N/A",
            TestsPassed = "N/A",
            PassAt1 = "N/A",
            RepairAttempts = "N/A",
            ToolCalls = "N/A",
            InvalidToolCalls = "N/A",
            TokensToSolution = "N/A",
            TimeToSolution = totalMs > 0 ? $"{totalMs / 1000.0:N2} s (this completion)" : "N/A",
            SuccessRate = "N/A",
            ContextReprocessing = reusedPromptTokens > 0 ? $"{reusedPromptTokens:N0} cached prompt tokens" : "N/A",
            QualityTaskScore = "N/A"
        };
    }

    private static string DescribeVisionDetail(string? detail) => detail switch
    {
        "fast" => "Fast · 1,024–2,048",
        "detailed" => "Detailed · 1,024–8,192",
        "maximum" => "Maximum · model default",
        "balanced" => "Balanced · 1,024–4,096",
        _ => "Unknown (external server)"
    };
}
