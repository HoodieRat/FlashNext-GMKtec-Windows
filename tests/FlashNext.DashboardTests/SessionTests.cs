using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FlashNext.Core.Services;
using FlashNext.Core.Models;
using FlashNext.Dashboard;
using FlashNext.Infrastructure.Windows.System;
using FlashNext.UnitTests;
using Xunit;

namespace FlashNext.DashboardTests;

public sealed class SessionTests
{
    [Fact]
    public Task ReopeningRecoversUnfinishedReportsAndKeepsLegacyRepliesReadable() => OnDispatcher(async () =>
    {
        using TestDirectory directory = new();
        await using DashboardSession session = CreateSession(directory);
        PromptRunReport run = new() { Prompt = "unfinished prompt" };
        await session.Conversations.SaveDocumentAsync(new()
        {
            Name = "unfinished", Runs = [run],
            Messages = [new("user", "older prompt"), new("assistant", "legacy answer"), new("user", run.Prompt!) { RunId = run.Id }, new("assistant", "partial") { RunId = run.Id }]
        });
        DateTime written = File.GetLastWriteTimeUtc(Path.Combine(session.Paths.ConversationsDirectory, "unfinished.json"));
        await session.RefreshReportsAsync();
        Assert.Equal(PromptRunStatus.Interrupted, Assert.Single(Assert.Single(session.ReportSessions).Runs).Report.Status);
        Assert.Null(session.ActiveSession);
        Assert.Equal(written, File.GetLastWriteTimeUtc(Path.Combine(session.Paths.ConversationsDirectory, "unfinished.json")));
        await session.ChangeSessionAsync("unfinished");
        Assert.Equal("Report not recorded", session.Messages[1].ReportHeadline);
        Assert.Equal(PromptRunStatus.Interrupted, session.Messages[^1].RunReport!.Status);
        Assert.Contains("TG N/A", session.Messages[^1].ReportHeadline);
        Assert.Contains("Interrupted", session.MetricsText);
        Assert.Equal(PromptRunStatus.Interrupted, (await session.Conversations.LoadDocumentAsync("unfinished")).Runs[0].Status);
    });

    [Fact]
    public Task ReportSearchUsesLinkedHistoryWithLegacyFallbackWithoutOpeningTheSession() => OnDispatcher(async () =>
    {
        using TestDirectory directory = new();
        await using DashboardSession session = CreateSession(directory);
        PromptRunReport linked = new();
        PromptRunReport legacy = new() { Prompt = "legacy fallback prompt" };
        PromptRunReport staleCopy = new() { Prompt = "outdated duplicate" };
        await session.Conversations.SaveDocumentAsync(new()
        {
            Name = "saved runs", Runs = [linked, legacy, staleCopy],
            Messages = [new("user", "linked history prompt") { RunId = linked.Id },
                new("user", "preferred history prompt") { RunId = staleCopy.Id }]
        });
        await session.RefreshReportsAsync();
        foreach ((string prompt, string id) in new[]
        {
            ("linked history prompt", linked.Id), ("legacy fallback prompt", legacy.Id),
            ("preferred history prompt", staleCopy.Id)
        })
        {
            session.ReportSearch = prompt;
            ReportRunRow row = Assert.Single(session.ReportRuns!.Cast<ReportRunRow>());
            Assert.Equal(id, row.Report.Id);
            Assert.Equal(prompt, row.Prompt);
            Assert.DoesNotContain(prompt, row.Details);
        }
        session.ReportSearch = "outdated duplicate";
        Assert.Empty(session.ReportSessions);
        Assert.Null(session.ActiveSession);
    });

    [Fact]
    public Task SwitchingSessionsPreservesMessagesDraftsAndThinking() => OnDispatcher(async () =>
    {
        using TestDirectory directory = new();
        await using DashboardSession session = CreateSession(directory);
        session.SystemPrompt = "game rules";
        session.Messages.Add(new("You", "game", false));
        session.Messages.Add(new("Assistant", "partial code", false) { Thinking = "thoughts", Status = "Stopped" });
        session.Draft = "continue";
        await session.ChangeSessionAsync(null);
        string first = Assert.Single(session.SessionNames);
        Assert.Empty(session.Messages);
        Assert.Null(session.ActiveSession);
        Assert.Equal(string.Empty, session.Draft);

        session.SystemPrompt = "other rules";
        session.Messages.Add(new("You", "another task", false));
        await session.ChangeSessionAsync(first);
        Assert.Equal(first, session.ActiveSession);
        Assert.Equal(2, session.SessionNames.Length);
        Assert.Equal("game rules", session.SystemPrompt);
        Assert.Equal("continue", session.Draft);
        Assert.Equal("partial code", session.Messages[1].Text);
        Assert.Equal("thoughts", session.Messages[1].Thinking);
        Assert.Equal("Stopped", session.Messages[1].Status);
        Assert.Equal(first, session.Conversations.List(mostRecentFirst: true)[0]);

        await using DashboardSession reopened = CreateSession(directory);
        await reopened.ChangeSessionAsync(reopened.Conversations.List(mostRecentFirst: true)[0]);
        Assert.Equal("continue", reopened.Draft);
        Assert.Equal("partial code", reopened.Messages[1].Text);
    });

