using System.Globalization;
using FlashNext.Core.Models;

namespace FlashNext.Core.Services;

public sealed class ServerArgumentBuilder
{
    private static readonly string[] RequiredOptions = ["-m", "-ngl", "-fa", "-ctk", "-ctv", "-b", "-ub", "-t", "-tb", "--device", "--fit", "--load-mode", "--prio", "--prio-batch", "--no-context-shift", "--spec-type", "--spec-draft-p-min", "-c", "--host", "--port", "--api-key-file", "--metrics", "--jinja", "--no-agent", "--no-ui", "--cache-prompt", "--parallel", "--alias"];
    private static readonly string[] SpeculativeRequiredOptions = ["-md", "--n-gpu-layers-draft", "--spec-draft-n-max", "--spec-draft-device", "--spec-draft-prio", "--spec-draft-prio-batch", "--spec-draft-type-k", "--spec-draft-type-v"];

    public IReadOnlyList<string> Build(AppSettings settings, string runtimeDirectory, string apiKeyFile, string helpText, string? chatTemplatePath = null)
    {
        foreach (string option in RequiredOptions)
        {
            if (!ContainsOption(helpText, option)) throw new InvalidOperationException($"Pinned runtime does not expose required option '{option}'. Rebuild or roll back the runtime.");
        }
        string modelDirectory = PathExpander.Expand(settings.Paths.ModelDirectory);
        string firstShard = Path.Combine(modelDirectory, "UD-Q4_K_XL", "Qwen3.8-Flash-Next-UD-Q4_K_XL-00001-of-00004.gguf");
        if (!File.Exists(firstShard)) throw new FileNotFoundException("The first target shard is missing.", firstShard);
        if (!File.Exists(apiKeyFile)) throw new FileNotFoundException("The API key file is missing.", apiKeyFile);
        InferenceProfile profile = settings.GetActiveProfile();
        if (profile.MtpNMax is < 1 or > 6) throw new InvalidDataException("MTP n-max must be 1 through 6.");
        if (settings.Server.SpecDraftPMin is not (0.0 or 0.50 or 0.65 or 0.75 or 0.80))
            throw new InvalidDataException("Spec Draft P-Min must be 0.00, 0.50, 0.65, 0.75, or 0.80.");
        if (settings.Server.BatchSize is not (1024 or 2048 or 4096) || settings.Server.UBatchSize is not (256 or 512 or 1024 or 2048) || settings.Server.UBatchSize > settings.Server.BatchSize)
            throw new InvalidDataException("Unsupported Batch/UBatch combination; UBatch may not exceed Batch.");
        string specType = string.IsNullOrWhiteSpace(settings.Server.SpecType) ? "draft-mtp" : settings.Server.SpecType;
        if (specType is not "none" and not "draft-mtp" and not "ngram-simple") throw new InvalidDataException($"Unsupported specType '{specType}'.");
        bool speculationEnabled = specType != "none";
        if (speculationEnabled)
        {
            foreach (string option in SpeculativeRequiredOptions)
            {
                if (!ContainsOption(helpText, option)) throw new InvalidOperationException($"Pinned runtime does not expose required option '{option}'. Rebuild or roll back the runtime.");
            }
        }
        string mtp = Path.Combine(modelDirectory, "MTP", "mtp-Qwen3.8-Flash-Next-shared-Q8_0.gguf");
        string projector = Path.Combine(modelDirectory, "mmproj-F16.gguf");
        if (speculationEnabled && !File.Exists(mtp)) throw new FileNotFoundException("The MTP sidecar is missing.", mtp);
        string host = settings.Lan.Enabled ? settings.Lan.BindAddress : settings.Server.Host;
        // The pinned Halo runtime was qualified with the complete target and draft on Vulkan.
        // Its PLE disk loader is not implemented on Windows, so do not combine this runtime with
        // the older CPU tensor override / lazy-mode recipe. Explicit fit/context controls prevent
        // a later runtime from silently reducing the requested 131,072-token agent context.
        List<string> args =
        [
            "-m", firstShard,
            "--device", "Vulkan0",
            "--fit", "off",
            "-ngl", settings.Server.GpuLayers.ToString(),
            "-fa", "on",
            "-ctk", "q8_0", "-ctv", "q8_0",
            "-b", settings.Server.BatchSize.ToString(), "-ub", settings.Server.UBatchSize.ToString(),
            "-t", "16", "-tb", "16",
            "--load-mode", "mmap",
            "--prio", "2", "--prio-batch", "2",
            "--no-context-shift"
        ];
        if (File.Exists(projector))
        {
            if (!ContainsOption(helpText, "--mmproj")) throw new InvalidOperationException("The installed runtime does not support --mmproj.");
            args.AddRange(["--mmproj", projector]);
            if (TryGetImageTokenLimits(settings.Server.VisionDetail, out int minTokens, out int maxTokens))
            {
                foreach (string option in new[] { "--image-min-tokens", "--image-max-tokens" })
                {
                    if (!ContainsOption(helpText, option)) throw new InvalidOperationException($"The installed runtime does not support {option}.");
                }
                args.AddRange(["--image-min-tokens", minTokens.ToString(), "--image-max-tokens", maxTokens.ToString()]);
            }
        }
        if (speculationEnabled) args.AddRange(["-md", mtp, "--n-gpu-layers-draft", settings.Server.DraftGpuLayers.ToString(), "--spec-draft-device", "Vulkan0", "--spec-draft-type-k", "q8_0", "--spec-draft-type-v", "q8_0", "--spec-draft-prio", "2", "--spec-draft-prio-batch", "2"]);
        args.AddRange(["--spec-type", specType, "--spec-draft-p-min", settings.Server.SpecDraftPMin.ToString("0.00", CultureInfo.InvariantCulture)]);
        if (speculationEnabled) args.AddRange(["--spec-draft-n-max", profile.MtpNMax.ToString()]);
        args.AddRange(["-c", profile.ContextSize.ToString(), "--host", host, "--port", settings.Server.Port.ToString(), "--api-key-file", apiKeyFile, "--metrics", "--jinja", "--no-agent", "--no-ui", "--cache-prompt", "--parallel", "1", "--alias", "Qwen3.8-Flash-Next"]);
        if (chatTemplatePath is not null)
        {
            if (!File.Exists(chatTemplatePath)) throw new FileNotFoundException("The FlashNext prefix-preserving chat template is missing.", chatTemplatePath);
            if (!ContainsOption(helpText, "--chat-template-file")) throw new InvalidOperationException("The installed runtime does not support --chat-template-file.");
            args.AddRange(["--chat-template-file", Path.GetFullPath(chatTemplatePath)]);
        }
        if (settings.Lan.Enabled && settings.Lan.CorsOrigins.Count > 0 && ContainsOption(helpText, "--allowed-origin"))
        {
            foreach (string origin in settings.Lan.CorsOrigins) { args.Add("--allowed-origin"); args.Add(origin); }
        }
        for (int i = 0; i < settings.Server.ExtraArguments.Count; i++)
        {
            string extra = settings.Server.ExtraArguments[i];
            if (string.IsNullOrWhiteSpace(extra) || extra.Contains('\0') || extra.Contains('\r') || extra.Contains('\n')) throw new InvalidDataException("An extra server argument is invalid.");
            if (IsReservedExtraArgument(extra)) throw new InvalidDataException($"Extra server argument '{extra}' attempts to override a managed runtime or security option.");
            if (speculationEnabled && extra.Equals("--spec-draft-adaptive", StringComparison.OrdinalIgnoreCase) && !ContainsOption(helpText, "--spec-draft-adaptive"))
                throw new InvalidOperationException("Pinned runtime does not expose --spec-draft-adaptive. Rebuild or roll back the runtime.");
            if (speculationEnabled && extra.Equals("--spec-draft-n-min", StringComparison.OrdinalIgnoreCase) && !ContainsOption(helpText, "--spec-draft-n-min"))
                throw new InvalidOperationException("Pinned runtime does not expose --spec-draft-n-min. Rebuild or roll back the runtime.");
            if (speculationEnabled && extra.Equals("--spec-draft-n-min", StringComparison.OrdinalIgnoreCase) &&
                (i + 1 >= settings.Server.ExtraArguments.Count || !int.TryParse(settings.Server.ExtraArguments[i + 1], out int minimum) || minimum < 0 || minimum > profile.MtpNMax))
                throw new InvalidDataException("MTP draft minimum must be between 0 and MTP n-max.");
            if (!speculationEnabled && extra.Equals("--spec-draft-adaptive", StringComparison.OrdinalIgnoreCase)) continue;
            if (!speculationEnabled && extra.Equals("--spec-draft-n-min", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < settings.Server.ExtraArguments.Count && !settings.Server.ExtraArguments[i + 1].StartsWith("-", StringComparison.Ordinal)) i++;
                continue;
            }
            args.Add(extra);
        }
        return args;
    }

