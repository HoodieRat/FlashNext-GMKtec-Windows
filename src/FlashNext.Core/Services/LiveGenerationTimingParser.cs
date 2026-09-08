using System.Globalization;
using System.Text.RegularExpressions;

namespace FlashNext.Core.Services;

public sealed record LiveGenerationTiming(long TaskId, long GeneratedTokens, double AverageTps, double RecentTps);

/// <summary>Reads the pinned runtime's slot timing lines, not completion-only Prometheus totals.</summary>
public sealed class LiveGenerationTimingParser
{
    private static readonly Regex Header = new(@"^(?:\d+(?:\.\d+){3}\s+[IWD]\s+)?slot\s+(?<kind>launch_slot_|print_timing|release):\s+id\s+(?<slot>\d+)\s*\|\s*task\s+(?<task>\d+)\s*\|(?<body>.*)$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex Timing = new(@"^\s*n_gen\s*=\s*(?<tokens>\d+),\s*tg\s*=\s*(?<average>\d+(?:\.\d+)?)\s+t/s,\s*tg_3s\s*=\s*(?<recent>\d+(?:\.\d+)?)\s+t/s\s*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private long? _task;
    private long _tokens;
    public long? CurrentTaskId => _task;

    public void Reset() { _task = null; _tokens = 0; }

    public LiveGenerationTiming? AcceptLine(string line)
    {
        if (line.Length > 16384) return null;
        Match header = Header.Match(line);
        if (!header.Success || header.Groups["slot"].Value != "0" ||
            !long.TryParse(header.Groups["task"].Value, out long task)) return null;
        string kind = header.Groups["kind"].Value;
        if (kind == "launch_slot_")
        {
            _task = task;
            _tokens = 0;
            return null;
        }
        if (task != _task) return null;
        if (kind == "release") { Reset(); return null; }
        Match timing = Timing.Match(header.Groups["body"].Value);
        if (!timing.Success || !long.TryParse(timing.Groups["tokens"].Value, out long tokens) || tokens <= _tokens ||
            !double.TryParse(timing.Groups["average"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double average) ||
            !double.TryParse(timing.Groups["recent"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double recent) ||
            !double.IsFinite(average) || !double.IsFinite(recent) || average <= 0 || recent <= 0) return null;
        _tokens = tokens;
        return new(task, tokens, average, recent);
    }
}
