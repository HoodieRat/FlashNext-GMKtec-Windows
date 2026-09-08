using System.IO;
using System.Reflection;
using FlashNext.Dashboard;
using FlashNext.Infrastructure.Windows.Diagnostics;
using FlashNext.Infrastructure.Windows.System;
using FlashNext.UnitTests;
using Xunit;

namespace FlashNext.DashboardTests;

public sealed class LiveSpeedTests
{
    private const string Launch = "slot launch_slot_: id 0 | task 1 | processing task\n";
    private const string Timing = "slot print_timing: id 0 | task 1 | n_gen = 3722, tg = 32.02 t/s, tg_3s = 36.79 t/s\n";

    [Fact]
    public async Task TailIgnoresOldHistoryAndWaitsForCompleteLines()
    {
        using TestDirectory directory = new();
        string path = directory.File("server.log");
        await File.WriteAllTextAsync(path, Launch + Timing);
        LiveTimingLogReader reader = new(path);
        Assert.Null(await reader.ReadAsync(default));
        await File.AppendAllTextAsync(path, Timing); // No new launch, not this request.
        Assert.Null(await reader.ReadAsync(default));
        await File.AppendAllTextAsync(path, Launch + Timing[..^1]);
        Assert.Null(await reader.ReadAsync(default));
        await File.AppendAllTextAsync(path, "\n");
        Assert.Equal(36.79, (await reader.ReadAsync(default))!.RecentTps);
        Assert.Null(await reader.ReadAsync(default));
    }

    [Theory]
    [InlineData("slot release: id 0 | task 1 | stop processing\n")]
    [InlineData("slot launch_slot_: id 0 | task 2 | processing task\n")]
    public async Task DoesNotReturnEarlierTimingWhenBatchEndsWithAnotherTaskOrRelease(string ending)
    {
        using TestDirectory directory = new();
        string path = directory.File("server.log");
        await File.WriteAllTextAsync(path, string.Empty);
        LiveTimingLogReader reader = new(path);
        await File.AppendAllTextAsync(path, Launch + Timing + ending);
        Assert.Null(await reader.ReadAsync(default));
    }

    [Fact]
    public async Task TruncationAndLargeBacklogsDoNotReplayHistory()
    {
        using TestDirectory directory = new();
        string path = directory.File("server.log");
        await File.WriteAllTextAsync(path, Launch + Timing);
        LiveTimingLogReader reader = new(path);
        await File.WriteAllTextAsync(path, string.Empty);
        Assert.Null(await reader.ReadAsync(default));
        await File.AppendAllTextAsync(path, Launch + Timing);
        Assert.NotNull(await reader.ReadAsync(default));
        await File.AppendAllTextAsync(path, new string('x', 70000) + "\n" + Launch + Timing);
        Assert.Null(await reader.ReadAsync(default));
        await File.AppendAllTextAsync(path, Launch + Timing);
        Assert.NotNull(await reader.ReadAsync(default));
    }

    [Fact]
    public async Task MissingFileCanRecoverWithoutReadingItsOldContents()
    {
        using TestDirectory directory = new();
        string path = directory.File("server.log");
        LiveTimingLogReader reader = new(path);
        await Assert.ThrowsAsync<FileNotFoundException>(() => reader.ReadAsync(default));
        await File.WriteAllTextAsync(path, Launch + Timing);
        Assert.Null(await reader.ReadAsync(default));
        await File.AppendAllTextAsync(path, Launch + Timing);
        Assert.NotNull(await reader.ReadAsync(default));
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(cancelled.Token));
    }

    [Fact]
    public Task LivePanelUpdatesBeforeCompletionAndRejectsStaleReadings() => SessionTests.OnDispatcher(async () =>
    {
        using TestDirectory directory = new();
        await using DashboardSession session = new(new PlatformPaths(directory.Path, directory.Path));
        string path = directory.File("timings.log");
        await File.WriteAllTextAsync(path, string.Empty);
        LiveTimingLogReader reader = new(path);
        using CancellationTokenSource stop = new();
        TaskCompletionSource live = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource stale = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(session.LiveTpsState)) return;
            if (session.LiveTpsState == "LIVE • ~3 SECOND WINDOW") live.TrySetResult();
            if (session.LiveTpsState == "GENERATING • TIMING UNAVAILABLE") stale.TrySetResult();
        };
        MethodInfo track = typeof(DashboardSession).GetMethod("TrackLiveSpeedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using FileStream locked = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Task tracking = (Task)track.Invoke(session, [reader, (Func<bool>)(() => true), null, stop.Token])!;
        try
        {
            await Task.Delay(1200); // At least one read fails; the telemetry loop must retry.
            locked.Dispose();
            await File.AppendAllTextAsync(path, Launch + Timing);
            await live.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("36.8", session.LiveTpsText); // Recent window, not request average 32.02.
            await stale.Task.WaitAsync(TimeSpan.FromSeconds(13));
            Assert.Equal("—", session.LiveTpsText);
        }
        finally { stop.Cancel(); await tracking; }
        await File.AppendAllTextAsync(path, Launch + Timing);
        Assert.Equal("GENERATING • TIMING UNAVAILABLE", session.LiveTpsState);
    });
}
