using System.Text.Json;
using FlashNext.Core.Models;
using FlashNext.Core.Services;

namespace FlashNext.Manager.ConsoleUi;

public static class CommandLineModes
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int?> TryRunAsync(string[] args, ManagerServices services, CancellationToken cancellationToken = default)
    {
        if (args.Length == 0) return null;
        switch (args[0].ToLowerInvariant())
        {
            case "--initialize":
                _ = await services.Settings.LoadAsync(cancellationToken).ConfigureAwait(false);
                _ = await services.Secrets.GetOrCreateApiKeyAsync(cancellationToken).ConfigureAwait(false);
                return 0;

            case "--set-model-directory":
                if (args.Length != 2) throw new ArgumentException("Usage: --set-model-directory <directory>");
                AppSettings pathSettings = await services.Settings.LoadAsync(cancellationToken).ConfigureAwait(false);
                pathSettings.Paths.ModelDirectory = Path.GetFullPath(Environment.ExpandEnvironmentVariables(args[1]));
                await services.Settings.SaveAsync(pathSettings, cancellationToken).ConfigureAwait(false);
                return 0;

            case "--hardware-report":
                if (args.Length != 2) throw new ArgumentException("Usage: --hardware-report <output.json>");
                HardwareReport report = await services.Hardware.ProbeAsync(cancellationToken).ConfigureAwait(false);
                await WriteJsonAsync(args[1], report, cancellationToken).ConfigureAwait(false);
                return report.PassesFactoryPreflight ? 0 : 20;

            case "--verify-model":
                AppSettings modelSettings = await services.Settings.LoadAsync(cancellationToken).ConfigureAwait(false);
                ModelVerificationResult model = await services.Model.VerifyAsync(modelSettings, cancellationToken).ConfigureAwait(false);
                foreach (string message in model.Messages) Console.WriteLine(message);
                return model.Success ? 0 : 21;

            case "--download-model":
                AppSettings downloadSettings = await services.Settings.LoadAsync(cancellationToken).ConfigureAwait(false);
                bool accepted = args.Contains("--accept-license", StringComparer.OrdinalIgnoreCase);
                ModelVerificationResult download = await services.Model.DownloadOrRepairAsync(downloadSettings, accepted, cancellationToken).ConfigureAwait(false);
                foreach (string message in download.Messages) Console.WriteLine(message);
                return download.Success ? 0 : 22;

            case "--verify-runtime":
                AppSettings runtimeSettings = await services.Settings.LoadAsync(cancellationToken).ConfigureAwait(false);
                RuntimeVerificationResult runtime = await services.Runtime.VerifyCurrentAsync(runtimeSettings, cancellationToken).ConfigureAwait(false);
                Console.WriteLine(JsonSerializer.Serialize(runtime, JsonOptions));
                return runtime.Success ? 0 : 23;

            case "--smoke-test":
                if (args.Length != 2) throw new ArgumentException("Usage: --smoke-test <output.json>");
                return await RunFactorySmokeAsync(args[1], services, cancellationToken).ConfigureAwait(false);

            case "--factory-reset":
                _ = await services.Settings.ResetToFactoryAsync(cancellationToken).ConfigureAwait(false);
                _ = await services.Secrets.RotateApiKeyAsync(cancellationToken).ConfigureAwait(false);
                Console.WriteLine("Factory settings restored and local API security material rotated. Verified model and runtime files were preserved.");
                return 0;

            default:
                throw new ArgumentException($"Unknown command-line mode '{args[0]}'.");
        }
    }

    private static async Task<int> RunFactorySmokeAsync(string outputPath, ManagerServices services, CancellationToken cancellationToken)
    {
        object result;
        try
        {
            AppSettings settings = await services.Settings.LoadAsync(cancellationToken).ConfigureAwait(false);
            ModelVerificationResult model = await services.Model.VerifyAsync(settings, cancellationToken).ConfigureAwait(false);
            if (!model.Success)
            {
                result = new { success = false, stage = "model-verification", messages = model.Messages, timestampUtc = DateTimeOffset.UtcNow };
                await WriteJsonAsync(outputPath, result, cancellationToken).ConfigureAwait(false);
                return 24;
            }

            HardwareReport hardware = await services.Hardware.ProbeAsync(cancellationToken).ConfigureAwait(false);
            if (!hardware.PassesFactoryPreflight)
            {
                result = new { success = false, stage = "hardware-preflight", hardware, timestampUtc = DateTimeOffset.UtcNow };
                await WriteJsonAsync(outputPath, result, cancellationToken).ConfigureAwait(false);
                return 25;
            }

            if (!settings.Profiles.TryGetValue("benchmark-deterministic", out InferenceProfile? profile)) throw new InvalidDataException("benchmark-deterministic profile is missing.");
            settings.ActiveProfile = "benchmark-deterministic";
            profile.MtpNMax = 3;
            profile.MaxOutputTokens = 32;
            profile.Temperature = 0;
            profile.Seed = 12345;

            ServerStatus status = await services.Supervisor.StartAsync(settings, cancellationToken).ConfigureAwait(false);
            if (status.BaseUri is null) throw new InvalidOperationException("The server did not publish a local API address.");
            string apiKey = await services.Secrets.GetApiKeyAsync(cancellationToken).ConfigureAwait(false);
            using LlamaApiClient client = new(status.BaseUri, apiKey, TimeSpan.FromMinutes(20));
            _ = await client.GetModelsAsync(cancellationToken).ConfigureAwait(false);
            MetricSnapshot before = await client.GetMetricsAsync(cancellationToken).ConfigureAwait(false);
            List<ChatMessage> messages = [new("system", "Reply with one concise sentence."), new("user", "Confirm that local inference is operational.")];
            ChatCompletionResult completion = await client.StreamChatAsync(messages, profile, static _ => Task.CompletedTask, cancellationToken).ConfigureAwait(false);
            MetricSnapshot after = await client.GetMetricsAsync(cancellationToken).ConfigureAwait(false);
            (long drafted, long accepted, double? acceptance) = PrometheusParser.SpeculativeDelta(before, after);
            if (completion.CompletionTokens < 1 || string.IsNullOrWhiteSpace(completion.Content)) throw new InvalidOperationException("The server returned no valid completion.");
            if (drafted <= 0 || accepted <= 0) throw new InvalidOperationException("MTP counters did not show nonzero drafted and accepted token deltas.");
            SystemTelemetry telemetry = await services.Telemetry.CaptureAsync(status.ProcessId, cancellationToken).ConfigureAwait(false);
            result = new
            {
                success = true,
                stage = "factory-model-smoke",
                timestampUtc = DateTimeOffset.UtcNow,
                processId = status.ProcessId,
                completionTokens = completion.CompletionTokens,
                timeToFirstTokenMilliseconds = completion.TimeToFirstTokenMilliseconds,
                promptTokensPerSecond = completion.PromptTokensPerSecond,
                generationTokensPerSecond = completion.GenerationTokensPerSecond,
                draftedTokens = drafted,
                acceptedTokens = accepted,
                acceptancePercent = acceptance,
                workingSetBytes = telemetry.WorkingSetBytes,
                availableSystemMemoryBytes = telemetry.AvailableSystemMemoryBytes,
                gpuMemoryUsedBytes = telemetry.GpuMemoryUsedBytes,
                gpuUtilizationPercent = telemetry.GpuUtilizationPercent,
                hardware
            };
            await WriteJsonAsync(outputPath, result, cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = new { success = false, stage = "factory-model-smoke", error = ex.Message, timestampUtc = DateTimeOffset.UtcNow };
            await WriteJsonAsync(outputPath, result, CancellationToken.None).ConfigureAwait(false);
            return 26;
        }
        finally
        {
            try { await services.Supervisor.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
        }
    }

    private static async Task WriteJsonAsync(string path, object value, CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await AtomicFile.WriteTextAsync(fullPath, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine, cancellationToken).ConfigureAwait(false);
    }
}
