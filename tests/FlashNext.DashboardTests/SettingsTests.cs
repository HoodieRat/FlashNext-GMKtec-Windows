using System.Reflection;
using FlashNext.Core.Models;
using FlashNext.Dashboard;
using FlashNext.Infrastructure.Windows.System;
using FlashNext.UnitTests;
using Xunit;

namespace FlashNext.DashboardTests;

public sealed class SettingsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task DraftConfidencePersistsIndependentlyAndRequiresRestart(bool running) => SessionTests.OnDispatcher(async () =>
    {
        using TestDirectory directory = new();
        await using DashboardSession session = new(new PlatformPaths(directory.Path, directory.Path));
        AppSettings settings = new()
        {
            Profiles = new() { ["coding-balanced"] = new() { MinP = 0.05 } },
            Server = new() { SpecDraftPMin = 0.00, UBatchSize = 512 }
        };
        await session.Settings.SaveAsync(settings);
        if (running)
        {
            ServerStatus status = (ServerStatus)session.Supervisor.GetType().GetField("_status", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session.Supervisor)!;
            status.Configuration = RuntimeConfiguration.Capture(settings);
        }
        session.MinP = 0.05;
        session.SpecDraftPMin = 0.75;
        await session.SaveSettingsAsync();
        Assert.True(session.RestartRequired);
        await session.SaveSettingsAsync();
        Assert.True(session.RestartRequired);
        AppSettings restored = await session.Settings.LoadAsync();
        Assert.Equal(0.75, restored.Server.SpecDraftPMin);
        Assert.Equal(0.05, restored.GetActiveProfile().MinP);
        await using DashboardSession reopened = new(new PlatformPaths(directory.Path, directory.Path));
        typeof(DashboardSession).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(reopened, restored);
        typeof(DashboardSession).GetMethod("LoadFormFromSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(reopened, null);
        Assert.Equal(0.75, reopened.SpecDraftPMin);
        Assert.Equal(0.05, reopened.MinP);
        reopened.MinP = 0.10;
        reopened.MtpMode = "adaptive-2-6";
        await reopened.SaveSettingsAsync();
        Assert.Equal(0.75, (await reopened.Settings.LoadAsync()).Server.SpecDraftPMin);
        if (running)
        {
            session.SpecDraftPMin = 0.00;
            await session.SaveSettingsAsync();
            Assert.False(session.RestartRequired);
        }
    });

    [Theory]
    [InlineData("off", 4)]
    [InlineData("fixed-1", 1)]
    [InlineData("fixed-2", 2)]
    [InlineData("fixed-3", 3)]
    [InlineData("fixed-4", 4)]
    [InlineData("fixed-5", 5)]
    [InlineData("fixed-6", 6)]
    [InlineData("adaptive-2-2", 2)]
    [InlineData("adaptive-2-3", 3)]
    [InlineData("adaptive-2-4", 4)]
    [InlineData("adaptive-2-5", 5)]
    [InlineData("adaptive-2-6", 6)]
    public Task SavesAndRestoresModesAndRestartRequirement(string mode, int depth) => SessionTests.OnDispatcher(async () =>
    {
        using TestDirectory directory = new();
        await using DashboardSession session = new(new PlatformPaths(directory.Path, directory.Path));
        AppSettings settings = new() { Profiles = new() { ["coding-balanced"] = new() } };
        await session.Settings.SaveAsync(settings);
        Assert.Equal("fixed-4", session.MtpMode);
        Assert.Contains(session.MtpModes, option => option.Value == mode);
        Assert.Equal(new[] { 1024, 2048, 4096 }, session.BatchSizes);
        Assert.Equal(new[] { 256, 512, 1024, 2048 }, session.UBatchSizes);
        session.MtpMode = mode;
        session.BatchSize = 4096;
        session.UBatchSize = 2048;
        await session.SaveSettingsAsync();
        Assert.True(session.RestartRequired);
        await session.SaveSettingsAsync();
        Assert.True(session.RestartRequired); // A second save cannot apply launch settings.
        AppSettings restored = await session.Settings.LoadAsync();
        Assert.Equal(depth, restored.GetActiveProfile().MtpNMax);
        Assert.Equal(mode == "off" ? "none" : "draft-mtp", restored.Server.SpecType);
        Assert.Equal(2048, restored.Server.UBatchSize);
        Assert.Equal(mode.StartsWith("adaptive"), restored.Server.ExtraArguments.Contains("--spec-draft-adaptive"));
        await using DashboardSession reopened = new(new PlatformPaths(directory.Path, directory.Path));
        typeof(DashboardSession).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(reopened, restored);
        typeof(DashboardSession).GetMethod("LoadFormFromSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(reopened, null);
        Assert.Equal(mode, reopened.MtpMode);
        Assert.Equal(2048, reopened.UBatchSize);
    });
}
