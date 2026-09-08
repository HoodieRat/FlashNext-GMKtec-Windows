using System.Text.Json;
using FlashNext.Core.Models;
using FlashNext.Core.Services;
using Spectre.Console;

namespace FlashNext.Manager.ConsoleUi;

public sealed class BenchmarkConsole(ManagerServices services)
{
    private const string Prompt = "Write exactly 250 useful tokens explaining how to validate a local OpenAI-compatible coding model server. Use plain English and no headings.";

    public async Task RunAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        AnsiConsole.MarkupLine("[yellow]Benchmarking reloads the model for MTP n-max 4 and 6, using fixed seed 12345. The model is warmed once, then measured three times per candidate.[/]");
        if (!AnsiConsole.Confirm("Continue with deterministic auto-tuning?", true)) return;
        string originalProfile = settings.ActiveProfile;
        if (!settings.Profiles.TryGetValue("benchmark-deterministic", out InferenceProfile? benchmarkProfile)) throw new InvalidDataException("benchmark-deterministic profile is missing.");
        int originalBenchmarkNMax = benchmarkProfile.MtpNMax;
        int originalBenchmarkOutput = benchmarkProfile.MaxOutputTokens;
        int? originalBenchmarkSeed = benchmarkProfile.Seed;
        settings.ActiveProfile = "benchmark-deterministic";
        HardwareReport hardware = await services.Hardware.ProbeAsync(cancellationToken).ConfigureAwait(false);
        BenchmarkReport report = new()
        {
            Hardware = hardware,
            Prompt = BenchmarkConsole.Prompt,
            PromptSha256 = HashService.Sha256Text(Prompt)
        };
        string path = Path.Combine(services.Paths.BenchmarksDirectory, $"benchmark-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
        try
        {
            foreach (int nMax in new[] { 4, 6 })
            {
                InferenceProfile profile = settings.Profiles["benchmark-deterministic"];
                profile.MtpNMax = nMax;
                profile.MaxOutputTokens = 250;
                profile.Seed = 12345;
                await services.Settings.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
                await services.Supervisor.RestartAsync(settings, cancellationToken).ConfigureAwait(false);
                RuntimeLaunchSnapshot? launch = services.Supervisor.Status.Launch;
                report.RuntimeRepository = launch?.RuntimeRepository ?? "Unknown (launch not recorded)";
                report.RuntimeCommit = launch?.RuntimeCommit ?? "Unknown (launch not recorded)";
                report.RuntimeBuildManifestSha256 = launch?.BuildManifestSha256 ?? "Unknown (launch not recorded)";
                report.RuntimeExecutable = launch?.Executable ?? "Unknown (launch not recorded)";
                report.ModelCommit = launch?.Files.FirstOrDefault(file => file.Kind == "model")?.Revision ?? "Unknown (launch not recorded)";
                report.MainModelPath = launch?.Files.FirstOrDefault(file => file.Kind == "model")?.Path ?? "Unknown (launch not recorded)";
                report.DraftModelPath = launch?.Files.FirstOrDefault(file => file.Kind == "mtp")?.Path ?? "MTP disabled or launch not recorded";
                BenchmarkCandidate candidate = new() { MtpNMax = nMax };
                report.Candidates.Add(candidate);
                for (int run = 0; run < 4; run++)
                {
                    bool warmup = run == 0;
                    BenchmarkRun item = await RunOnceAsync(settings, nMax, run, warmup, cancellationToken).ConfigureAwait(false);
                    candidate.Runs.Add(item);
                    await SaveReportAsync(path, report).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                BenchmarkRun[] measured = candidate.Runs.Where(static r => !r.Warmup).ToArray();
                candidate.MedianGenerationTokensPerSecond = Median(measured.Select(static r => r.GenerationTokensPerSecond));
                candidate.MedianTimeToFirstTokenMilliseconds = Median(measured.Select(static r => (double?)r.TimeToFirstTokenMilliseconds));
                candidate.MedianAcceptancePercent = Median(measured.Select(static r => r.AcceptancePercent));
                candidate.Stable = measured.Length == 3 && measured.All(static r => r.Error is null && r.GenerationTokensPerSecond is > 0 && r.DraftedTokens > 0 && r.AcceptedTokens > 0);
            }
            BenchmarkCandidate? winner = report.Candidates.Where(static c => c.Stable).OrderByDescending(static c => c.MedianGenerationTokensPerSecond).FirstOrDefault();
            if (winner is null)
            {
                report.SelectionReason = "No candidate completed all measured runs with nonzero throughput and MTP deltas. Existing profile values were retained.";
                AnsiConsole.MarkupLine("[red]Auto-tune did not find a stable candidate. Existing MTP settings were retained.[/]");
            }
            else
            {
                report.SelectedMtpNMax = winner.MtpNMax;
                report.SelectionReason = $"Selected n-max {winner.MtpNMax} because it had the highest stable median generation throughput.";
                foreach (string profileName in new[] { "coding-balanced", "chat-fast" }) if (settings.Profiles.TryGetValue(profileName, out InferenceProfile? target)) target.MtpNMax = winner.MtpNMax;
                AnsiConsole.MarkupLine($"[green]Selected MTP n-max {winner.MtpNMax}.[/]");
            }
        }
        finally
        {
            benchmarkProfile.MtpNMax = originalBenchmarkNMax;
            benchmarkProfile.MaxOutputTokens = originalBenchmarkOutput;
            benchmarkProfile.Seed = originalBenchmarkSeed;
            settings.ActiveProfile = originalProfile;
            await services.Settings.SaveAsync(settings, CancellationToken.None).ConfigureAwait(false);
            try { await services.Supervisor.RestartAsync(settings, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { AnsiConsole.MarkupLine($"[yellow]The original profile could not be restored automatically: {Markup.Escape(ex.Message)}[/]"); }
            await SaveReportAsync(path, report).ConfigureAwait(false);
        }
        ShowReport(report);
        AnsiConsole.MarkupLine($"Report: {Markup.Escape(path)}");
    }

    private async Task<BenchmarkRun> RunOnceAsync(AppSettings settings, int nMax, int run, bool warmup, CancellationToken cancellationToken)
    {
        InferenceProfile profile = settings.GetActiveProfile().Copy();
        const string systemPrompt = "Follow the requested length and format precisely.";
        ServerStatus status = services.Supervisor.Status;
        BenchmarkRun item = new()
        {
            MtpNMax = nMax, RunNumber = run, Warmup = warmup,
            Report = PromptRunCapture.Begin(systemPrompt, settings.ActiveProfile, profile, status)
        };
        try
        {
            string key = await services.Secrets.GetApiKeyAsync(cancellationToken).ConfigureAwait(false);
            using LlamaApiClient client = new(new Uri($"http://127.0.0.1:{settings.Server.Port}"), key, TimeSpan.FromMinutes(30));
            List<ChatMessage> messages = [new("system", systemPrompt), new("user", Prompt)];
            PreparedChat prepared = await client.PrepareChatAsync(messages, profile, cancellationToken).ConfigureAwait(false);
            item.Report = PromptRunCapture.Prepared(item.Report, prepared, profile);
            MetricSnapshot before = await client.GetMetricsAsync(cancellationToken).ConfigureAwait(false);
            ChatCompletionResult completion = await client.StreamChatAsync(prepared.Messages, profile, static _ => Task.CompletedTask, cancellationToken).ConfigureAwait(false);
            MetricSnapshot after = await client.GetMetricsAsync(cancellationToken).ConfigureAwait(false);
            SystemTelemetry telemetry = await services.Telemetry.CaptureAsync(services.Supervisor.Status.ProcessId, cancellationToken).ConfigureAwait(false);
            (long drafted, long accepted, double? acceptance) = PrometheusParser.SpeculativeDelta(before, after);
            item.TimeToFirstTokenMilliseconds = completion.TimeToFirstTokenMilliseconds;
            item.PromptTokensPerSecond = completion.PromptTokensPerSecond;
            item.GenerationTokensPerSecond = completion.GenerationTokensPerSecond ?? (completion.CompletionTokens > 0 && completion.TotalElapsedMilliseconds > completion.TimeToFirstTokenMilliseconds ? completion.CompletionTokens / ((completion.TotalElapsedMilliseconds - completion.TimeToFirstTokenMilliseconds) / 1000.0) : null);
            item.DraftedTokens = drafted;
            item.AcceptedTokens = accepted;
            item.AcceptancePercent = acceptance;
            item.WorkingSetBytes = telemetry.WorkingSetBytes;
            item.AvailableSystemMemoryBytes = telemetry.AvailableSystemMemoryBytes;
            ResponseMetrics metrics = MetricCalculator.Build(completion, before, after, telemetry, settings.ActiveProfile, prepared.ContextSize, runtime: status.Configuration, launch: status.Launch);
            item.Report = PromptRunCapture.Completed(item.Report, completion, metrics);
            AnsiConsole.MarkupLine(item.Error is null
                ? $"n-max {nMax}, {(warmup ? "warm-up" : $"run {run}")}: [green]{item.GenerationTokensPerSecond?.ToString("N2") ?? "N/A"} t/s[/], acceptance {item.AcceptancePercent?.ToString("N1") ?? "N/A"}%"
                : $"n-max {nMax}, run {run}: [red]{Markup.Escape(item.Error)}[/]");
            if (!warmup) ConsoleFormatting.ShowMetrics(metrics);
            if (completion.CompletionTokens < 200) item.Error = $"Only {completion.CompletionTokens} tokens were generated; the deterministic run is incomplete.";
        }
        catch (Exception ex)
        {
            item.Error = ex.Message;
            item.Report = item.Report! with { Status = ex is OperationCanceledException ? PromptRunStatus.Stopped : PromptRunStatus.Failed, FinishedAtUtc = DateTimeOffset.UtcNow, Error = ex.Message };
        }
        return item;
    }

    private static Task SaveReportAsync(string path, BenchmarkReport report) => AtomicFile.WriteTextAsync(path,
        JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }) + Environment.NewLine, CancellationToken.None);

    private static void ShowReport(BenchmarkReport report)
    {
        AnsiConsole.Write(new Rule("[aqua]Auto-tune summary[/]").LeftJustified());
        AnsiConsole.MarkupLine($"Runtime: [cyan]{Markup.Escape(report.RuntimeRepository)} @ {Markup.Escape(report.RuntimeCommit)}[/]");
        Table table = new Table().RoundedBorder().AddColumn("n-max").AddColumn("Stable").AddColumn("Median TTFT").AddColumn("Median decode").AddColumn("Median acceptance");
        foreach (BenchmarkCandidate candidate in report.Candidates)
        {
            table.AddRow(candidate.MtpNMax.ToString(), candidate.Stable ? "[green]yes[/]" : "[red]no[/]", candidate.MedianTimeToFirstTokenMilliseconds is double ttft ? $"{ttft:N0} ms" : "N/A", candidate.MedianGenerationTokensPerSecond is double tps ? $"{tps:N2} t/s" : "N/A", candidate.MedianAcceptancePercent is double percent ? $"{percent:N1}%" : "N/A");
        }
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(Markup.Escape(report.SelectionReason));
    }

    private static double? Median(IEnumerable<double?> values)
    {
        double[] ordered = values.Where(static value => value.HasValue).Select(static value => value!.Value).Order().ToArray();
        if (ordered.Length == 0) return null;
        int middle = ordered.Length / 2;
        return ordered.Length % 2 == 1 ? ordered[middle] : (ordered[middle - 1] + ordered[middle]) / 2.0;
    }

}
