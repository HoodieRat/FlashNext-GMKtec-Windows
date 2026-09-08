using System.Diagnostics;
using System.Text.Json;
using FlashNext.Core.Interfaces;
using FlashNext.Core.Models;
using FlashNext.Core.Services;

namespace FlashNext.Infrastructure.Windows.System;

public sealed class LanModeService(PlatformPaths paths) : ILanModeService
{
    public Task EnableAsync(AppSettings settings, CancellationToken cancellationToken = default) => InvokeAsync("enable", settings, cancellationToken);
    public Task DisableAsync(AppSettings settings, CancellationToken cancellationToken = default) => InvokeAsync("disable", settings, cancellationToken);

    private async Task InvokeAsync(string action, AppSettings settings, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("LAN firewall management requires Windows.");
        paths.EnsureUserDirectories();
        string request = Path.Combine(paths.StateDirectory, "lan-request-" + Guid.NewGuid().ToString("N") + ".json");
        string result = request + ".result.json";
        string executable = Path.Combine(paths.ApplicationDirectory, "FlashNext.Manager.exe");
        var payload = new
        {
            action,
            port = settings.Server.Port,
            program = executable,
            remoteAddress = settings.Lan.AllowedRemoteAddress,
            resultFile = result
        };
        await AtomicFile.WriteTextAsync(request, JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }) + Environment.NewLine, cancellationToken).ConfigureAwait(false);
        try
        {
            ProcessStartInfo start = new("powershell.exe")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Normal
            };
            foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", paths.LanScriptPath, "-RequestFile", request })
            {
                start.ArgumentList.Add(argument);
            }
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not launch the elevated firewall helper.");
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { if (!process.HasExited) process.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or global::System.ComponentModel.Win32Exception) { }
                throw new TimeoutException("The elevated firewall helper exceeded the 5-minute limit.");
            }
            if (process.ExitCode != 0) throw new InvalidOperationException($"Firewall helper exited with code {process.ExitCode}.");
            if (!File.Exists(result)) throw new InvalidDataException("Firewall helper did not write a result.");
            using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(result, cancellationToken).ConfigureAwait(false));
            if (!document.RootElement.TryGetProperty("success", out JsonElement success) || !success.GetBoolean())
            {
                string message = document.RootElement.TryGetProperty("message", out JsonElement value) ? value.GetString() ?? "Firewall operation failed." : "Firewall operation failed.";
                throw new InvalidOperationException(message);
            }
        }
        finally
        {
            File.Delete(request);
            File.Delete(result);
        }
    }
}
