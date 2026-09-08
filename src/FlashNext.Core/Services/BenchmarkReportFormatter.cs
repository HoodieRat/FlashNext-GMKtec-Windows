using System.Globalization;
using System.Text;
using FlashNext.Core.Models;

namespace FlashNext.Core.Services;

public static class BenchmarkReportFormatter
{
    public static string Format(ResponseMetrics value)
    {
        StringBuilder text = new();
        text.AppendLine("Model: " + value.Model);
        text.AppendLine("Quant: " + value.Quant);
        text.AppendLine("Runtime: " + Runtime(value));
        text.AppendLine("Runtime build manifest SHA-256: " + value.RuntimeBuildManifestSha256);
        text.AppendLine("Runtime executable: " + value.RuntimeExecutable);
        text.AppendLine("Context: " + value.ContextMaximum.ToString("N0", CultureInfo.InvariantCulture));
        text.AppendLine("Vision Detail: " + value.VisionDetail);
        text.AppendLine("Image/visual tokens: " + (value.VisualTokens?.ToString("N0", CultureInfo.InvariantCulture) ?? "N/A"));
        text.AppendLine("Total prompt tokens: " + value.PromptTokens.ToString("N0", CultureInfo.InvariantCulture));
        text.AppendLine("Output tokens: " + value.GeneratedTokens.ToString("N0", CultureInfo.InvariantCulture));
        text.AppendLine();
        text.AppendLine("PP: " + Rate(value.PromptTokensPerSecond));
        text.AppendLine("TTFT: " + SecondsFromMs(value.TimeToFirstTokenMilliseconds));
        text.AppendLine("TG: " + Rate(value.GenerationTokensPerSecond));
        text.AppendLine("Effective TPS: " + Rate(value.EffectiveTokensPerSecond));
        text.AppendLine("MTP mode: " + value.MtpMode);
        text.AppendLine("Spec Draft P-Min: " + (value.SpecDraftPMin?.ToString("0.00", CultureInfo.InvariantCulture) ?? "Unknown (launch not recorded)"));
        text.AppendLine("MTP acceptance: " + (value.AcceptancePercent is double accept ? accept.ToString("N1", CultureInfo.InvariantCulture) + "%" : "N/A") + "  (" + value.AcceptedTokens.ToString("N0", CultureInfo.InvariantCulture) + "/" + value.DraftedTokens.ToString("N0", CultureInfo.InvariantCulture) + ")");
        text.AppendLine("Avg accepted draft: " + (value.AverageAcceptedDraftLength is double avg ? avg.ToString("N2", CultureInfo.InvariantCulture) : "N/A"));
        text.AppendLine("Graph reuse: " + (value.GraphReuseCount?.ToString(CultureInfo.InvariantCulture) ?? "N/A"));
        text.AppendLine("Prompt-cache reuse: " + value.PromptCacheReusedTokens.ToString("N0", CultureInfo.InvariantCulture) + " tokens");
        text.AppendLine("Newly evaluated prompt: " + value.NewlyEvaluatedPromptTokens.ToString("N0", CultureInfo.InvariantCulture) + " tokens");
        text.AppendLine("Cache reuse: " + (value.PromptCacheReusePercent is double reuse ? reuse.ToString("N1", CultureInfo.InvariantCulture) + "%" : "N/A"));
        if (value.FirstPrefixDivergenceTokenIndex is int divergence)
            text.AppendLine("First prefix divergence: token " + divergence.ToString("N0", CultureInfo.InvariantCulture) + " (runtime cache)");
        text.AppendLine("MTP speedup: " + (value.MtpSpeedup is double speed ? speed.ToString("N2", CultureInfo.InvariantCulture) + "×" : "N/A (needs an MTP-off baseline run)"));
        text.AppendLine();
        text.AppendLine("Total time: " + SecondsFromMs(value.TotalElapsedMilliseconds));
        text.AppendLine("Prefill: " + SecondsFromMs(value.PrefillMilliseconds));
        text.AppendLine("Decode: " + SecondsFromMs(value.DecodeMilliseconds));
        text.AppendLine("Inter-token: " + (value.InterTokenLatencyMilliseconds is double itl ? itl.ToString("N1", CultureInfo.InvariantCulture) + " ms" : "N/A"));
        text.AppendLine("RAM: " + Gib(value.ProcessWorkingSetBytes));
        text.AppendLine("Peak RAM: " + Gib(value.PeakWorkingSetBytes));
        text.AppendLine("GPU memory: " + Gib(value.GpuMemoryUsedBytes));
        text.AppendLine("GPU util: " + Pct(value.GpuUtilizationPercent));
        text.AppendLine("CPU util: " + Pct(value.CpuUtilizationPercent));
        text.AppendLine("Power: " + (value.PowerWatts is double w ? w.ToString("N0", CultureInfo.InvariantCulture) + " W" : "N/A"));
        text.AppendLine("Peak temp: " + (value.TemperatureCelsius is double c ? c.ToString("N0", CultureInfo.InvariantCulture) + " °C" : "N/A"));
        text.AppendLine("Flash Attention: " + value.FlashAttention);
        text.AppendLine("KV cache: " + value.KvCacheType);
        text.AppendLine("Batch size: " + (value.BatchSize is int batch ? batch.ToString(CultureInfo.InvariantCulture) : "N/A"));
        text.AppendLine("UBatch: " + (value.UBatchSize is int ubatch ? ubatch.ToString(CultureInfo.InvariantCulture) : "N/A"));
        text.AppendLine();
        text.AppendLine("Headline:  Runtime " + RuntimeShort(value) + "  |  PP " + Rate(value.PromptTokensPerSecond) + "  |  TTFT " + SecondsFromMs(value.TimeToFirstTokenMilliseconds) + "  |  TG " + Rate(value.GenerationTokensPerSecond) + "  |  Eff " + Rate(value.EffectiveTokensPerSecond) + "  |  MTP " + (value.AcceptancePercent is double a ? a.ToString("N0", CultureInfo.InvariantCulture) + "%" : "N/A") + "  |  Time " + SecondsFromMs(value.TotalElapsedMilliseconds));
        text.AppendLine();
        text.AppendLine("Compile: " + (value.CompileSuccess ?? "N/A"));
        text.AppendLine("Tests: " + (value.TestsPassed ?? "N/A"));
        text.AppendLine("Pass@1: " + (value.PassAt1 ?? "N/A"));
        text.AppendLine("Repairs: " + (value.RepairAttempts ?? "N/A"));
        text.AppendLine("Tool calls: " + (value.ToolCalls ?? "N/A"));
        text.AppendLine("Invalid tool calls: " + (value.InvalidToolCalls ?? "N/A"));
        text.AppendLine("Tokens to solution: " + (value.TokensToSolution ?? "N/A"));
        text.AppendLine("Time-to-solution: " + (value.TimeToSolution ?? "N/A"));
        text.AppendLine("Success rate: " + (value.SuccessRate ?? "N/A"));
        text.AppendLine("Context reprocessing: " + (value.ContextReprocessing ?? "N/A"));
        text.AppendLine("Quality / task score: " + (value.QualityTaskScore ?? "N/A"));
        return text.ToString().TrimEnd();
    }