    public static bool IsReservedExtraArgument(string argument)
    {
        string value = argument.Trim();
        string[] managedOptions =
        [
            "-m", "--model", "-md", "--model-draft", "-ngl", "--n-gpu-layers", "--n-gpu-layers-draft", "-fa", "--flash-attn", "-ctk", "--cache-type-k", "-ctv", "--cache-type-v", "-b", "--batch-size", "-ub", "--ubatch-size", "-mm", "--mmproj",
            "--image-min-tokens", "--image-max-tokens",
            "--device", "-dev", "--fit", "-fit", "-ot", "--override-tensor", "--load-mode", "-lm", "--lazy-mode", "--prio", "--prio-batch", "--ngram-on-disk", "--ngram-io-threads", "--ngram-cache", "--ngram-direct-io", "--no-ngram-direct-io", "--context-shift", "--no-context-shift", "-t", "--threads", "-tb", "--threads-batch",
            "--spec-type", "--spec-draft-n-max", "--spec-draft-p-min", "--draft-p-min", "-c", "--ctx-size", "--host", "--port", "--api-key",
            "--spec-draft-device", "--device-draft", "--spec-draft-prio", "--prio-draft", "--spec-draft-prio-batch", "--prio-batch-draft", "--spec-draft-type-k", "-ctkd", "--cache-type-k-draft", "--spec-draft-type-v", "-ctvd", "--cache-type-v-draft",
            "--api-key-file", "--metrics", "--no-metrics", "--jinja", "--no-jinja", "--agent", "--no-agent",
            "--ui", "--no-ui", "--webui", "--web-ui", "--allowed-origin", "--parallel", "--alias",
            "--cache-prompt", "--no-cache-prompt", "--chat-template", "--chat-template-file"
        ];
        return managedOptions.Any(option => value.Equals(option, StringComparison.OrdinalIgnoreCase) || value.StartsWith(option + "=", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryGetImageTokenLimits(string? detail, out int minTokens, out int maxTokens)
    {
        minTokens = 1024;
        switch (detail)
        {
            case "fast": maxTokens = 2048; return true;
            case "balanced": maxTokens = 4096; return true;
            case "detailed": maxTokens = 8192; return true;
            case "maximum": maxTokens = 0; return false;
            default: throw new InvalidDataException($"Unsupported visionDetail '{detail}'.");
        }
    }

    internal static bool ContainsOption(string helpText, string option)
    {
        if (string.IsNullOrEmpty(helpText) || string.IsNullOrEmpty(option)) return false;
        int index = 0;
        while (index <= helpText.Length - option.Length)
        {
            int found = helpText.IndexOf(option, index, StringComparison.Ordinal);
            if (found < 0) return false;
            bool prefixOk = found == 0 || IsOptionBoundary(helpText[found - 1]);
            int after = found + option.Length;
            bool suffixOk = after == helpText.Length || IsOptionBoundary(helpText[after]);
            if (prefixOk && suffixOk) return true;
            index = found + 1;
        }
        return false;
    }

    private static bool IsOptionBoundary(char character)
    {
        return char.IsWhiteSpace(character) || character is ',' or '=' or ':' or ';';
    }
}
