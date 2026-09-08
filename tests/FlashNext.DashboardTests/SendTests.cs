using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using FlashNext.Core.Models;
using FlashNext.Dashboard;
using FlashNext.Infrastructure.Windows.System;
using FlashNext.UnitTests;
using Xunit;

namespace FlashNext.DashboardTests;

public sealed class SendTests
{
    [Fact]
    public Task LiveTpsAppearsDuringStreamEvenWhenPrometheusCountersStayZero() => SessionTests.OnDispatcher(async () =>
    {
        using TestDirectory directory = new();
        await using StubServer server = new(100, partialReply: false, completeReply: true, streamFirstChunk: true);
        await using DashboardSession session = await CreateAttachedSession(directory, server);
        // A synthetic owned-server status and private temp log; never attach to a real model.
        ServerStatus stubStatus = (ServerStatus)session.Supervisor.GetType().GetField("_status", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session.Supervisor)!;
        stubStatus.IsOwnedProcess = true;
        string log = Path.Combine(session.Paths.LogsDirectory, "server-error.log");
        await File.WriteAllTextAsync(log, string.Empty);
        TaskCompletionSource receiving = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource live = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(session.LiveTpsState)) return;
            if (session.LiveTpsState == "GENERATING • WAITING FOR TIMING") receiving.TrySetResult();
            if (session.LiveTpsState == "LIVE • ~3 SECOND WINDOW") live.TrySetResult();
        };
        Task sending = session.SendAsync("offline live telemetry test");
        try
        {
            await receiving.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(sending.IsCompleted);
            Assert.Equal("—", session.LiveTpsText);
            await File.AppendAllTextAsync(log, "slot launch_slot_: id 0 | task 1 | processing task\n" +
                "slot print_timing: id 0 | task 1 | n_gen = 120, tg = 32.02 t/s, tg_3s = 36.79 t/s\n");
            await live.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(sending.IsCompleted);
            Assert.Equal("36.8", session.LiveTpsText);
        }
        finally { server.ContinueReply.TrySetResult(); await sending; }
        Assert.Equal("10.0", session.LiveTpsText); // Final request average remains authoritative.
        Assert.Equal("LAST COMPLETED RUN", session.LiveTpsState);
    });

    [Fact]
    public Task ReportsFreezeActualRequestSettingsAndSurviveLaterPromptsAndReopening() => SessionTests.OnDispatcher(async () =>
    {
        using TestDirectory directory = new();
        await using StubServer server = new(100, partialReply: false, completeReply: true, holdResponse: true);
        await using DashboardSession session = await CreateAttachedSession(directory, server);
        ServerStatus active = (ServerStatus)session.Supervisor.GetType().GetField("_status", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session.Supervisor)!;
        active.Configuration = new(8192, 4, "draft-mtp", false, 0, 2048, 512, "balanced", server.Port, "127.0.0.1", 99, 99, "");
        active.Artifacts = new("actual model", "actual runtime", "actual template");
        session.Temperature = 0.2;
        session.PresencePenalty = 1.5;
        session.MtpMode = "fixed-6"; // Pending, not the running configuration.
        session.BatchSize = 4096;
        session.MaxOutputTokens = 9000;
        session.SystemPrompt = "original system";
        Task sending = session.SendAsync("write skate game");
        await server.RequestReceived.Task;
        ConversationDocument inFlight = await session.Conversations.LoadDocumentAsync(session.ActiveSession!);
        Assert.Equal(PromptRunStatus.Running, Assert.Single(inFlight.Runs).Status);
        Assert.Null(inFlight.Runs[0].Prompt);
        Assert.Equal("write skate game", inFlight.Messages.Single(message => message.Role == "user" && message.RunId == inFlight.Runs[0].Id).Content);
        session.Temperature = 0.7;
        session.SystemPrompt = "later system";
        server.ContinueReply.TrySetResult();
        await sending;
        string name = session.ActiveSession!;
        PromptRunReport first = Assert.Single((await session.Conversations.LoadDocumentAsync(name)).Runs);
        Assert.Equal(PromptRunStatus.Completed, first.Status);
        Assert.Null(first.Prompt);
        Assert.DoesNotContain("write skate game", JsonSerializer.Serialize(first));
        Assert.Equal("original system", first.SystemPrompt);
        Assert.Equal(0.2, first.Sampling!.Temperature);
        Assert.Equal(1.5, first.Sampling.PresencePenalty);
        Assert.Equal(9000, first.Sampling.RequestedMaxOutputTokens);
        Assert.Equal(8060, first.EffectiveMaxOutputTokens);
        Assert.Equal(8192, first.ContextSize);
        Assert.Equal(4, first.Runtime!.MtpNMax);
        Assert.Equal("actual model", first.ModelIdentity);
        Assert.Equal("actual runtime", first.RuntimeIdentity);
        Assert.Equal(2048, first.Runtime.BatchSize);
        Assert.Equal(10, first.Metrics!.GenerationTokensPerSecond);
        Assert.Equal(first.Id, session.Messages.Last().RunId);
        await Task.WhenAll(session.SendAsync("change the controls"), session.RateRunAsync(new(name, first), 5));
        Assert.DoesNotContain("runId", server.LastChatBody);
        Assert.DoesNotContain(first.Id, server.LastChatBody);
        Assert.DoesNotContain("generationTokensPerSecond", server.LastChatBody);
        ConversationDocument saved = await session.Conversations.LoadDocumentAsync(name);
        Assert.Equal(2, saved.Runs.Count);
        Assert.Equal(5, saved.Runs[0].Rating);
        Assert.Equal(PromptRunStatus.Completed, saved.Runs[1].Status);
        Assert.Equal(name, session.ActiveSession);
        Assert.Equal(0.2, saved.Runs[0].Sampling!.Temperature);
        Assert.Equal(0.7, saved.Runs[1].Sampling!.Temperature);
        Assert.Contains("TG 10", session.Messages[1].ReportHeadline);
        await session.ChangeSessionAsync(null);
        await session.ChangeSessionAsync(name);
        Assert.Equal(first.Id, session.Messages[1].RunReport!.Id);
        Assert.DoesNotContain("change the controls", session.MetricsText);
        await session.RefreshReportsAsync();
        session.ReportSearch = "change the controls";
        ReportRunRow currentRow = Assert.Single(session.ReportRuns!.Cast<ReportRunRow>());
        Assert.Equal("change the controls", currentRow.Prompt);
        Assert.Null(currentRow.Report.Prompt);
        Assert.Equal(saved.Runs[1].Id, (await session.OpenReportInChatAsync(currentRow))!.RunId);
        await using DashboardSession reopened = new(new PlatformPaths(directory.Path, directory.Path));
        await reopened.ChangeSessionAsync(name);
        Assert.Equal(first.Id, reopened.Messages[1].RunReport!.Id);
        Assert.Contains("Temperature: 0.2", reopened.Messages[1].ReportDetails);
        await reopened.RefreshReportsAsync();
        reopened.ReportSearch = "write skate game";
        Assert.Equal("write skate game", reopened.ReportRuns!.Cast<ReportRunRow>().Single(row => row.Report.Id == first.Id).Prompt);
    });

    [Theory]
    [InlineData("stop", PromptRunStatus.Completed)]
    [InlineData("length", PromptRunStatus.TokenLimited)]
    public Task OptionalMetricLogFailureDoesNotLoseOrMislabelFinishedReports(string finishReason, PromptRunStatus expected) => SessionTests.OnDispatcher(async () =>
    {
        using TestDirectory directory = new();
        await using StubServer server = new(100, partialReply: false, completeReply: true, finishReason: finishReason);
        await using DashboardSession session = await CreateAttachedSession(directory, server);
        AppSettings settings = (AppSettings)typeof(DashboardSession).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        settings.Metrics.IncludePromptOrResponse = true; // Writer must reject this, without losing the report.
        await session.SendAsync("write game");
        PromptRunReport report = Assert.Single((await session.Conversations.LoadDocumentAsync(session.ActiveSession!)).Runs);
        Assert.Equal(expected, report.Status);
        Assert.Equal("finished code", session.Messages.Last().Text);
        Assert.Contains("optional metric log", session.LastError);
        Assert.NotNull(report.Metrics);
        Assert.Null(report.Error);
    });

    [Fact]
    public Task StoppedAndTemporaryRunsRemainInspectableWithoutFabricatedSpeeds() => SessionTests.OnDispatcher(async () =>
    {
        using TestDirectory directory = new();
        await using StubServer server = new(100, partialReply: false, completeReply: true, holdResponse: true);
        await using DashboardSession session = await CreateAttachedSession(directory, server);
        Task send = session.SendAsync("cancel this");
        await server.RequestReceived.Task;
        session.StopChat();
        await send;
        server.ContinueReply.TrySetResult();
        PromptRunReport stopped = Assert.Single((await session.Conversations.LoadDocumentAsync(session.ActiveSession!)).Runs);
        Assert.Equal(PromptRunStatus.Stopped, stopped.Status);
        Assert.Null(stopped.Metrics);
        Assert.Equal("cancel this", stopped.Prompt); // No retained user message: keep the searchable fallback.
        Assert.Equal("cancel this", session.Draft);
        AppSettings settings = (AppSettings)typeof(DashboardSession).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        settings.Chat.SaveConversations = false;
        await session.SendAsync("temporary prompt");
        await session.RefreshReportsAsync();
        ReportRunRow temporary = Assert.Single(session.ReportSessions.First(row => row.Name.Contains("temporary")).Runs);
        Assert.Null(temporary.SessionName);
        Assert.Equal("temporary prompt", temporary.Prompt);
        Assert.Null(temporary.Report.Prompt);
        Assert.DoesNotContain("temporary prompt", temporary.Details);
        Assert.Equal(stopped.Id, Assert.Single(session.ReportSessions.Single(row => row.Name == session.ActiveSession).Runs).Report.Id);
        await session.RateRunAsync(temporary, 5);
        Assert.Contains("TEMPORARY", session.ReportsNotice);
        Assert.Single(session.Conversations.List());
        Assert.Contains("Rating 5", session.Messages.Last().ReportHeadline);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task SaveFailuresRetainReportsAndNeverMislabelCompletedOutput(bool beforeRequest) => SessionTests.OnDispatcher(async () =>
    {
        using TestDirectory directory = new();
        await using StubServer server = new(100, partialReply: false, completeReply: true, holdResponse: true);
        await using DashboardSession session = await CreateAttachedSession(directory, server);
        if (beforeRequest)
        {
            Directory.Delete(session.Paths.ConversationsDirectory); // Empty test fixture, never user data.
            await File.WriteAllTextAsync(session.Paths.ConversationsDirectory, "block persistence");
            await session.SendAsync("retain failed attempt");
            Assert.Equal(0, server.ChatRequests);
            Assert.Equal("retain failed attempt", session.Draft);
            await session.RefreshReportsAsync();
            PromptRunReport failed = Assert.Single(Assert.Single(session.ReportSessions).Runs).Report;
            Assert.Equal(PromptRunStatus.Failed, failed.Status);
            Assert.Contains("TEMPORARY", session.ReportsNotice);
            File.Delete(session.Paths.ConversationsDirectory);
            Directory.CreateDirectory(session.Paths.ConversationsDirectory);
        }
        else
        {
            Task sending = session.SendAsync("retain completed attempt");
            await server.RequestReceived.Task;
            string path = Path.Combine(session.Paths.ConversationsDirectory, session.ActiveSession! + ".json");
            using (FileStream blockSave = new(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                server.ContinueReply.TrySetResult();
                await sending;
                Assert.Equal(PromptRunStatus.Completed, session.Messages.Last().RunReport!.Status);
                Assert.Equal(10, session.Messages.Last().RunReport!.Metrics!.GenerationTokensPerSecond);
                Assert.Equal("finished code", session.Messages.Last().Text);
                Assert.Contains("could not be saved", session.LastError);
            }
        }
        await session.ChangeSessionAsync(null); // Retry persistence after the disk problem is resolved.
        PromptRunReport restored = Assert.Single((await session.Conversations.LoadDocumentAsync(Assert.Single(session.SessionNames))).Runs);
        Assert.Equal(beforeRequest ? PromptRunStatus.Failed : PromptRunStatus.Completed, restored.Status);
        Assert.True(session.CanChangeSession);
    });
    [Fact]
    public Task ReplaysReasoningOnlyAssistantTurnsWithoutStatusText() => SessionTests.OnDispatcher(async () =>
    {
        using TestDirectory directory = new();
        await using StubServer server = new(100, partialReply: false);
        await using DashboardSession session = await CreateAttachedSession(directory, server);
        session.Messages.Add(new("You", "old question", false));
        session.Messages.Add(new("Assistant", "", false) { Thinking = "unfinished reasoning\n", Status = "Stopped" });
        await session.SendAsync("continue");
        using JsonDocument sent = JsonDocument.Parse(server.LastChatBody);
        JsonElement messages = sent.RootElement.GetProperty("messages");
        Assert.Equal(4, messages.GetArrayLength());
        Assert.Equal("assistant", messages[2].GetProperty("role").GetString());
        Assert.Equal("unfinished reasoning\n", messages[2].GetProperty("reasoning_content").GetString());
        Assert.Equal("", messages[2].GetProperty("content").GetString());
        Assert.DoesNotContain("Stopped", server.LastChatBody);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task FailedSendRestoresDraftWithoutAccumulatingFailedTurns(bool contextFailure) => SessionTests.OnDispatcher(async () =>
    {
        using TestDirectory directory = new();
        await using StubServer server = new(contextFailure ? 8208 : 100, partialReply: false);
        await using DashboardSession session = await CreateAttachedSession(directory, server);
        session.Draft = "continue and finish the game";
        await session.SendAsync();
        await session.SendAsync(); // Retry must not add a second unanswered user turn.
        Assert.Empty(session.Messages);
        Assert.Equal("continue and finish the game", session.Draft);
        Assert.True(session.CanSend);
        Assert.False(session.CanStopChat);
        Assert.Contains(contextFailure ? "Increase Context size and Restart" : "HTTP 503", session.LastError);
        ConversationDocument saved = await session.Conversations.LoadDocumentAsync(session.ActiveSession!);
        Assert.Equal(session.Draft, saved.Draft);
        Assert.Equal(2, saved.Runs.Count);
        Assert.All(saved.Runs, run => { Assert.Equal(PromptRunStatus.Failed, run.Status); Assert.Null(run.Metrics); });
        Assert.All(saved.Runs, run => Assert.Equal(session.Draft, run.Prompt));
        await session.RefreshReportsAsync();
        session.ReportSearch = "finish the game";
        Assert.Equal(2, session.ReportRuns!.Cast<ReportRunRow>().Count());
        Assert.DoesNotContain(session.Draft, session.SelectedReportDetails);
        Assert.Single(saved.Messages); // System prompt only, failed input remains a draft.
        Assert.Equal(contextFailure ? 0 : 2, server.ChatRequests);
    });

    [Fact]
    public Task InterruptedStreamKeepsPartialAnswerInTranscriptAndSavedSession() => SessionTests.OnDispatcher(async () =>
    {
        using TestDirectory directory = new();
        await using StubServer server = new(100, partialReply: true);
        await using DashboardSession session = await CreateAttachedSession(directory, server);
        await session.SendAsync("write code");
        Assert.Equal(2, session.Messages.Count);
        ChatLine answer = session.Messages[1];
        Assert.Equal("partial code", answer.Text);
        Assert.False(answer.IsError);
        Assert.False(answer.Pending);
        Assert.Contains("Partial reply retained", answer.Status);
        ConversationDocument saved = await session.Conversations.LoadDocumentAsync(session.ActiveSession!);
        Assert.Equal("partial code", saved.Messages[^1].Content);
        Assert.Equal("partial code", await File.ReadAllTextAsync(Path.Combine(session.Paths.UserDataRoot, "last-answer.txt")));
        Assert.Contains("Partial reply retained", saved.Messages[^1].Status);
        Assert.Equal(PromptRunStatus.Failed, Assert.Single(saved.Runs).Status);
        Assert.Null(saved.Runs[0].Prompt); // A partial reply retains its linked user message.
        Assert.Null(saved.Runs[0].Metrics);
        Assert.Equal(string.Empty, session.Draft);
    });

    private static async Task<DashboardSession> CreateAttachedSession(TestDirectory directory, StubServer server)
    {
        DashboardSession session = new(new PlatformPaths(directory.Path, directory.Path));
        AppSettings settings = (AppSettings)typeof(DashboardSession).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        settings.Profiles[settings.ActiveProfile] = new InferenceProfile();
        settings.Server.Port = server.Port;
        Assert.True(await session.Supervisor.AttachIfHealthyAsync(settings)); // Isolated HTTP stub, no runtime process.
        return session;
    }

    private sealed class StubServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;
        public int Port { get; }
        public int ChatRequests { get; private set; }
        public string LastChatBody { get; private set; } = string.Empty;
        public TaskCompletionSource RequestReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContinueReply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public StubServer(int tokens, bool partialReply, bool completeReply = false, string finishReason = "stop", bool holdResponse = false, bool streamFirstChunk = false)
        {
            using TcpListener probe = new(IPAddress.Loopback, 0);
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                try
                {
                    while (_listener.IsListening)
                    {
                        HttpListenerContext context = await _listener.GetContextAsync();
                        string path = context.Request.Url!.AbsolutePath;
                        // Drain incoming bodies so repeated keep-alive requests remain usable.
                        using StreamReader requestReader = new(context.Request.InputStream);
                        string requestBody = await requestReader.ReadToEndAsync();
                        string body = path switch
                        {
                            "/props" => """{"default_generation_settings":{"n_ctx":8192}}""",
                            "/apply-template" => """{"prompt":"rendered chat"}""",
                            "/tokenize" => JsonSerializer.Serialize(new { tokens = Enumerable.Range(0, tokens) }),
                            "/metrics" => "llamacpp:tokens_predicted_total 0\nllamacpp:tokens_predicted_seconds_total 0\n",
                            _ => "{}"
                        };
                        if (path == "/v1/chat/completions")
                        {
                            ChatRequests++;
                            LastChatBody = requestBody;
                            RequestReceived.TrySetResult();
                            if (holdResponse) await ContinueReply.Task;
                            context.Response.StatusCode = partialReply || completeReply ? 200 : 503;
                            context.Response.ContentType = partialReply || completeReply ? "text/event-stream" : "application/json";
                            if (streamFirstChunk)
                            {
                                context.Response.SendChunked = true;
                                byte[] first = Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"first chunk \"}}]}\n\n");
                                await context.Response.OutputStream.WriteAsync(first);
                                await context.Response.OutputStream.FlushAsync();
                                await ContinueReply.Task;
                            }
                            body = partialReply
                                ? "data: {\"choices\":[{\"delta\":{\"content\":\"partial code\"}}]}\n\ndata: invalid JSON\n\n"
                                : """{"error":{"message":"Temporarily unavailable"}}""";
                            if (completeReply) body = "data: " + JsonSerializer.Serialize(new
                            {
                                choices = new[] { new { delta = new { content = "finished code" }, finish_reason = finishReason } },
                                usage = new { prompt_tokens = 100, completion_tokens = 10, total_tokens = 110 },
                                timings = new { prompt_n = 80, cache_n = 20, predicted_n = 10, prompt_ms = 100, predicted_ms = 1000, predicted_per_second = 10 }
                            }) + "\n\ndata: [DONE]\n\n";
                        }
                        byte[] bytes = Encoding.UTF8.GetBytes(body);
                        if (!context.Response.SendChunked) context.Response.ContentLength64 = bytes.Length;
                        await context.Response.OutputStream.WriteAsync(bytes);
                        context.Response.Close();
                    }
                }
                catch (HttpListenerException) when (!_listener.IsListening) { }
                catch (ObjectDisposedException) when (!_listener.IsListening) { }
            });
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            await _loop;
            _listener.Close();
        }
    }
}
