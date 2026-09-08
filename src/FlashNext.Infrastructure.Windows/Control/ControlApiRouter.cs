using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlashNext.Core.Interfaces;
using FlashNext.Core.Models;
using FlashNext.Infrastructure.Windows.System;

namespace FlashNext.Infrastructure.Windows.Control;

public sealed class ControlApiRouter(ISettingsStore settings, IRuntimeSupervisor supervisor, ISecretStore secrets, PlatformPaths paths)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly HashSet<string> RootKeys = new(StringComparer.OrdinalIgnoreCase) { "activeProfile", "profiles", "chat", "server" };
    private static readonly HashSet<string> ProfileKeys = new(StringComparer.OrdinalIgnoreCase) { "thinking", "reasoningEffort", "temperature", "topP", "topK", "minP", "presencePenalty", "repetitionPenalty", "maxOutputTokens", "contextSize", "mtpNMax", "seed" };
    private static readonly HashSet<string> ChatKeys = new(StringComparer.OrdinalIgnoreCase) { "systemPrompt" };
    private static readonly HashSet<string> ServerKeys = new(StringComparer.OrdinalIgnoreCase) { "port", "gpuLayers", "draftGpuLayers", "specType", "specDraftPMin", "extraArguments", "batchSize", "ubatchSize", "visionDetail" };

    public async Task<ControlApiResult> HandleAsync(string method, string path, string? authorization, string body, IPAddress? remote, CancellationToken cancellationToken = default)
    {
        if (remote is not null && !IPAddress.IsLoopback(remote)) return JsonError(403, "Control API is loopback-only.");
        string expected = await secrets.GetApiKeyAsync(cancellationToken).ConfigureAwait(false);
        if (!BearerMatches(authorization, expected)) return JsonError(401, "Missing or invalid API key.");

        string normalized = NormalizePath(path);
        if (method.Equals("GET", StringComparison.OrdinalIgnoreCase) && normalized is "/health" or "/flashnext/health") return new ControlApiResult(200, """{"status":"ok"}""" + Environment.NewLine);
        if (method.Equals("GET", StringComparison.OrdinalIgnoreCase) && normalized == "/flashnext/status") return await StatusAsync(cancellationToken).ConfigureAwait(false);
        if (method.Equals("GET", StringComparison.OrdinalIgnoreCase) && normalized == "/flashnext/settings") return await SettingsAsync(cancellationToken).ConfigureAwait(false);
        if (method.Equals("PATCH", StringComparison.OrdinalIgnoreCase) && normalized == "/flashnext/settings") return await PatchSettingsAsync(body, cancellationToken).ConfigureAwait(false);
        if (method.Equals("POST", StringComparison.OrdinalIgnoreCase) && normalized == "/flashnext/server/start") return await StartAsync(cancellationToken).ConfigureAwait(false);
        if (method.Equals("POST", StringComparison.OrdinalIgnoreCase) && normalized == "/flashnext/server/stop") return await StopAsync(cancellationToken).ConfigureAwait(false);
        if (method.Equals("POST", StringComparison.OrdinalIgnoreCase) && normalized == "/flashnext/server/restart") return await RestartAsync(cancellationToken).ConfigureAwait(false);
        return JsonError(404, "Unknown control route.");
    }

    private async Task<ControlApiResult> StatusAsync(CancellationToken cancellationToken)
    {
        AppSettings current = await settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        InferenceProfile profile = current.GetActiveProfile();
        ServerStatus status = supervisor.Status;
        object payload = new
        {
            state = status.State.ToString(),
            processId = status.ProcessId,
            isOwnedProcess = status.IsOwnedProcess,
            lastError = status.LastError,
            baseUri = status.BaseUri?.ToString(),
            activeProfile = current.ActiveProfile,
            thinking = profile.Thinking,
            mtpNMax = status.Configuration?.MtpNMax,
            mtpMode = status.Configuration?.MtpMode,
            specDraftPMin = status.Configuration?.SpecDraftPMin,
            batchSize = status.Configuration?.BatchSize,
            ubatchSize = status.Configuration?.UBatchSize,
            savedMtpNMax = profile.MtpNMax,
            savedSpecDraftPMin = current.Server.SpecDraftPMin,
            restartRequired = status.Configuration is { } running ? running != RuntimeConfiguration.Capture(current) : (bool?)null,
            inferencePort = current.Server.Port,
            controlPort = current.Server.ControlPort,
            modelDirectory = current.Paths.ModelDirectory,
            applicationDirectory = paths.ApplicationDirectory
        };
        return Ok(payload);
    }

    private async Task<ControlApiResult> SettingsAsync(CancellationToken cancellationToken)
    {
        AppSettings current = await settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        return Ok(current);
    }

    private async Task<ControlApiResult> PatchSettingsAsync(string body, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(body)) return JsonError(400, "PATCH body is required.");
        JsonNode? patch;
        try { patch = JsonNode.Parse(body); }
        catch (JsonException ex) { return JsonError(400, "PATCH body is not valid JSON: " + ex.Message); }
        if (patch is not JsonObject root) return JsonError(400, "PATCH body must be a JSON object.");
        string? unknown = FindUnknownKey(root);
        if (unknown is not null) return JsonError(400, $"Field '{unknown}' is not writable through the control API.");

        AppSettings current = await settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        RuntimeConfiguration before = RuntimeConfiguration.Capture(current);
        JsonObject merged = JsonSerializer.SerializeToNode(current, Json)!.AsObject();
        MergeObject(merged, root);
        AppSettings? updated;
        try { updated = merged.Deserialize<AppSettings>(Json); }
        catch (JsonException ex) { return JsonError(400, "Merged settings are invalid JSON: " + ex.Message); }
        if (updated is null) return JsonError(400, "Merged settings are empty.");
        IReadOnlyList<string> errors = settings.Validate(updated);
        if (errors.Count > 0) return JsonError(400, string.Join(Environment.NewLine, errors));
        await settings.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        bool restartRequired = RuntimeConfiguration.Capture(updated) != (supervisor.Status.Configuration ?? before);
        return Ok(new { saved = true, restartRequired, settings = updated });
    }

    private async Task<ControlApiResult> StartAsync(CancellationToken cancellationToken)
    {
        AppSettings current = await settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        ServerStatus status = await supervisor.StartAsync(current, cancellationToken).ConfigureAwait(false);
        return Ok(status);
    }

    private async Task<ControlApiResult> StopAsync(CancellationToken cancellationToken)
    {
        await supervisor.StopAsync(cancellationToken).ConfigureAwait(false);
        return Ok(supervisor.Status);
    }

    private async Task<ControlApiResult> RestartAsync(CancellationToken cancellationToken)
    {
        AppSettings current = await settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        ServerStatus status = await supervisor.RestartAsync(current, cancellationToken).ConfigureAwait(false);
        return Ok(status);
    }

    private static string? FindUnknownKey(JsonObject root)
    {
        foreach (string key in root.Select(static p => p.Key))
        {
            if (!RootKeys.Contains(key)) return key;
        }
        if (root["profiles"] is JsonObject profiles)
        {
            foreach ((string name, JsonNode? node) in profiles)
            {
                if (node is not JsonObject profile) return "profiles." + name;
                foreach (string key in profile.Select(static p => p.Key))
                {
                    if (!ProfileKeys.Contains(key)) return "profiles." + name + "." + key;
                }
            }
        }
        if (root["chat"] is JsonObject chat)
        {
            foreach (string key in chat.Select(static p => p.Key))
            {
                if (!ChatKeys.Contains(key)) return "chat." + key;
            }
        }
        if (root["server"] is JsonObject server)
        {
            foreach (string key in server.Select(static p => p.Key))
            {
                if (!ServerKeys.Contains(key)) return "server." + key;
            }
        }
        return null;
    }

    private static void MergeObject(JsonObject target, JsonObject patch)
    {
        foreach ((string key, JsonNode? node) in patch)
        {
            string canonicalKey = target.Select(pair => pair.Key).FirstOrDefault(existingKey => existingKey.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? key;
            if (node is JsonObject child && target[canonicalKey] is JsonObject existing)
            {
                MergeObject(existing, child);
                continue;
            }
            target[canonicalKey] = node?.DeepClone();
        }
    }

    private static bool BearerMatches(string? authorization, string expected)
    {
        if (string.IsNullOrWhiteSpace(authorization) || string.IsNullOrWhiteSpace(expected)) return false;
        const string prefix = "Bearer ";
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        string provided = authorization[prefix.Length..].Trim();
        return CryptographicEquals(provided, expected);
    }

    private static bool CryptographicEquals(string left, string right)
    {
        if (left.Length != right.Length) return false;
        int different = 0;
        for (int i = 0; i < left.Length; i++) different |= left[i] ^ right[i];
        return different == 0;
    }

    private static string NormalizePath(string path)
    {
        string value = string.IsNullOrWhiteSpace(path) ? "/" : path.Trim();
        int query = value.IndexOf('?', StringComparison.Ordinal);
        if (query >= 0) value = value[..query];
        if (value.Length > 1) value = value.TrimEnd('/');
        return value.Length == 0 ? "/" : value;
    }

    private static ControlApiResult Ok(object payload) => new(200, JsonSerializer.Serialize(payload, Json) + Environment.NewLine);

    private static ControlApiResult JsonError(int status, string message) => new(status, JsonSerializer.Serialize(new { error = message }, Json) + Environment.NewLine);

}
