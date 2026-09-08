using System.Security.Cryptography;
using FlashNext.Core.Models;

namespace FlashNext.Core.Services;

public static class PromptRunCapture
{
    // Call with a request-local profile. A random setting stays random between runs,
    // but the exact seed sent for this run is resolved before the first snapshot is saved.
    public static PromptRunReport Begin(string systemPrompt, string profileName, InferenceProfile requestProfile, ServerStatus status, bool hasImage = false)
    {
        bool randomSeed = requestProfile.Seed is null or -1;
        if (randomSeed) requestProfile.Seed = RandomNumberGenerator.GetInt32(int.MaxValue);
        return new()
        {
            SystemPrompt = systemPrompt, Profile = profileName, HasImage = hasImage,
            Sampling = RunSamplingSettings.Capture(requestProfile), GeneratedRandomSeed = randomSeed,
            Runtime = status.Configuration, Launch = status.Launch,
            RuntimeIdentity = status.Artifacts?.Runtime, ModelIdentity = status.Artifacts?.Model,
            TemplateIdentity = status.Launch?.TemplateSha256 ?? status.Artifacts?.TemplateSha256
        };
    }

    public static PromptRunReport Prepared(PromptRunReport report, PreparedChat prepared, InferenceProfile requestProfile)
    {
        requestProfile.MaxOutputTokens = prepared.MaxOutputTokens;
        var request = LlamaApiClient.CaptureRequest(prepared.Messages, requestProfile);
        return report with
        {
            ContextSize = prepared.ContextSize, EffectiveMaxOutputTokens = prepared.MaxOutputTokens,
            PreparedPromptTokens = prepared.PromptTokens, OmittedTurns = prepared.OmittedTurns,
            ServerGenerationDefaults = prepared.ServerGenerationDefaults, RequestParameters = request.Parameters,
            RequestSha256 = request.Sha256, FormattedPromptSha256 = prepared.FormattedPromptSha256,
            PromptTokenIdsSha256 = prepared.PromptTokenIdsSha256, ServerTemplateSha256 = prepared.ServerTemplateSha256
        };
    }

    public static PromptRunReport Completed(PromptRunReport report, ChatCompletionResult completion, ResponseMetrics? metrics) => report with
    {
        Status = completion.FinishReason == "length" ? PromptRunStatus.TokenLimited
            : completion.FinishReason is null ? PromptRunStatus.Interrupted : PromptRunStatus.Completed,
        FinishedAtUtc = DateTimeOffset.UtcNow, ElapsedMilliseconds = completion.TotalElapsedMilliseconds,
        HasOutput = !string.IsNullOrWhiteSpace(completion.Content), Metrics = metrics,
        EffectiveGenerationSettings = completion.EffectiveGenerationSettings, ServerFingerprint = completion.ServerFingerprint
    };
}
