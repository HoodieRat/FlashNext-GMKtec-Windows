using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows.Data;
using FlashNext.Core.Models;
using FlashNext.Core.Services;

namespace FlashNext.Dashboard;

public sealed partial class DashboardSession
{
    private readonly List<PromptRunReport> _runs = [];
    private readonly SemaphoreSlim _sessionSaveGate = new(1, 1);
    private readonly SemaphoreSlim _ratingGate = new(1, 1);
    private List<ReportSessionRow> _reportSessions = [];
    private ReportSessionRow? _selectedReportSession;
    private ReportRunRow? _selectedReportRun;
    private string _reportSearch = string.Empty;
    private int _reportRefreshVersion;
    public int[] Ratings { get; } = [0, 1, 2, 3, 4, 5];
    public string ReportsNotice { get; private set; } = "Ratings: 0 = unrated, 1–5 = your output-quality rating. Best = rating, then generation speed.";
    public string ReportSearch { get => _reportSearch; set { _reportSearch = value; FilterReports(); } }
    public ReportSessionRow[] ReportSessions { get; private set; } = [];
    public ICollectionView? ReportRuns { get; private set; }
    public ReportSessionRow? SelectedReportSession
    {
        get => _selectedReportSession;
        set
        {
            _selectedReportSession = value;
            IEnumerable<ReportRunRow> rows = value?.Runs ?? [];
            if (!string.IsNullOrWhiteSpace(ReportSearch) && value?.Name.Contains(ReportSearch, StringComparison.OrdinalIgnoreCase) != true)
                rows = rows.Where(row => row.Prompt.Contains(ReportSearch, StringComparison.OrdinalIgnoreCase));
            ReportRuns = new ListCollectionView(rows.OrderBy(row => row.RatingValue == 0).ThenByDescending(row => row.RatingValue)
                .ThenByDescending(row => row.GenerationSpeed).ThenByDescending(row => row.Report.StartedAtUtc).ToList());
            ReportRuns.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ReportRunRow.RatingGroup)));
            string? bestId = value is null ? null : PromptRunReport.Best(value.Runs.Select(row => row.Report))?.Id;
            SelectedReportRun = ReportRuns.Cast<ReportRunRow>().FirstOrDefault(row => row.Report.Id == bestId) ?? ReportRuns.Cast<ReportRunRow>().FirstOrDefault();
            OnPropertyChanged();
            OnPropertyChanged(nameof(ReportRuns));
        }
    }
    public ReportRunRow? SelectedReportRun
    {
        get => _selectedReportRun;
        set { _selectedReportRun = value; OnPropertyChanged(); OnPropertyChanged(nameof(SelectedReportDetails)); }
    }
    public string SelectedReportDetails => SelectedReportRun?.Details ?? "Select a saved run. Legacy replies have no saved configuration report.";

    public async Task RefreshReportsAsync()
    {
        int version = ++_reportRefreshVersion;
        string? selectedSession = SelectedReportSession?.Name;
        string? selectedRun = SelectedReportRun?.Report.Id;
        List<ReportSessionRow> sessions = [];
        List<string> unreadable = [];
        HashSet<string> savedCurrentIds = [];
        bool currentIncluded = false;
        string? readError = null;
        IReadOnlyList<string> names;
        try { names = Conversations.List(mostRecentFirst: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            readError = "Saved reports could not be opened: " + ex.Message;
            names = [];
        }
        foreach (string name in names)
        {
            try
            {
                ConversationDocument doc = await Conversations.LoadDocumentAsync(name).ConfigureAwait(true);
                if (name == ActiveSession) savedCurrentIds.UnionWith(doc.Runs.Select(run => run.Id));
                bool useCurrent = name == ActiveSession && _settings.Chat.SaveConversations;
                IEnumerable<PromptRunReport> reports = useCurrent ? _runs : doc.Runs.Select(run => run.RecoverInterrupted());
                ILookup<string, string> prompts = useCurrent ? CurrentPromptsByRunId()
                    : doc.Messages.Where(message => message.Role == "user" && message.RunId is not null)
                        .ToLookup(message => message.RunId!, message => message.Content, StringComparer.Ordinal);
                sessions.Add(new(name, reports.Select(run => new ReportRunRow(
                    useCurrent && !savedCurrentIds.Contains(run.Id) ? null : name, run, prompts[run.Id].FirstOrDefault())).ToList()));
                currentIncluded |= useCurrent;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            { unreadable.Add(name); }
        }
        ILookup<string, string> currentPrompts = CurrentPromptsByRunId();
        List<ReportRunRow> temporary = currentIncluded ? [] : _runs.Where(run => !savedCurrentIds.Contains(run.Id))
            .Select(run => new ReportRunRow(null, run, currentPrompts[run.Id].FirstOrDefault())).ToList();
        if (temporary.Count > 0) sessions.Insert(0, new("Current session (temporary)", temporary));
        if (version != _reportRefreshVersion) return;
        _reportSessions = sessions;
        FilterReports(selectedSession);
        if (selectedRun is not null && ReportRuns?.Cast<ReportRunRow>().FirstOrDefault(row => row.Report.Id == selectedRun) is { } selected)
            SelectedReportRun = selected;
        ReportsNotice = (!_settings.Chat.SaveConversations ? "Conversation saving is disabled: current reports are TEMPORARY. " : "")
            + (sessions.Any(session => session.Runs.Any(row => row.SessionName is null)) && _settings.Chat.SaveConversations ? "Some reports have not been saved and are TEMPORARY. " : "")
            + "Ratings: 0 = unrated, 1–5 = quality. Best = rating, then generation speed."
            + (readError is null ? "" : " " + readError)
            + (unreadable.Count > 0 ? $" Could not read {unreadable.Count} session(s): {string.Join(", ", unreadable)}." : "");
        OnPropertyChanged(nameof(ReportsNotice));
    }

    private void FilterReports(string? select = null)
    {
        ReportSessions = _reportSessions.Where(session => string.IsNullOrWhiteSpace(ReportSearch)
            || session.Name.Contains(ReportSearch, StringComparison.OrdinalIgnoreCase)
            || session.Runs.Any(run => run.Prompt.Contains(ReportSearch, StringComparison.OrdinalIgnoreCase))).ToArray();
        OnPropertyChanged(nameof(ReportSessions));
        SelectedReportSession = ReportSessions.FirstOrDefault(session => session.Name == (select ?? SelectedReportSession?.Name)) ?? ReportSessions.FirstOrDefault();
    }

    public async Task RateRunAsync(ReportRunRow row, int ratingValue)
    {
        int? rating = ratingValue == 0 ? null : ratingValue;
        if (rating is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(ratingValue));
        await _ratingGate.WaitAsync().ConfigureAwait(true);
        try
        {
            PromptRunReport updated = row.SessionName is not null
                ? await Conversations.UpdateRatingAsync(row.SessionName, row.Report.Id, rating).ConfigureAwait(true)
                : row.Report with { Rating = rating, RatingRevision = row.Report.RatingRevision + 1 };
            if (row.SessionName == ActiveSession || row.SessionName is null)
            {
                int index = _runs.FindIndex(run => run.Id == updated.Id);
                if (index >= 0) _runs[index] = _runs[index] with { Rating = updated.Rating, RatingRevision = updated.RatingRevision };
                RefreshInlineReports();
            }
            await RefreshReportsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ReportsNotice = "Rating was not saved: " + ex.Message;
            OnPropertyChanged(nameof(ReportsNotice));
            OnPropertyChanged(nameof(SelectedReportRun));
        }
        finally { _ratingGate.Release(); }
    }

    public async Task<ChatLine?> OpenReportInChatAsync(ReportRunRow row)
    {
        if (!CanChangeSession) { ReportsNotice = "Wait for the current reply, or stop it, before opening another run."; OnPropertyChanged(nameof(ReportsNotice)); return null; }
        if (row.SessionName is not null && row.SessionName != ActiveSession) await ChangeSessionAsync(row.SessionName).ConfigureAwait(true);
        if (row.SessionName is not null && row.SessionName != ActiveSession) return null;
        ChatLine? line = Messages.LastOrDefault(message => message.RunId == row.Report.Id);
        MetricsText = row.Details;
        if (line is null) ChatProgress = "This attempt has no retained message. Its prompt is in the Reports list; its report is in the center panel.";
        OnPropertyChanged(nameof(MetricsText));
        OnPropertyChanged(nameof(ChatProgress));
        return line;
    }

    private void ReplaceRun(PromptRunReport report)
    {
        int index = _runs.FindIndex(run => run.Id == report.Id);
        if (index < 0) _runs.Add(report);
        else _runs[index] = report with { Rating = _runs[index].Rating, RatingRevision = _runs[index].RatingRevision };
        RefreshInlineReports();
    }

    private void RefreshInlineReports()
    {
        foreach (ChatLine line in Messages.Where(line => line.Role == "Assistant"))
            line.RunReport = _runs.FirstOrDefault(run => run.Id == line.RunId);
        if (_runs.LastOrDefault() is { } latest)
        {
            MetricsText = PromptRunFormatter.Format(latest);
            OnPropertyChanged(nameof(MetricsText));
        }
    }

    private ILookup<string, string> CurrentPromptsByRunId() => Messages
        .Where(line => line.Role == "You" && !line.IsError && line.RunId is not null)
        .ToLookup(line => line.RunId!, line => line.Text, StringComparer.Ordinal);

    private PromptRunReport CaptureRun(string systemPrompt, InferenceProfile profile, bool hasImage)
        => PromptRunCapture.Begin(systemPrompt, ActiveProfile, profile, Supervisor.Status, hasImage);
}

public sealed record ReportSessionRow(string Name, List<ReportRunRow> Runs)
{
    public int RunCount => Runs.Count;
    public string Best => PromptRunReport.Best(Runs.Select(row => row.Report)) is { } best
        ? $"Rated {best.Rating}/5  |  TG {PromptRunReport.ValidSpeed(best.Metrics?.GenerationTokensPerSecond)?.ToString("0.0") ?? "N/A"} tok/s  |  {best.Runtime?.MtpMode ?? "MTP unknown"}  |  Batch {best.Runtime?.BatchSize.ToString() ?? "N/A"}/{best.Runtime?.UBatchSize.ToString() ?? "N/A"}"
        : "No rated completed run";
}

public sealed record ReportRunRow(string? SessionName, PromptRunReport Report, string? LinkedPrompt = null)
{
    public DateTime Started => Report.StartedAtUtc.LocalDateTime;
    public string Prompt => LinkedPrompt ?? (string.IsNullOrEmpty(Report.Prompt) ? "Prompt unavailable" : Report.Prompt);
    public string Status => Report.Status.ToString();
    public int RatingValue => Report.Rating ?? 0;
    public string RatingGroup => Report.Rating.HasValue ? "Rated" : "Unrated";
    public double? GenerationSpeed => PromptRunReport.ValidSpeed(Report.Metrics?.GenerationTokensPerSecond);
    public double? EffectiveSpeed => PromptRunReport.ValidSpeed(Report.Metrics?.EffectiveTokensPerSecond);
    public string Mtp => Report.Runtime?.MtpMode ?? "Unknown";
    public string Batch => Report.Runtime is { } r ? $"{r.BatchSize}/{r.UBatchSize}" : "Unknown";
    public string Details => PromptRunFormatter.Format(Report);
}
