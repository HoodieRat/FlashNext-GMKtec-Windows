using FlashNext.Core.Models;
using FlashNext.Core.Services;
using Spectre.Console;

namespace FlashNext.Manager.ConsoleUi;

public sealed class ChatConsole(ManagerServices services)
{
    private readonly List<ChatMessage> _messages = [];
    private ResponseMetrics? _lastMetrics;
    private ConversationDocument? _loadedDocument;
    private CancellationTokenSource? _activeRequest;

    public async Task RunAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        string apiKey = await services.Secrets.GetApiKeyAsync(cancellationToken).ConfigureAwait(false);
        string host = settings.Lan.Enabled ? settings.Lan.BindAddress : settings.Server.Host;
        if (host is "0.0.0.0" or "::") host = "127.0.0.1";
        Uri uri = new($"http://{(host.Contains(':', StringComparison.Ordinal) ? "[" + host + "]" : host)}:{settings.Server.Port}");
        using LlamaApiClient client = new(uri, apiKey, TimeSpan.FromSeconds(settings.Chat.RequestTimeoutSeconds));
        _messages.Clear();
        _messages.Add(new ChatMessage("system", settings.Chat.SystemPrompt));
        ConsoleFormatting.Header();
        AnsiConsole.MarkupLine("[bold green]Interactive chat[/]  [grey]Enter sends. End a line with \\ to continue it. Ctrl+C cancels the active response.[/]");
        AnsiConsole.MarkupLine("Commands: [cyan]/new /system /save /load /stats /profile /thinking /cancel /exit[/]");
        if (settings.GetActiveProfile().Thinking) AnsiConsole.MarkupLine("[grey]Thinking is on for this profile. Grey text is model reasoning; the answer follows it.[/]");
        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            if (_activeRequest is not null && !_activeRequest.IsCancellationRequested)
            {
                e.Cancel = true;
                _activeRequest.Cancel();
            }
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? input = ReadMultiline();
                if (input is null) return;
                if (input.Length == 0) continue;
                if (input.StartsWith("/", StringComparison.Ordinal))
                {
                    bool leave = await HandleCommandAsync(input, settings, cancellationToken).ConfigureAwait(false);
                    if (leave) return;
                    continue;
                }
                await SendAsync(client, settings, input, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            _activeRequest?.Dispose();
        }
    }

    private async Task SendAsync(LlamaApiClient client, AppSettings settings, string input, CancellationToken cancellationToken)
    {
        _messages.Add(new ChatMessage("user", input));
        ServerStatus requestStatus = services.Supervisor.Status;
        RuntimeConfiguration? requestRuntime = requestStatus.Configuration;
        MetricSnapshot before;
        try { before = await client.GetMetricsAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { before = new MetricSnapshot(); }
        AnsiConsole.Write(new Rule("[cyan]Assistant[/]").LeftJustified());
        AnsiConsole.MarkupLine("[grey]Waiting for first token...[/]");
        using CancellationTokenSource request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activeRequest = request;
        ChatCompletionResult completion;
        bool firstVisible = true;
        bool thinkingHeader = false;
        bool answerHeader = false;
        try
        {
            completion = await client.StreamChatAsync(_messages, settings.GetActiveProfile(), chunk =>
            {
                if (chunk.ReasoningContent.Length > 0)
                {
                    if (firstVisible) { firstVisible = false; AnsiConsole.MarkupLine("[grey]First token received.[/]"); }
                    if (!thinkingHeader) { thinkingHeader = true; AnsiConsole.MarkupLine("[grey]Thinking:[/]"); }
                    AnsiConsole.Write(new Text(chunk.ReasoningContent, new Style(Color.Grey)));
                }
                if (chunk.Content.Length > 0)
                {
                    if (firstVisible) { firstVisible = false; AnsiConsole.MarkupLine("[grey]First token received.[/]"); }
                    if (!answerHeader) { answerHeader = true; if (thinkingHeader) AnsiConsole.WriteLine(); AnsiConsole.MarkupLine("[cyan]Answer:[/]"); }
                    AnsiConsole.Write(new Text(chunk.Content));
                }
                return Task.CompletedTask;
            }, request.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            RemovePendingUserMessage();
            AnsiConsole.MarkupLine("\n[yellow]Response cancelled.[/]");
            return;
        }
        catch
        {
            RemovePendingUserMessage();
            throw;
        }
        finally
        {
            if (ReferenceEquals(_activeRequest, request)) _activeRequest = null;
        }
        AnsiConsole.WriteLine();
        _messages.Add(new ChatMessage("assistant", completion.Content) { ReasoningContent = completion.ReasoningContent });
        MetricSnapshot after;
        try { after = await client.GetMetricsAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { after = before; }
        SystemTelemetry telemetry = await services.Telemetry.CaptureAsync(services.Supervisor.Status.ProcessId, cancellationToken).ConfigureAwait(false);
        _lastMetrics = MetricCalculator.Build(completion, before, after, telemetry, settings.ActiveProfile, requestRuntime?.ContextSize ?? settings.GetActiveProfile().ContextSize, runtime: requestRuntime, launch: requestStatus.Launch);
        ConsoleFormatting.ShowMetrics(_lastMetrics);
        MetricsWriter writer = new(services.Paths.MetricsDirectory, settings.Metrics);
        await writer.WriteAsync(_lastMetrics, cancellationToken).ConfigureAwait(false);
    }

    private void RemovePendingUserMessage()
    {
        if (_messages.Count > 0 && _messages[^1].Role.Equals("user", StringComparison.OrdinalIgnoreCase)) _messages.RemoveAt(_messages.Count - 1);
    }

    private async Task<bool> HandleCommandAsync(string input, AppSettings settings, CancellationToken cancellationToken)
    {
        string[] parts = input.Split(' ', 2, StringSplitOptions.TrimEntries);
        string command = parts[0].ToLowerInvariant();
        string argument = parts.Length > 1 ? parts[1] : string.Empty;
        switch (command)
        {
            case "/exit": return true;
            case "/new":
                _loadedDocument = null;
                _messages.Clear(); _messages.Add(new ChatMessage("system", settings.Chat.SystemPrompt)); _lastMetrics = null;
                AnsiConsole.MarkupLine("[green]New conversation started.[/]"); break;
            case "/system":
                if (string.IsNullOrWhiteSpace(argument)) argument = AnsiConsole.Ask<string>("New system prompt:");
                settings.Chat.SystemPrompt = argument;
                if (_messages.Count > 0 && _messages[0].Role == "system") _messages[0] = new ChatMessage("system", argument); else _messages.Insert(0, new ChatMessage("system", argument));
                await services.Settings.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
                AnsiConsole.MarkupLine("[green]System prompt updated.[/]"); break;
            case "/save":
                if (string.IsNullOrWhiteSpace(argument)) argument = AnsiConsole.Ask<string>("Conversation name:");
                await services.Conversations.SaveDocumentAsync(new ConversationDocument { Name = argument, Messages = [.. _messages], Draft = _loadedDocument?.Draft ?? string.Empty, Runs = _loadedDocument?.Runs ?? [] }, cancellationToken).ConfigureAwait(false);
                AnsiConsole.MarkupLine("[green]Conversation saved.[/]"); break;
            case "/load":
                if (string.IsNullOrWhiteSpace(argument))
                {
                    IReadOnlyList<string> names = services.Conversations.List();
                    if (names.Count == 0) { AnsiConsole.MarkupLine("[yellow]No saved conversations.[/]"); break; }
                    argument = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("Load conversation").AddChoices(names));
                }
                _loadedDocument = await services.Conversations.LoadDocumentAsync(argument, cancellationToken).ConfigureAwait(false);
                _messages.Clear(); _messages.AddRange(_loadedDocument.Messages);
                AnsiConsole.MarkupLine("[green]Conversation loaded.[/]"); break;
            case "/stats":
                if (_lastMetrics is null) AnsiConsole.MarkupLine("[yellow]No response metrics are available yet.[/]"); else ConsoleFormatting.ShowMetrics(_lastMetrics); break;
            case "/profile":
                if (string.IsNullOrWhiteSpace(argument)) argument = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("Profile").AddChoices(settings.Profiles.Keys));
                if (!settings.Profiles.ContainsKey(argument)) { AnsiConsole.MarkupLine("[red]Unknown profile.[/]"); break; }
                settings.ActiveProfile = argument; await services.Settings.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
                AnsiConsole.MarkupLine($"[green]Active profile:[/] {Markup.Escape(argument)}. Restart the server if context or MTP n-max changed."); break;
            case "/thinking":
                bool enabled = argument.Equals("on", StringComparison.OrdinalIgnoreCase) || (!argument.Equals("off", StringComparison.OrdinalIgnoreCase) && AnsiConsole.Confirm("Enable thinking?", settings.GetActiveProfile().Thinking));
                settings.GetActiveProfile().Thinking = enabled; await services.Settings.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
                AnsiConsole.MarkupLine(enabled ? "[green]Thinking enabled.[/]" : "[green]Thinking disabled.[/]"); break;
            case "/cancel":
                _activeRequest?.Cancel(); AnsiConsole.MarkupLine("[grey]No active request is waiting for cancellation.[/]"); break;
            default: AnsiConsole.MarkupLine("[yellow]Unknown command.[/]"); break;
        }
        return false;
    }

    private static string? ReadMultiline()
    {
        AnsiConsole.Markup("[bold cyan]You>[/] ");
        List<string> lines = [];
        while (true)
        {
            string? line = Console.ReadLine();
            if (line is null) return null;
            if (lines.Count == 0 && line.StartsWith("/", StringComparison.Ordinal)) return line.Trim();
            bool continueLine = line.EndsWith('\\') && (line.Length == 1 || line[^2] != '\\');
            lines.Add(continueLine ? line[..^1] : line);
            if (!continueLine) break;
            AnsiConsole.Markup("[grey]... [/]");
        }
        return string.Join(Environment.NewLine, lines).Trim();
    }
}