    [Fact]
    public Task FailedOpenDoesNotClearCurrentTranscriptOrDraft() => OnDispatcher(async () =>
    {
        using TestDirectory directory = new();
        await using DashboardSession session = CreateSession(directory);
        await AtomicFile.WriteTextAsync(Path.Combine(session.Paths.ConversationsDirectory, "broken.json"), "not json");
        session.Messages.Add(new("You", "keep this", false));
        session.Draft = "keep draft";
        await session.ChangeSessionAsync("broken");
        Assert.Equal("keep this", Assert.Single(session.Messages).Text);
        Assert.Equal("keep draft", session.Draft);
        Assert.Contains("could not be saved or opened", session.LastError);
        Assert.NotEqual("broken", session.ActiveSession);
        Assert.True(session.CanChangeSession);
    });

    [Fact]
    public Task FailedSavePreventsDestructiveSessionSwitch() => OnDispatcher(async () =>
    {
        using TestDirectory directory = new();
        await using DashboardSession session = CreateSession(directory);
        Directory.Delete(session.Paths.ConversationsDirectory); // Empty, isolated test directory only.
        await File.WriteAllTextAsync(session.Paths.ConversationsDirectory, "blocks directory creation");
        session.Messages.Add(new("You", "keep this", false));
        session.Draft = "keep draft";
        await session.ChangeSessionAsync(null);
        Assert.Equal("keep this", Assert.Single(session.Messages).Text);
        Assert.Equal("keep draft", session.Draft);
        Assert.Contains("could not be saved or opened", session.LastError);
        Assert.True(session.CanChangeSession);
    });

    [Fact]
    public Task SessionSwitchIsBlockedWhileGenerating() => OnDispatcher(async () =>
    {
        using TestDirectory directory = new();
        await using DashboardSession session = CreateSession(directory);
        session.Messages.Add(new("You", "in progress", false));
        FieldInfo busy = typeof(DashboardSession).GetField("_busy", BindingFlags.Instance | BindingFlags.NonPublic)!;
        busy.SetValue(session, true);
        Assert.False(session.CanChangeSession);
        Assert.True(session.IsChatBusy);
        await session.ChangeSessionAsync(null);
        Assert.Equal("in progress", Assert.Single(session.Messages).Text);
        busy.SetValue(session, false);
    });

