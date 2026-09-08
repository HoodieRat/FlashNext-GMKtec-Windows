using System.Text;

namespace FlashNext.Core.Services;

public static class HardwareIdentity
{
    public static bool IsTargetCpu(string? value)
    {
        string normalized = Normalize(value);
        return ContainsToken(normalized, "RYZEN")
            && ContainsToken(normalized, "AI")
            && ContainsToken(normalized, "MAX")
            && ContainsToken(normalized, "395");
    }

    public static bool IsTargetGpu(string? value)
    {
        string normalized = Normalize(value);
        return ContainsToken(normalized, "RADEON") && ContainsToken(normalized, "8060S");
    }

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        StringBuilder builder = new(value.Length);
        bool previousWasSeparator = true;
        foreach (char character in value)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(char.ToUpperInvariant(character));
                previousWasSeparator = false;
            }
            else if (!previousWasSeparator)
            {
                builder.Append(' ');
                previousWasSeparator = true;
            }
        }

        return builder.ToString().Trim();
    }

    private static bool ContainsToken(string normalized, string required)
    {
        if (normalized.Length == 0) return false;
        foreach (string token in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(token, required, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
