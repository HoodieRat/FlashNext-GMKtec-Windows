using FlashNext.Core.Models;
using FlashNext.Core.Services;

namespace FlashNext.UnitTests;

public sealed class PrometheusParserTests
{
    [Fact]
    public void ComputesLiveGenerationRateFromRuntimeCounters()
    {
        MetricSnapshot before = PrometheusParser.Parse("llamacpp:tokens_predicted_total 100\nllamacpp:tokens_predicted_seconds_total 2\n");
        MetricSnapshot after = PrometheusParser.Parse("llamacpp:tokens_predicted_total 220\nllamacpp:tokens_predicted_seconds_total 5\n");

        Assert.Equal(40, PrometheusParser.GenerationTokensPerSecond(before, after));
    }

    [Fact]
    public void LiveGenerationRateIsUnavailableBeforeTokensAdvance()
    {
        MetricSnapshot before = PrometheusParser.Parse("llamacpp:tokens_predicted_total 100\nllamacpp:tokens_predicted_seconds_total 2\n");
        MetricSnapshot after = PrometheusParser.Parse("llamacpp:tokens_predicted_total 100\nllamacpp:tokens_predicted_seconds_total 2\n");

        Assert.Null(PrometheusParser.GenerationTokensPerSecond(before, after));
    }

    [Fact]
    public void CalculatesPerRequestSpeculativeDeltas()
    {
        var before = PrometheusParser.Parse("llamacpp:tokens_drafted_total 100\nllamacpp:tokens_accepted_total 60\n");
        var after = PrometheusParser.Parse("llamacpp:tokens_drafted_total 130\nllamacpp:tokens_accepted_total 81\n");
        (long drafted, long accepted, double? percent) = PrometheusParser.SpeculativeDelta(before, after);
        Assert.Equal(30, drafted);
        Assert.Equal(21, accepted);
        Assert.Equal(70, percent);
    }

    [Fact]
    public void HandlesCounterResetWithoutNegativeValues()
    {
        var before = PrometheusParser.Parse("server_draft_tokens_total 50\nserver_accepted_tokens_total 25\n");
        var after = PrometheusParser.Parse("server_draft_tokens_total 5\nserver_accepted_tokens_total 2\n");
        var result = PrometheusParser.SpeculativeDelta(before, after);
        Assert.Equal(0, result.Drafted);
        Assert.Equal(0, result.Accepted);
        Assert.Null(result.Acceptance);
    }

    [Fact]
    public void ReadsGraphReuseAndDraftMeanFromCompletionLog()
    {
        var result = PrometheusParser.ParseCompletionLog("graphs reused =          42\ndraft acceptance = 0.90000 (  90 accepted / 100 generated), mean len =  3.70");
        Assert.Equal(42, result.GraphReuseCount);
        Assert.Equal(3.7, result.MeanAcceptedSpanLength);
    }

    [Fact]
    public void ConvertsRuntimeMeanSpanToDraftOnlyAverageAndCapsItAtConfiguredDepth()
    {
        Assert.Equal(2.63, PrometheusParser.AcceptedDraftTokensFromMeanSpan(3.63, 3));
        Assert.Equal(3, PrometheusParser.AcceptedDraftTokensFromMeanSpan(4.20, 3));
    }

    [Fact]
    public void SumsImageBatchesFromRuntimeCompletionLog()
    {
        int? tokens = PrometheusParser.ParseImageTokenCount("mtmd_helper_eval_chunks: decoding image batch 1/3, n_tokens_batch = 1024\nmtmd_helper_eval_chunks: decoding image batch 2/3, n_tokens_batch = 1024\nmtmd_helper_eval_chunks: decoding image batch 3/3, n_tokens_batch = 512");
        Assert.Equal(2560, tokens);
    }
}
