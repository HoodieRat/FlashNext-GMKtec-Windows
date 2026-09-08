using System.Globalization;
using System.Text;
using System.Text.Json;
using FlashNext.Core.Models;

namespace FlashNext.Core.Services;

public sealed class MetricsWriter(string directory, MetricsSettings settings)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _directory = Path.GetFullPath(directory);
    private readonly MetricsSettings _settings = settings;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task WriteAsync(ResponseMetrics metrics, CancellationToken cancellationToken = default)
    {
        if (_settings.IncludePromptOrResponse) throw new InvalidOperationException("Content fields are not supported by this writer.");
        Directory.CreateDirectory(_directory);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string day = metrics.TimestampUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (_settings.WriteJsonl)
            {
                await File.AppendAllTextAsync(Path.Combine(_directory, $"metrics-{day}.jsonl"), JsonSerializer.Serialize(metrics, JsonOptions) + Environment.NewLine, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            }
            if (_settings.WriteCsv)
            {
                string path = Path.Combine(_directory, $"metrics-{day}.csv");
                // Do not append the new column layout to an older day's CSV header.
                for (int version = 2; File.Exists(path) && File.ReadLines(path).FirstOrDefault() != Header; version++)
                    path = Path.Combine(_directory, $"metrics-{day}-v{version}.csv");
                if (!File.Exists(path)) await File.WriteAllTextAsync(path, Header + Environment.NewLine, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                await File.AppendAllTextAsync(path, ToCsv(metrics) + Environment.NewLine, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            }
            PurgeOldFiles();
        }
        finally { _gate.Release(); }
    }

    private void PurgeOldFiles()
    {
        DateTime cutoff = DateTime.UtcNow.AddDays(-_settings.RetentionDays);
        foreach (string file in Directory.EnumerateFiles(_directory, "metrics-*.*", SearchOption.TopDirectoryOnly))
        {
            if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
        }
    }

    private const string Header = "timestampUtc,model,quant,profile,runtimeRepository,runtimeCommit,runtimeBuildManifestSha256,runtimeExecutable,contextUsed,contextMaximum,promptTokens,cachedTokens,promptCacheReusedTokens,generatedTokens,ttftMs,promptTps,generationTps,effectiveTps,totalElapsedMs,prefillMs,decodeMs,interTokenMs,draftedTokens,acceptedTokens,acceptancePercent,avgAcceptedDraft,mtpMode,specDraftPMin,mtpNMax,mtpOn,graphReuseCount,flashAttention,kvCache,batchSize,ubatchSize,serverUptimeSeconds,workingSetBytes,peakWorkingSetBytes,availableSystemMemoryBytes,gpuMemoryUsedBytes,gpuUtilizationPercent,cpuUtilizationPercent";
    private static string ToCsv(ResponseMetrics value)
    {
        string[] fields =
        [
            value.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
            value.Model,
            value.Quant,
            value.Profile,
            value.RuntimeRepository,
            value.RuntimeCommit,
            value.RuntimeBuildManifestSha256,
            value.RuntimeExecutable,
            value.ContextUsed.ToString(CultureInfo.InvariantCulture),
            value.ContextMaximum.ToString(CultureInfo.InvariantCulture),
            value.PromptTokens.ToString(CultureInfo.InvariantCulture),
            value.CachedTokens.ToString(CultureInfo.InvariantCulture),
            value.PromptCacheReusedTokens.ToString(CultureInfo.InvariantCulture),
            value.GeneratedTokens.ToString(CultureInfo.InvariantCulture),
            F<double>(value.TimeToFirstTokenMilliseconds),
            F(value.PromptTokensPerSecond),
            F(value.GenerationTokensPerSecond),
            F(value.EffectiveTokensPerSecond),
            F<double>(value.TotalElapsedMilliseconds),
            F(value.PrefillMilliseconds),
            F(value.DecodeMilliseconds),
            F(value.InterTokenLatencyMilliseconds),
            value.DraftedTokens.ToString(CultureInfo.InvariantCulture),
            value.AcceptedTokens.ToString(CultureInfo.InvariantCulture),
            F(value.AcceptancePercent),
            F(value.AverageAcceptedDraftLength),
            value.MtpMode,
            F(value.SpecDraftPMin),
            value.MtpNMax.ToString(CultureInfo.InvariantCulture),
            value.MtpOn ? "1" : "0",
            F(value.GraphReuseCount),
            value.FlashAttention,
            value.KvCacheType,
            F(value.BatchSize),
            F(value.UBatchSize),
            F<double>(value.ServerUptime is TimeSpan uptime ? uptime.TotalSeconds : null),
            F(value.ProcessWorkingSetBytes),
            F(value.PeakWorkingSetBytes),
            F(value.AvailableSystemMemoryBytes),
            F(value.GpuMemoryUsedBytes),
            F(value.GpuUtilizationPercent),
            F(value.CpuUtilizationPercent)
        ];
        return string.Join(',', fields.Select(Escape));
    }
    private static string F<T>(T? value) where T : struct, IFormattable => value?.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty;
    private static string Escape(string value) => value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
}