    public static string Headline(ResponseMetrics value) =>
        "Runtime " + RuntimeShort(value) + "   PP " + Rate(value.PromptTokensPerSecond) + "   TTFT " + SecondsFromMs(value.TimeToFirstTokenMilliseconds) + "   TG " + Rate(value.GenerationTokensPerSecond) + "   Eff " + Rate(value.EffectiveTokensPerSecond) + "   " + value.MtpMode + "   MTP " + (value.AcceptancePercent is double a ? a.ToString("N0", CultureInfo.InvariantCulture) + "%" : "N/A") + "   Batch " + (value.BatchSize?.ToString(CultureInfo.InvariantCulture) ?? "N/A") + "/" + (value.UBatchSize?.ToString(CultureInfo.InvariantCulture) ?? "N/A") + "   " + SecondsFromMs(value.TotalElapsedMilliseconds);

    private static string Runtime(ResponseMetrics value) => value.RuntimeRepository + " @ " + value.RuntimeCommit;
    private static string RuntimeShort(ResponseMetrics value) => value.RuntimeCommit.Length > 12 && value.RuntimeCommit.All(Uri.IsHexDigit)
        ? value.RuntimeCommit[..12]
        : value.RuntimeCommit;

    private static string Rate(double? value) => value is double rate ? rate.ToString("N1", CultureInfo.InvariantCulture) + " tok/s" : "N/A";
    private static string SecondsFromMs(double? milliseconds)
    {
        if (milliseconds is not double ms) return "N/A";
        return ms >= 1000 ? (ms / 1000.0).ToString("N2", CultureInfo.InvariantCulture) + " s" : ms.ToString("N0", CultureInfo.InvariantCulture) + " ms";
    }
    private static string Gib(long? bytes) => bytes is long value && value > 0 ? (value / (1024d * 1024d * 1024d)).ToString("N1", CultureInfo.InvariantCulture) + " GB" : "N/A";
    private static string Pct(double? value) => value is double pct ? pct.ToString("N0", CultureInfo.InvariantCulture) + "%" : "N/A";
}