    [Fact]
    public Task SessionDropdownAndNewButtonWorkThroughRealWindowBindings() => OnDispatcher(async () =>
    {
        using TestDirectory directory = new();
        await using DashboardSession session = CreateSession(directory);
        session.Messages.Add(new("You", "Build a game", false));
        session.Messages.Add(new("Assistant", "Partial game code remains available when you switch sessions.", false) { Status = "Reply reached its token limit. You can ask to continue in this session." });
        session.Draft = "Continue and finish the game";
        await session.ChangeSessionAsync(null);
        string game = Assert.Single(session.SessionNames);
        session.Messages.Add(new("You", "Second conversation", false));
        await session.ChangeSessionAsync(game);

        App app = new();
        app.InitializeComponent(); // Resources only: do not run startup or touch the user's app/server.
        MainWindow window = new(session);
        FrameworkElement content = (FrameworkElement)window.Content;
        content.Measure(new Size(980, 650));
        content.Arrange(new Rect(0, 0, 980, 650));
        content.UpdateLayout();
        ComboBox selector = Descendants<ComboBox>(content).Single(combo => ReferenceEquals(combo.ItemsSource, session.SessionNames));
        Assert.Equal(game, selector.SelectedItem);
        Assert.Equal(2, selector.Items.Count);
        TabControl tabs = Descendants<TabControl>(content).Single();
        tabs.SelectedIndex = 2; // Settings content is realized only while selected.
        content.UpdateLayout();
        ComboBox mtp = Descendants<ComboBox>(content).Single(combo => ReferenceEquals(combo.ItemsSource, session.MtpModes));
        Assert.Equal("fixed-4", mtp.SelectedValue);
        mtp.SelectedValue = "fixed-6";
        Assert.Equal("fixed-6", session.MtpMode);
        mtp.SelectedValue = "adaptive-2-6";
        Assert.Equal("adaptive-2-6", session.MtpMode);
        ComboBox confidence = Descendants<ComboBox>(content).Single(combo => ReferenceEquals(combo.ItemsSource, session.DraftConfidences));
        Assert.Equal(new[] { 0.00, 0.50, 0.65, 0.75, 0.80 }, session.DraftConfidences.Select(option => option.Value));
        Assert.Equal(0.0, confidence.SelectedValue);
        confidence.SelectedValue = 0.75;
        Assert.Equal(0.75, session.SpecDraftPMin);
        Assert.Equal(0.0, session.MinP);
        confidence.SelectedValue = 0.80;
        Assert.Equal(0.80, session.SpecDraftPMin);
        ComboBox ubatch = Descendants<ComboBox>(content).Single(combo => ReferenceEquals(combo.ItemsSource, session.UBatchSizes));
        ubatch.SelectedItem = 2048;
        Assert.Equal(2048, session.UBatchSize);

        // Exercise the actual TextBox binding one character at a time, including
        // incomplete numeric text that must survive until the edit is committed.
        foreach ((string property, string input, double expected) in new[]
        {
            (nameof(DashboardSession.Temperature), "0.7", 0.7),
            (nameof(DashboardSession.TopP), "0.8", 0.8),
            (nameof(DashboardSession.MinP), "0.05", 0.05),
            (nameof(DashboardSession.PresencePenalty), "-1.5", -1.5),
            (nameof(DashboardSession.PresencePenalty), "1.5", 1.5),
            (nameof(DashboardSession.RepetitionPenalty), "1.1", 1.1)
        })
        {
            TextBox editor = Descendants<TextBox>(content).Single(box =>
                BindingOperations.GetBinding(box, TextBox.TextProperty)?.Path.Path == property);
            editor.SelectAll();
            string entered = string.Empty;
            foreach (char character in input)
            {
                editor.SelectedText = character.ToString();
                entered += character;
                await Dispatcher.Yield(DispatcherPriority.Background);
                Assert.Equal(entered, editor.Text);
                editor.CaretIndex = editor.Text.Length;
            }
            editor.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            await Dispatcher.Yield(DispatcherPriority.Background);
            Assert.False(Validation.GetHasError(editor));
            Assert.Equal(expected, (double)typeof(DashboardSession).GetProperty(property)!.GetValue(session)!);
        }
        session.MtpMode = "fixed-4";
        confidence.SelectedValue = 0.0;
        session.UBatchSize = 512;
        await session.Settings.SaveAsync(new AppSettings { Profiles = new() { ["coding-balanced"] = new() }, Server = new() { SpecDraftPMin = 0.0, UBatchSize = 512 } });
        await session.SaveSettingsAsync();
        AppSettings saved = await session.Settings.LoadAsync();
        Assert.Equal(1.5, saved.GetActiveProfile().PresencePenalty);
        Assert.False(session.RestartRequired);
        tabs.SelectedIndex = 0;
        content.UpdateLayout();
        TextBox draft = (TextBox)window.FindName("DraftBox");
        Assert.Equal("Continue and finish the game", draft.Text);

        string other = session.SessionNames.Single(name => name != game);
        selector.SelectedItem = other;
        while (!session.CanChangeSession) await Task.Delay(10);
        Assert.Equal(other, session.ActiveSession);
        Assert.Equal("Second conversation", Assert.Single(session.Messages).Text);
        selector.SelectedItem = game;
        while (!session.CanChangeSession) await Task.Delay(10);
        Assert.Equal(game, session.ActiveSession);
        content.UpdateLayout();

        GridSplitter upperGrip = (GridSplitter)window.FindName("ChatMetricsSplitter");
        GridSplitter lowerGrip = (GridSplitter)window.FindName("MetricsInputSplitter");
        Grid panels = (Grid)upperGrip.Parent;
        panels.RowDefinitions[0].Height = new GridLength(2, GridUnitType.Star);
        panels.RowDefinitions[2].Height = new GridLength(1, GridUnitType.Star);
        content.UpdateLayout();
        foreach (GridSplitter grip in new[] { upperGrip, lowerGrip })
        {
            Assert.True(grip.ActualHeight >= 20);
            Assert.True(grip.ActualWidth > 500);
            Assert.Contains(Descendants<TextBlock>(grip), label => label.Text.Contains("Drag to resize"));
            double before = panels.RowDefinitions[2].ActualHeight;
            double distance = ReferenceEquals(grip, upperGrip) ? -10 : 10;
            grip.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            grip.RaiseEvent(new DragDeltaEventArgs(0, distance) { RoutedEvent = Thumb.DragDeltaEvent });
            grip.RaiseEvent(new DragCompletedEventArgs(0, distance, false) { RoutedEvent = Thumb.DragCompletedEvent });
            content.UpdateLayout();
            Assert.True(panels.RowDefinitions[2].ActualHeight > before, "Dragging either grip must be able to enlarge the benchmark panel.");
        }

        RenderTargetBitmap bitmap = new(980, 650, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (FileStream output = File.Create(Path.Combine(AppContext.BaseDirectory, "session-layout.png"))) encoder.Save(output);

        ConversationDocument reportSession = await session.Conversations.LoadDocumentAsync(other);
        PromptRunReport report = new()
        {
            Prompt = "skate game", SystemPrompt = "original system rules", Status = PromptRunStatus.Completed,
            HasOutput = true, Sampling = RunSamplingSettings.Capture(new() { Temperature = 0.2, PresencePenalty = 1.5 }),
            Metrics = new() { GenerationTokensPerSecond = 35, EffectiveTokensPerSecond = 30 },
            Runtime = new(64000, 6, "draft-mtp", false, 0, 2048, 512, "balanced", 8080, "127.0.0.1", 99, 99, "")
        };
        reportSession.Runs = [report];
        reportSession.Messages.Add(new("assistant", "saved skate code") { RunId = report.Id });
        await session.Conversations.SaveDocumentAsync(reportSession);
        string reportPath = Path.Combine(session.Paths.ConversationsDirectory, other + ".json");
        File.SetLastWriteTimeUtc(reportPath, DateTime.UtcNow.AddDays(-1));
        DateTime reportWriteTime = File.GetLastWriteTimeUtc(reportPath);
        tabs.SelectedIndex = 3;
        await session.RefreshReportsAsync();
        content.UpdateLayout();
        DataGrid sessionGrid = (DataGrid)window.FindName("ReportSessionsGrid");
        sessionGrid.SelectedItem = session.ReportSessions.Single(row => row.Name == other);
        content.UpdateLayout();
        DataGrid runGrid = (DataGrid)window.FindName("ReportRunsGrid");
        ReportRunRow reportRow = Assert.Single(runGrid.Items.Cast<ReportRunRow>());
        runGrid.SelectedItem = reportRow;
        content.UpdateLayout();
        Assert.Contains("original system rules", session.SelectedReportDetails);
        ComboBox ratingBox = (ComboBox)window.FindName("ReportRatingBox");
        ratingBox.SelectedItem = 5;
        for (int attempt = 0; attempt < 100 && (await session.Conversations.LoadDocumentAsync(other)).Runs[0].Rating != 5; attempt++) await Task.Delay(10);
        await Dispatcher.Yield(DispatcherPriority.Background);
        Assert.Equal(5, (await session.Conversations.LoadDocumentAsync(other)).Runs[0].Rating);
        Assert.Equal(reportWriteTime, File.GetLastWriteTimeUtc(reportPath));
        Assert.Equal(game, session.ActiveSession);
        Assert.Equal("Continue and finish the game", session.Draft);
        Assert.Contains("Fixed 6", session.ReportSessions.Single(row => row.Name == other).Best);
        TextBox search = (TextBox)window.FindName("ReportSearchBox");
        search.Text = "skate";
        Assert.Equal(other, Assert.Single(session.ReportSessions).Name);
        content.UpdateLayout();
        RenderTargetBitmap reportBitmap = new(980, 650, 96, 96, PixelFormats.Pbgra32);
        reportBitmap.Render(content);
        PngBitmapEncoder reportEncoder = new();
        reportEncoder.Frames.Add(BitmapFrame.Create(reportBitmap));
        using (FileStream output = File.Create(Path.Combine(AppContext.BaseDirectory, "reports-layout.png"))) reportEncoder.Save(output);
        search.Text = string.Empty;
        ChatLine? openedReport = await session.OpenReportInChatAsync(new(other, report));
        Assert.Equal("saved skate code", openedReport!.Text);
        Assert.Equal(5, openedReport.RunReport!.Rating);
        Assert.Contains("original system rules", session.MetricsText);
        await session.ChangeSessionAsync(game);
        tabs.SelectedIndex = 0;
        content.UpdateLayout();

        Button newSession = Descendants<Button>(content).Single(button => Equals(button.Content, "New session"));
        newSession.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        while (!session.CanChangeSession) await Task.Delay(10);
        Assert.Null(session.ActiveSession);
        Assert.Empty(session.Messages);
        Assert.Equal(2, session.SessionNames.Length);
        window.Close(); // Only this never-shown test window, not the user's running dashboard.
    });

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (T descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static DashboardSession CreateSession(TestDirectory directory) => new(new PlatformPaths(directory.Path, directory.Path));

    internal static Task OnDispatcher(Func<Task> test)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await test(); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
                finally { dispatcher.InvokeShutdown(); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
