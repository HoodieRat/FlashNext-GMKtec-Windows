using System.Text.RegularExpressions;

namespace FlashNext.Core.Services;

public sealed partial class SecretRedactor
{
    private readonly HashSet<string> _literalSecrets = new(StringComparer.Ordinal);

    public void Register(string? secret)
    {
        if (!string.IsNullOrWhiteSpace(secret) && secret.Length >= 8) _literalSecrets.Add(secret);
    }

    public string Redact(string value)
    {
        string result = value;
        foreach (string secret in _literalSecrets.OrderByDescending(static item => item.Length))
        {
            result = result.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        }
        result = BearerPattern().Replace(result, "Bearer [REDACTED]");
        result = ApiKeyJsonPattern().Replace(result, "$1[REDACTED]$2");
        return result;
    }

    [GeneratedRegex("Bearer\\s+[A-Za-z0-9._~+\\-/=]{8,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerPattern();

    [GeneratedRegex("(\\\"(?:apiKey|api_key|key)\\\"\\s*:\\s*\\\")[^\\\"]+(\\\")", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ApiKeyJsonPattern();
}
