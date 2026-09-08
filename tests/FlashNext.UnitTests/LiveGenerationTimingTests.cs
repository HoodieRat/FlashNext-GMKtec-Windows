using System.Globalization;
using FlashNext.Core.Services;

namespace FlashNext.UnitTests;

public sealed class LiveGenerationTimingTests
{
    private const string Launch = "4.22.321.365 I slot launch_slot_: id  0 | task 1 | processing task, is_child = 0";
    private const string Timing = "6.22.089.067 I slot print_timing: id  0 | task 1 | n_gen =   3722, tg =  32.02 t/s, tg_3s =  36.79 t/s";

    [Fact]
    public void ParsesActualLiveLineAndKeepsWindowSeparateFromAverage()
    {
        LiveGenerationTimingParser parser = new();
        Assert.Null(parser.AcceptLine(Timing)); // Cannot bind a previous request's tail.
        parser.AcceptLine(Launch);
        LiveGenerationTiming value = Assert.IsType<LiveGenerationTiming>(parser.AcceptLine(Timing));
        Assert.Equal(36.79, value.RecentTps);
        Assert.Equal(32.02, value.AverageTps);
        Assert.Equal(3722, value.GeneratedTokens);
        Assert.Equal(1, value.TaskId);
        Assert.Null(parser.AcceptLine(Timing)); // No stale duplicate refresh.
        Assert.Null(parser.AcceptLine(Timing.Replace("3722", "3000")));
    }

    [Theory]
    [InlineData("36.79", "NaN")]
    [InlineData("36.79", "Infinity")]
    [InlineData("36.79", "0")]
    [InlineData("36.79", "-2")]
    [InlineData("32.02", "0")]
    [InlineData("3722", "9223372036854775808")]
    [InlineData("id  0", "id  1")]
    [InlineData("task 1", "task 2")]
    public void RejectsInvalidOrUnrelatedTimings(string from, string to)
    {
        LiveGenerationTimingParser parser = new();
        parser.AcceptLine(Launch);
        Assert.Null(parser.AcceptLine(Timing.Replace(from, to)));
    }

    [Fact]
    public void ReleaseAndNewTaskInvalidatePreviousTimings()
    {
        LiveGenerationTimingParser parser = new();
        parser.AcceptLine(Launch);
        parser.AcceptLine("slot release: id 0 | task 1 | stop processing");
        Assert.Null(parser.AcceptLine(Timing));
        parser.AcceptLine(Launch.Replace("task 1", "task 2"));
        Assert.Null(parser.AcceptLine(Timing));
        Assert.NotNull(parser.AcceptLine(Timing.Replace("task 1", "task 2")));
        parser.Reset();
        Assert.Null(parser.AcceptLine(Timing.Replace("task 1", "task 2")));
    }

    [Fact]
    public void DecimalParsingDoesNotDependOnWindowsCulture()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            LiveGenerationTimingParser parser = new();
            parser.AcceptLine(Launch);
            Assert.Equal(36.79, parser.AcceptLine(Timing)!.RecentTps);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }
}
