using FlashNext.Core.Models;
using System.IO;

namespace FlashNext.Dashboard;

public sealed partial class DashboardSession
{
    private bool _sessionOp;
    private TaskCompletionSource? _sessionFinished;
    public string[] SessionNames { get; private set; } = [];
    public string? ActiveSession { get; private set; }
    public bool CanChangeSession => !_busy && !_sessionOp && !_disposed;
    public bool IsChatBusy => !CanChangeSession;
    public string ContextNote { get; private set; } = "Sessions keep the transcript; the model uses only the recent exchanges that fit its context.";

    private void NotifyChatAvailability()
    {
        OnPropertyChanged(nameof(CanChangeSession));
        OnPropertyChanged(nameof(IsChatBusy));
        OnPropertyChanged(nameof(CanSend));
    }

    private void RefreshSessions()
    {
        SessionNames = [.. Conversations.List(mostRecentFirst: true)];
        OnPropertyChanged(nameof(SessionNames));
        OnPropertyChanged(nameof(ActiveSession));
    }

    private async Task SaveCurrentSessionAsync()
    {
        if (!_settings.Chat.SaveConversations) return;
        await _sessionSaveGate.WaitAsync().ConfigureAwait(true);
        try
        {
            List<ChatMessage> messages = [new("system", string.IsNullOrWhiteSpace(SystemPrompt) ? _settings.Chat.SystemPrompt : SystemPrompt)];
            messages.AddRange(Messages.Where(line => !line.IsError && (line.Role is "You" or "Assistant") && (line.Text.Length > 0 || line.Thinking.Length > 0))
                .Select(line => new ChatMessage(line.Role == "You" ? "user" : "assistant", line.Text)
                {
                    ReasoningContent = line.Thinking.Length > 0 ? line.Thinking : null,
                    ImageDataUrl = line.ImageDataUrl,
                    RunId = line.RunId,
                    Status = !line.Pending && line.Status.Length > 0 ? line.Status : null
                }));
            if (ActiveSession is null && messages.Count == 1 && string.IsNullOrWhiteSpace(Draft) && _runs.Count == 0) return;
            string title = messages.FirstOrDefault(message => message.Role == "user")?.Content ?? Draft;
            title = new string(title.Where(c => char.IsLetterOrDigit(c) || c == ' ').Take(48).ToArray()).Trim();
            string name = ActiveSession ?? $"{(title.Length > 0 ? title : "Chat")} - {DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
            ConversationDocument document = new() { Name = name, Messages = messages, Draft = Draft, Runs = [.. _runs] };
            await Conversations.SaveDocumentAsync(document).ConfigureAwait(true);
            foreach (PromptRunReport saved in document.Runs)
            {
                int index = _runs.FindIndex(run => run.Id == saved.Id);
                if (index >= 0 && saved.RatingRevision > _runs[index].RatingRevision)
                    _runs[index] = _runs[index] with { Rating = saved.Rating, RatingRevision = saved.RatingRevision };
            }
            ActiveSession = name;
            RefreshSessions();
        }
        finally { _sessionSaveGate.Release(); }
    }

    // A null name creates a new session; the old transcript is saved before changing anything.
    public async Task ChangeSessionAsync(string? name)
    {
        if (!CanChangeSession || (name is not null && name == ActiveSession)) return;
        _sessionOp = true;
        _sessionFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NotifyChatAvailability();
        try
        {
            if (!_settings.Chat.SaveConversations)
                throw new InvalidOperationException("Saved sessions are disabled. Enable chat.saveConversations in settings.json to use them.");
            await SaveCurrentSessionAsync().ConfigureAwait(true);
            ConversationDocument? document = name is null ? null : await Conversations.LoadDocumentAsync(name).ConfigureAwait(true);
            if (document is not null)
            {
                if (document.Messages.Any(message => message.Role == "tool") || document.Messages.Skip(1).Any(message => message.Role == "system"))
                    throw new InvalidDataException("This session contains tool messages or embedded system messages that the dashboard cannot resume.");
                // Touch the opened session so it is the one restored on the next launch.
                document.Runs = document.Runs.Select(run => run.RecoverInterrupted()).ToList();
                await Conversations.SaveDocumentAsync(document).ConfigureAwait(true);
            }
            Messages.Clear();
            _runs.Clear();
            if (document is not null) _runs.AddRange(document.Runs);
            ActiveSession = name;
            Draft = document?.Draft ?? string.Empty;
            if (document is not null)
            {
                SystemPrompt = document.Messages.FirstOrDefault(message => message.Role == "system")?.Content ?? _settings.Chat.SystemPrompt;
                foreach (ChatMessage message in document.Messages.Where(message => message.Role != "system"))
                    Messages.Add(new ChatLine(message.Role == "user" ? "You" : "Assistant", message.Content, false)
                    {
                        Thinking = message.ReasoningContent ?? string.Empty,
                        ImageDataUrl = message.ImageDataUrl,
                        RunId = message.RunId,
                        Status = message.Status ?? string.Empty
                    });
            }
            MetricsText = string.Empty;
            RefreshInlineReports();
            ContextNote = "Sessions keep the transcript; the model uses only the recent exchanges that fit its context.";
            ChatProgress = name is null ? "New session. Your previous session is saved." : "Session restored. Continue chatting here.";
            LastError = string.Empty;
            RefreshSessions();
            OnPropertyChanged(nameof(SystemPrompt));
            OnPropertyChanged(nameof(MetricsText));
            OnPropertyChanged(nameof(ContextNote));
            OnPropertyChanged(nameof(ChatProgress));
            OnPropertyChanged(nameof(LastError));
            TranscriptUpdated?.Invoke();
        }
        catch (Exception ex) { ReportSessionError(ex); }
        finally
        {
            _sessionOp = false;
            OnPropertyChanged(nameof(ActiveSession));
            NotifyChatAvailability();
            _sessionFinished.TrySetResult();
        }
    }

    private void ReportSessionError(Exception ex)
    {
        LastError = "Session could not be saved or opened: " + ex.Message + " The current transcript is still here.";
        OnPropertyChanged(nameof(LastError));
    }
}
