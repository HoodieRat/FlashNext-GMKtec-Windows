namespace FlashNext.Core.Services;

public static class PathExpander
{
    public static string Expand(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        return Path.GetFullPath(expanded);
    }

    public static string EnsureUnder(string root, string path)
    {
        string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string normalizedPath = Path.GetFullPath(path);
        if (!normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Path '{normalizedPath}' is outside the owned root '{normalizedRoot}'.");
        }
        return normalizedPath;
    }
}
