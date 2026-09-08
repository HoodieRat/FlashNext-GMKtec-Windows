using System.Text.Json;
using FlashNext.Core.Interfaces;
using FlashNext.Core.Models;

namespace FlashNext.Core.Services;

public sealed class SettingsStore(string settingsPath, string factoryPath) : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public string SettingsPath { get; } = Path.GetFullPath(settingsPath);
    public string FactoryPath { get; } = Path.GetFullPath(factoryPath);

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        if (!File.Exists(SettingsPath)) return await ResetToFactoryAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadValidatedAsync(SettingsPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception primaryException) when (primaryException is JsonException or InvalidDataException or IOException)
        {
            string diagnostic = SettingsPath + ".invalid-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".json";
            File.Copy(SettingsPath, diagnostic, false);
            string backup = SettingsPath + ".bak";
            if (File.Exists(backup))
            {
                AppSettings recovered = await ReadValidatedAsync(backup, cancellationToken).ConfigureAwait(false);
                await SaveAsync(recovered, cancellationToken).ConfigureAwait(false);
                return recovered;
            }
            throw new InvalidDataException($"Settings are invalid and no valid backup exists. A diagnostic copy was saved at '{diagnostic}'.", primaryException);
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> errors = Validate(settings);
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        AppSettings normalized = Normalize(settings);
        string json = JsonSerializer.Serialize(normalized, JsonOptions) + Environment.NewLine;
        await AtomicFile.WriteTextAsync(SettingsPath, json, cancellationToken).ConfigureAwait(false);
    }

    public Task<AppSettings> LoadFactoryAsync(CancellationToken cancellationToken = default) => ReadValidatedAsync(FactoryPath, cancellationToken);

    public async Task<AppSettings> ResetToFactoryAsync(CancellationToken cancellationToken = default)
    {
        AppSettings settings = await ReadValidatedAsync(FactoryPath, cancellationToken).ConfigureAwait(false);
        await SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        return settings;
    }

    public IReadOnlyList<string> Validate(AppSettings settings)
    {
        List<string> errors = [];
        if (settings is null)
        {
            errors.Add("Settings object is required.");
            return errors;
        }

        if (settings.SchemaVersion != 1) errors.Add("schemaVersion must be 1.");
        if (string.IsNullOrWhiteSpace(settings.ActiveProfile)) errors.Add("activeProfile is required.");
        if (settings.Paths is null) errors.Add("paths is required.");
        if (settings.Server is null) errors.Add("server is required.");
        if (settings.Profiles is null) errors.Add("profiles is required.");
        if (settings.Chat is null) errors.Add("chat is required.");
        if (settings.Metrics is null) errors.Add("metrics is required.");
        if (settings.Downloads is null) errors.Add("downloads is required.");
        if (settings.Lan is null) errors.Add("lan is required.");
        if (settings.Updates is null) errors.Add("updates is required.");
        if (errors.Count > 0) return errors;

        if (settings.Profiles.Count == 0) errors.Add("At least one profile is required.");
        if (!string.IsNullOrWhiteSpace(settings.ActiveProfile) && !settings.Profiles.ContainsKey(settings.ActiveProfile)) errors.Add("activeProfile must name an existing profile.");
        if (string.IsNullOrWhiteSpace(settings.Server.Host)) errors.Add("Server host is required.");
        if (settings.Server.Port is < 1024 or > 65535) errors.Add("Server port must be between 1024 and 65535.");
        if (settings.Server.ControlPort is < 1024 or > 65535) errors.Add("Control port must be between 1024 and 65535.");
        if (settings.Server.ControlPort == settings.Server.Port) errors.Add("Control port must differ from the inference server port.");
        if (settings.Server.SpecType is not "none" and not "draft-mtp" and not "ngram-simple") errors.Add("Server specType must be none, draft-mtp, or ngram-simple.");
        if (settings.Server.SpecDraftPMin is not (0.0 or 0.50 or 0.65 or 0.75 or 0.80)) errors.Add("Server specDraftPMin must be 0.00, 0.50, 0.65, 0.75, or 0.80.");
        if (settings.Server.VisionDetail is not "fast" and not "balanced" and not "detailed" and not "maximum") errors.Add("Server visionDetail must be fast, balanced, detailed, or maximum.");
        if (settings.Server.StartupTimeoutSeconds is < 30 or > 3600) errors.Add("Server startup timeout must be between 30 and 3600 seconds.");
        if (settings.Server.ShutdownTimeoutSeconds is < 1 or > 300) errors.Add("Server shutdown timeout must be between 1 and 300 seconds.");
        if (settings.Server.ParallelRequests != 1) errors.Add("Factory-safe parallel request count is 1.");
        if (settings.Server.BatchSize is not (1024 or 2048 or 4096)) errors.Add("Server batchSize must be 1024, 2048, or 4096.");
        if (settings.Server.UBatchSize is not (256 or 512 or 1024 or 2048)) errors.Add("Server ubatchSize must be 256, 512, 1024, or 2048.");
        if (settings.Server.UBatchSize > settings.Server.BatchSize) errors.Add("Server ubatchSize may not exceed batchSize.");
        if (!settings.Server.Metrics || !settings.Server.Jinja || settings.Server.Agent || settings.Server.WebUi) errors.Add("Metrics and Jinja must be enabled; built-in agent and web UI must be disabled.");
        if (!settings.Lan.Enabled && !IsLoopback(settings.Server.Host)) errors.Add("Factory mode must bind to loopback.");
        if (settings.Lan.Enabled && string.IsNullOrWhiteSpace(settings.Lan.BindAddress)) errors.Add("LAN bind address is required when LAN mode is enabled.");
        if (settings.Lan.CorsOrigins is null) errors.Add("LAN CORS origin collection is required.");
        if (settings.Server.ExtraArguments is null)
        {
            errors.Add("Server extra argument collection is required.");
        }
        else
        {
            foreach (string argument in settings.Server.ExtraArguments)
            {
                if (string.IsNullOrWhiteSpace(argument) || argument.Contains('\0') || argument.Contains('\r') || argument.Contains('\n')) errors.Add("Server extra arguments may not be empty or contain control characters.");
                else if (ServerArgumentBuilder.IsReservedExtraArgument(argument)) errors.Add($"Server extra argument '{argument}' attempts to override a managed runtime or security option.");
            }
            if (settings.Server.SpecType != "none" && settings.Profiles.TryGetValue(settings.ActiveProfile, out InferenceProfile? active) && active is not null)
            {
                for (int i = 0; i < settings.Server.ExtraArguments.Count; i++)
                {
                    string argument = settings.Server.ExtraArguments[i];
                    if (!string.Equals(argument, "--spec-draft-n-min", StringComparison.OrdinalIgnoreCase)) continue;
                    if (++i >= settings.Server.ExtraArguments.Count || !int.TryParse(settings.Server.ExtraArguments[i], out int minimum) || minimum < 0 || minimum > active.MtpNMax)
                        errors.Add("MTP draft minimum must be between 0 and the active MTP n-max.");
                }
            }
        }
        if (settings.Chat.RecordContentInLogs || settings.Metrics.IncludePromptOrResponse) errors.Add("Prompt and response content recording is not permitted by the shipped security profile.");
        if (settings.Chat.RequestTimeoutSeconds is < 30 or > 7200) errors.Add("Chat request timeout must be between 30 and 7200 seconds.");
        if (settings.Metrics.RetentionDays is < 1 or > 3650) errors.Add("Metrics retention must be between 1 and 3650 days.");
        if (settings.Downloads.MinimumFreeGiB < 115 || settings.Downloads.RecommendedFreeGiB < 140) errors.Add("Download free-space limits may not be lower than the factory limits.");
        if (settings.Downloads.RecommendedFreeGiB < settings.Downloads.MinimumFreeGiB) errors.Add("Recommended free space may not be lower than the minimum.");
        if (settings.Downloads.MaxConcurrentLargeFiles is < 1 or > 2) errors.Add("Large-file concurrency must be one or two.");
        if (string.IsNullOrWhiteSpace(settings.Paths.RuntimeRoot) || string.IsNullOrWhiteSpace(settings.Paths.DataRoot)) errors.Add("Runtime and data roots are required.");
        if (string.IsNullOrWhiteSpace(settings.Downloads.CacheDirectory)) errors.Add("Download cache directory is required.");

        foreach ((string name, InferenceProfile profile) in settings.Profiles)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                errors.Add("Profile names may not be empty.");
                continue;
            }
            if (profile is null)
            {
                errors.Add($"Profile '{name}' is required.");
                continue;
            }
            if (profile.ContextSize is < 4096 or > 131072) errors.Add($"Profile '{name}' context is outside the supported range.");
            if (profile.MtpNMax is < 1 or > 6) errors.Add($"Profile '{name}' MTP n-max must be 1 through 6.");
            if (profile.MaxOutputTokens < 1 || profile.MaxOutputTokens > profile.ContextSize) errors.Add($"Profile '{name}' output limit is invalid.");
            if (profile.Temperature is < 0 or > 2 || profile.TopP is < 0 or > 1 || profile.MinP is < 0 or > 1) errors.Add($"Profile '{name}' sampling values are invalid.");
            if (profile.TopK is < 0 or > 1000) errors.Add($"Profile '{name}' top-k is invalid.");
            if (profile.PresencePenalty is < -2 or > 2 || profile.RepetitionPenalty is < 0.1 or > 2) errors.Add($"Profile '{name}' penalty values are invalid.");
        }
        return errors;
    }

    private async Task<AppSettings> ReadValidatedAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        AppSettings? settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        if (settings is null) throw new InvalidDataException($"'{path}' contains no settings object.");
        IReadOnlyList<string> errors = Validate(settings);
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        return Normalize(settings);
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        if (settings.SchemaVersion != 1) throw new InvalidDataException($"Unsupported settings schema {settings.SchemaVersion}.");
        settings.Profiles = new Dictionary<string, InferenceProfile>(settings.Profiles, StringComparer.OrdinalIgnoreCase);
        settings.Server.ExtraArguments = [.. settings.Server.ExtraArguments];
        settings.Lan.CorsOrigins = [.. settings.Lan.CorsOrigins];
        return settings;
    }

    private static bool IsLoopback(string? host) => !string.IsNullOrWhiteSpace(host) && (host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) || host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.Equals("::1", StringComparison.OrdinalIgnoreCase));
}
