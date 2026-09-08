using System.Globalization;
using System.Text.RegularExpressions;
using FlashNext.Core.Models;

namespace FlashNext.Core.Services;

public static class PrometheusParser
{
    private static readonly Regex GraphReusePattern = new(@"graphs reused\s*=\s*(?<value>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DraftMeanPattern = new(@"mean len\s*=\s*(?<value>\d+(?:\.\d+)?)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ImageTokenBatchPattern = new(@"decoding\s+image\s+batch\s+\d+\s*/\s*\d+\s*,\s*n_tokens_batch\s*=\s*(?<value>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static MetricSnapshot Parse(string text)
    {
        MetricSnapshot snapshot = new();
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int space = line.LastIndexOf(' ');
            if (space <= 0) continue;
            string name = line[..space];
            int labels = name.IndexOf('{');
            if (labels >= 0) name = name[..labels];
            if (double.TryParse(line[(space + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) snapshot.Values[name] = value;
        }
        return snapshot;
    }

    public static (long Drafted, long Accepted, double? Acceptance) SpeculativeDelta(MetricSnapshot before, MetricSnapshot after)
    {
        double draftedBefore = FindDrafted(before) ?? 0;
        double draftedAfter = FindDrafted(after) ?? 0;
        double acceptedBefore = FindAccepted(before) ?? 0;
        double acceptedAfter = FindAccepted(after) ?? 0;
        long drafted = Math.Max(0, (long)Math.Round(draftedAfter - draftedBefore));
        long accepted = Math.Max(0, (long)Math.Round(acceptedAfter - acceptedBefore));
        double? percent = drafted > 0 ? Math.Clamp(accepted * 100.0 / drafted, 0, 100) : null;
        return (drafted, accepted, percent);
    }

    public static double? UptimeSeconds(MetricSnapshot snapshot) => snapshot.First("llamacpp:uptime_seconds", "llama_server_uptime_seconds", "process_uptime_seconds");

    public static double? GenerationTokensPerSecond(MetricSnapshot before, MetricSnapshot after)
    {
        double? tokensBefore = before.First("llamacpp:tokens_predicted_total", "llama_server_tokens_predicted_total", "tokens_predicted_total");
        double? tokensAfter = after.First("llamacpp:tokens_predicted_total", "llama_server_tokens_predicted_total", "tokens_predicted_total");
        if (tokensBefore is not double startTokens || tokensAfter is not double endTokens || endTokens <= startTokens) return null;

        double generated = endTokens - startTokens;
        double? secondsBefore = before.First("llamacpp:tokens_predicted_seconds_total", "llama_server_tokens_predicted_seconds_total", "tokens_predicted_seconds_total");
        double? secondsAfter = after.First("llamacpp:tokens_predicted_seconds_total", "llama_server_tokens_predicted_seconds_total", "tokens_predicted_seconds_total");
        if (secondsBefore is double startSeconds && secondsAfter is double endSeconds && endSeconds > startSeconds)
            return generated / (endSeconds - startSeconds);

        double wallSeconds = (after.CapturedAtUtc - before.CapturedAtUtc).TotalSeconds;
        return wallSeconds > 0 ? generated / wallSeconds : null;
    }

    public static (int? GraphReuseCount, double? MeanAcceptedSpanLength) ParseCompletionLog(string text)
    {
        Match graph = GraphReusePattern.Matches(text).Cast<Match>().LastOrDefault() ?? Match.Empty;
        Match draft = DraftMeanPattern.Matches(text).Cast<Match>().LastOrDefault() ?? Match.Empty;
        int? graphReuse = graph.Success && int.TryParse(graph.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int graphValue) ? graphValue : null;
        double? averageDraft = draft.Success && double.TryParse(draft.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double draftValue) ? draftValue : null;
        return (graphReuse, averageDraft);
    }

    public static double? AcceptedDraftTokensFromMeanSpan(double? meanAcceptedSpanLength, int mtpNMax)
    {
        if (meanAcceptedSpanLength is not double span || mtpNMax < 1) return null;
        return Math.Clamp(span - 1.0, 0.0, mtpNMax);
    }

    public static int? ParseImageTokenCount(string text)
    {
        MatchCollection matches = ImageTokenBatchPattern.Matches(text);
        if (matches.Count == 0) return null;
        long total = 0;
        foreach (Match match in matches)
        {
            if (int.TryParse(match.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int tokens)) total += tokens;
        }
        return total is > 0 and <= int.MaxValue ? (int) total : null;
    }

    private static double? FindDrafted(MetricSnapshot snapshot) => FindByFragments(snapshot, "draft", "token");
    private static double? FindAccepted(MetricSnapshot snapshot) => FindByFragments(snapshot, "accept", "token");

    private static double? FindByFragments(MetricSnapshot snapshot, params string[] fragments)
    {
        foreach ((string key, double value) in snapshot.Values)
        {
            if (fragments.All(fragment => key.Contains(fragment, StringComparison.OrdinalIgnoreCase))) return value;
        }
        return null;
    }
}
