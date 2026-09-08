using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using FlashNext.Core.Interfaces;
using FlashNext.Core.Services;
using FlashNext.Infrastructure.Windows.System;

namespace FlashNext.Infrastructure.Windows.Security;

public sealed class WindowsSecretStore : ISecretStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly PlatformPaths _paths;
    private readonly SecretRedactor _redactor;

    public WindowsSecretStore(PlatformPaths paths, SecretRedactor redactor)
    {
        _paths = paths;
        _redactor = redactor;
    }

    public string SecretPath => _paths.SecretsPath;
    public string ApiKeyFilePath => _paths.ApiKeyFilePath;

    public async Task<string> GetOrCreateApiKeyAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(SecretPath)) return await GetApiKeyAsync(cancellationToken).ConfigureAwait(false);
        return await RotateApiKeyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> RotateApiKeyAsync(CancellationToken cancellationToken = default)
    {
        string key = "fn_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(36)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string json = JsonSerializer.Serialize(new SecretDocument { ApiKey = key, CreatedAtUtc = DateTimeOffset.UtcNow }, JsonOptions) + Environment.NewLine;
        await AtomicFile.WriteTextAsync(SecretPath, json, cancellationToken).ConfigureAwait(false);
        await AtomicFile.WriteTextAsync(ApiKeyFilePath, key + Environment.NewLine, cancellationToken).ConfigureAwait(false);
        await ProtectFileAsync(SecretPath, cancellationToken).ConfigureAwait(false);
        await ProtectFileAsync(ApiKeyFilePath, cancellationToken).ConfigureAwait(false);
        _redactor.Register(key);
        return key;
    }

    public async Task<string> GetApiKeyAsync(CancellationToken cancellationToken = default)
    {
        await using FileStream stream = File.OpenRead(SecretPath);
        SecretDocument? document = await JsonSerializer.DeserializeAsync<SecretDocument>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        if (document is null || string.IsNullOrWhiteSpace(document.ApiKey) || document.ApiKey.Length < 32) throw new InvalidDataException("The local API key file is invalid.");
        _redactor.Register(document.ApiKey);
        if (!File.Exists(ApiKeyFilePath) || !string.Equals((await File.ReadAllTextAsync(ApiKeyFilePath, cancellationToken).ConfigureAwait(false)).Trim(), document.ApiKey, StringComparison.Ordinal))
        {
            await AtomicFile.WriteTextAsync(ApiKeyFilePath, document.ApiKey + Environment.NewLine, cancellationToken).ConfigureAwait(false);
            await ProtectFileAsync(ApiKeyFilePath, cancellationToken).ConfigureAwait(false);
        }
        return document.ApiKey;
    }

    public string Redact(string value) => _redactor.Redact(value);

    private static async Task ProtectFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return;
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        string userSid = identity.User?.Value ?? throw new UnauthorizedAccessException("The current Windows user SID could not be resolved.");
        ProcessStartInfo start = new("icacls.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[]
        {
            path,
            "/inheritance:r",
            "/grant:r",
            $"*{userSid}:(F)",
            "*S-1-5-18:(F)",
            "*S-1-5-32-544:(F)"
        })
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the Windows ACL utility.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or global::System.ComponentModel.Win32Exception) { }
            throw new TimeoutException("The Windows ACL utility exceeded the 30-second limit.");
        }
        string error = await stderr.ConfigureAwait(false);
        _ = await stdout.ConfigureAwait(false);
        if (process.ExitCode != 0) throw new UnauthorizedAccessException($"Could not restrict access to '{path}': {error}");
    }

    private sealed class SecretDocument
    {
        public string ApiKey { get; set; } = string.Empty;
        public DateTimeOffset CreatedAtUtc { get; set; }
    }
}
