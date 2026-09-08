using System.Globalization;
using System.Text;
using System.Text.Json;
using FlashNext.Core.Models;

namespace FlashNext.Core.Services;

public static class PromptRunFormatter
{
    public static string Headline(PromptRunReport run) =>
        $"{run.Status}  |  Runtime {ShortRuntime(run)}  |  TG {Number(run.Metrics?.GenerationTokensPerSecond)} tok/s  |  Eff {Number(run.Metrics?.EffectiveTokensPerSecond)} tok/s  |  {Number((run.Metrics?.TotalElapsedMilliseconds ?? run.ElapsedMilliseconds) / 1000)} s  |  {run.Runtime?.MtpMode ?? "MTP unknown"}  |  Batch {run.Runtime?.BatchSize.ToString() ?? "N/A"}/{run.Runtime?.UBatchSize.ToString() ?? "N/A"}  |  Rating {run.Rating?.ToString() ?? "unrated"}";

    public static string Format(PromptRunReport run)
    {
        StringBuilder text = new();
        text.AppendLine(Headline(run));
        text.AppendLine($"Started: {run.StartedAtUtc.ToLocalTime():g}   Finished: {run.FinishedAtUtc?.ToLocalTime().ToString("g") ?? "N/A"}");
        text.AppendLine($"Run: {run.Id}   Profile: {run.Profile}");
        text.AppendLine("User prompt: see session history linked by run ID, or the Reports list for attempts without a retained message.");
        text.AppendLine("History: older exchanges omitted from the request remain in the session transcript.");
        text.AppendLine($"Model: {run.ModelIdentity ?? "Unknown"}");
        text.AppendLine($"Runtime: {run.RuntimeIdentity ?? "Unknown"}");
        text.AppendLine($"Chat template SHA-256: {run.TemplateIdentity ?? "Unknown"}");
        text.AppendLine($"Server fingerprint: {run.ServerFingerprint ?? "Not returned"}");
        text.AppendLine();
        text.AppendLine("SETTINGS USED");
        if (run.Sampling is { } p)
        {
            text.AppendLine($"Thinking: {p.Thinking}   Reasoning effort: {p.ReasoningEffort}   Seed: {p.Seed?.ToString() ?? "Random (not specified)"}");
            if (run.GeneratedRandomSeed) text.AppendLine("Seed source: randomly chosen by FlashNext and explicitly sent; reuse this number to replay the request.");
            text.AppendLine($"Temperature: {p.Temperature.ToString("R", CultureInfo.InvariantCulture)}   Top-p: {p.TopP.ToString("R", CultureInfo.InvariantCulture)}   Top-k: {p.TopK}   Min-p: {p.MinP.ToString("R", CultureInfo.InvariantCulture)}");
            text.AppendLine($"Presence penalty: {p.PresencePenalty.ToString("R", CultureInfo.InvariantCulture)}   Repetition penalty: {p.RepetitionPenalty.ToString("R", CultureInfo.InvariantCulture)}");
            text.AppendLine($"Output limit: requested {p.RequestedMaxOutputTokens}, effective {run.EffectiveMaxOutputTokens?.ToString() ?? "N/A"}");
        }
        text.AppendLine($"Actual context: {run.ContextSize?.ToString() ?? "N/A"}   Prepared prompt tokens: {run.PreparedPromptTokens?.ToString() ?? "N/A"}   Omitted old turns: {run.OmittedTurns?.ToString() ?? "N/A"}");
        if (run.Runtime is { } r)
        {
            text.AppendLine($"Speculation: {r.SpecType}   MTP max: {r.MtpNMax}   Adaptive: {r.AdaptiveDraft}   Minimum: {r.DraftMinimum}");
            text.AppendLine("Spec Draft P-Min: " + (r.SpecDraftPMin?.ToString("0.00", CultureInfo.InvariantCulture) ?? "Unknown (launch not recorded)"));
            text.AppendLine($"Batch: {r.BatchSize}   UBatch: {r.UBatchSize}   Vision: {r.VisionDetail}");
            text.AppendLine($"GPU layers: {r.GpuLayers}   Draft GPU layers: {r.DraftGpuLayers}   Endpoint: {r.Host}:{r.Port}");
        }
        else text.AppendLine("Active runtime settings: unknown (external or unverified server).");
        if (run.Launch is { } launch)
        {
            text.AppendLine($"Flash Attention: {launch.FlashAttention ?? "Unknown"}   KV K={launch.CacheTypeK ?? "Unknown"} V={launch.CacheTypeV ?? "Unknown"}");
            text.AppendLine($"Runtime commit: {launch.RuntimeCommit ?? "Unknown"}   Repository: {launch.RuntimeRepository ?? "Unknown"}");
            text.AppendLine($"Runtime executable: {launch.Executable}");
            text.AppendLine($"Working directory: {launch.WorkingDirectory}");
            text.AppendLine($"Template file: {launch.TemplatePath ?? "Unknown"}   SHA-256: {launch.TemplateSha256 ?? "Unknown"}");
            text.AppendLine("Launch arguments (JSON array; credentials redacted): " + JsonSerializer.Serialize(launch.Arguments));
            text.AppendLine("Runtime environment (GGML_/LLAMA_): " + JsonSerializer.Serialize(launch.Environment));
            text.AppendLine("LAUNCHED FILES");
            foreach (RuntimeFileIdentity file in launch.Files)
            {
                text.AppendLine($"{file.Kind}: {file.Path}");
                text.AppendLine($"  Bytes: {file.Bytes?.ToString() ?? "Unknown"}   Modified UTC: {file.LastWriteTimeUtc?.ToString("O") ?? "Unknown"}");
                text.AppendLine($"  SHA-256: {file.Sha256 ?? "Not rehashed"}   Manifest SHA-256: {file.ManifestSha256 ?? "N/A"}");
                if (file.Repository is not null) text.AppendLine($"  Source: {file.Repository} @ {file.Revision ?? "Unknown"}");
            }
            foreach (string notice in launch.Notices) text.AppendLine("Provenance notice: " + notice);
        }
        else text.AppendLine("Launch/file snapshot: not recorded. A managed server restart with this version is required; old/external launch details are not guessed.");
        text.AppendLine($"Request SHA-256: {run.RequestSha256 ?? "Not recorded"}");
        text.AppendLine($"Formatted prompt SHA-256: {run.FormattedPromptSha256 ?? "Not recorded"}");
        text.AppendLine($"Prompt token IDs SHA-256: {run.PromptTokenIdsSha256 ?? "Not recorded"}");
        text.AppendLine($"Server template SHA-256: {run.ServerTemplateSha256 ?? "Not returned"}");
        AppendJson(text, "REQUEST PARAMETERS SENT", run.RequestParameters);
        AppendJson(text, "SERVER GENERATION DEFAULTS AT REQUEST START", run.ServerGenerationDefaults);
        AppendJson(text, "EFFECTIVE GENERATION SETTINGS RETURNED BY SERVER", run.EffectiveGenerationSettings);
        text.AppendLine($"Image attached: {run.HasImage} (see the saved user message).");
        text.AppendLine();
        text.AppendLine("SYSTEM PROMPT");
        text.AppendLine(run.SystemPrompt);
        text.AppendLine();
        text.AppendLine("PERFORMANCE");
        text.AppendLine(run.Metrics is { } metrics ? BenchmarkReportFormatter.Format(metrics) : "Final token counts and performance measurements: N/A.");
        if (!string.IsNullOrWhiteSpace(run.Error)) text.AppendLine("Notice: " + run.Error);
        return text.ToString().TrimEnd();
    }

    private static string Number(double? value) => value is double n && double.IsFinite(n) ? n.ToString("0.###", CultureInfo.InvariantCulture) : "N/A";

    private static string ShortRuntime(PromptRunReport run)
    {
        string value = run.Launch?.RuntimeCommit ?? run.Metrics?.RuntimeCommit ?? "Unknown";
        return value.Length > 12 && value.All(Uri.IsHexDigit) ? value[..12] : value;
    }

    private static void AppendJson(StringBuilder text, string title, JsonElement? value)
    {
        text.AppendLine(title);
        text.AppendLine(value is { } json ? JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }) : "Not recorded/returned; do not infer from current settings.");
    }
}
