using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FlashNext.Core.Interfaces;
using FlashNext.Core.Models;
using FlashNext.Infrastructure.Windows.System;

namespace FlashNext.Infrastructure.Windows.Diagnostics;

public sealed class DiagnosticsBundleService(PlatformPaths paths, ISecretStore secrets) : IDiagnosticsBundleService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<string> CreateBundleAsync(AppSettings settings, HardwareReport? hardware, CancellationToken cancellationToken = default)
    {
        paths.EnsureUserDirectories();
        string staging = Path.Combine(paths.SupportDirectory, "bundle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            AppSettings sanitized = CloneAndSanitize(settings);
            await WriteJsonAsync(Path.Combine(staging, "settings.redacted.json"), sanitized, cancellationToken).ConfigureAwait(false);
            if (hardware is not null) await WriteJsonAsync(Path.Combine(staging, "hardware.json"), hardware, cancellationToken).ConfigureAwait(false);
            foreach (string manifest in new[] { paths.ModelLockPath, paths.RuntimeLockPath, paths.DependencyLockPath })
            {
                if (File.Exists(manifest)) File.Copy(manifest, Path.Combine(staging, Path.GetFileName(manifest)), true);
            }
            await WriteEnvironmentAsync(Path.Combine(staging, "environment.txt"), cancellationToken).ConfigureAwait(false);
            CopyRecentManagerLogs(staging);
            CopySanitizedServerTail(staging);
            CopyBenchmarks(staging);
            string zip = Path.Combine(paths.SupportDirectory, $"FlashNext-support-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.zip");
            if (File.Exists(zip)) File.Delete(zip);
            ZipFile.CreateFromDirectory(staging, zip, CompressionLevel.Optimal, false);
            return zip;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    private static AppSettings CloneAndSanitize(AppSettings settings)
    {
        string json = JsonSerializer.Serialize(settings, JsonOptions);
        AppSettings clone = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? throw new InvalidDataException("Could not prepare diagnostic settings.");
        clone.Chat.SystemPrompt = "[content removed from support bundle]";
        clone.Chat.RecordContentInLogs = false;
        clone.Metrics.IncludePromptOrResponse = false;
        clone.Paths.ModelDirectory = string.IsNullOrWhiteSpace(clone.Paths.ModelDirectory) ? string.Empty : "[configured model directory]";
        clone.Paths.DataRoot = "[current-user data directory]";
        clone.Downloads.CacheDirectory = "[current-user download cache]";
        return clone;
    }

    private async Task WriteJsonAsync(string path, object value, CancellationToken cancellationToken)
    {
        string json = secrets.Redact(JsonSerializer.Serialize(value, JsonOptions)) + Environment.NewLine;
        await File.WriteAllTextAsync(path, json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteEnvironmentAsync(string path, CancellationToken cancellationToken)
    {
        string text = $"Created: {DateTimeOffset.UtcNow:O}{Environment.NewLine}OS: {Environment.OSVersion}{Environment.NewLine}64-bit OS: {Environment.Is64BitOperatingSystem}{Environment.NewLine}Manager: {typeof(DiagnosticsBundleService).Assembly.GetName().Version}{Environment.NewLine}User data: [local path removed]{Environment.NewLine}";
        await File.WriteAllTextAsync(path, secrets.Redact(text), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }

    private void CopyRecentManagerLogs(string staging)
    {
        if (!Directory.Exists(paths.LogsDirectory)) return;
        foreach (string file in Directory.EnumerateFiles(paths.LogsDirectory, "manager-*.jsonl").OrderByDescending(File.GetLastWriteTimeUtc).Take(3))
        {
            string content = secrets.Redact(string.Join(Environment.NewLine, File.ReadLines(file).TakeLast(1000)));
            File.WriteAllText(Path.Combine(staging, Path.GetFileName(file)), content, new UTF8Encoding(false));
        }
    }

    private void CopySanitizedServerTail(string staging)
    {
        foreach (string name in new[] { "server-output.log", "server-error.log" })
        {
            string path = Path.Combine(paths.LogsDirectory, name);
            if (!File.Exists(path)) continue;
            IEnumerable<string> safe = File.ReadLines(path).TakeLast(500).Where(static line =>
                !line.Contains("prompt", StringComparison.OrdinalIgnoreCase) &&
                !line.Contains("content", StringComparison.OrdinalIgnoreCase) &&
                !line.Contains("response", StringComparison.OrdinalIgnoreCase) &&
                !line.Contains("request", StringComparison.OrdinalIgnoreCase));
            File.WriteAllText(Path.Combine(staging, name + ".sanitized.txt"), secrets.Redact(string.Join(Environment.NewLine, safe)), new UTF8Encoding(false));
        }
    }

    private void CopyBenchmarks(string staging)
    {
        if (!Directory.Exists(paths.BenchmarksDirectory)) return;
        foreach (string file in Directory.EnumerateFiles(paths.BenchmarksDirectory, "*.json").OrderByDescending(File.GetLastWriteTimeUtc).Take(5))
        {
            File.Copy(file, Path.Combine(staging, Path.GetFileName(file)), true);
        }
    }
}
