using FlashNext.Core.Models;
using Spectre.Console;

namespace FlashNext.Manager.ConsoleUi;

public sealed class ManagerApplication(ManagerServices services)
{
    private AppSettings _settings = new();
    private HardwareReport? _hardware;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        _settings = await services.Settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        while (!cancellationToken.IsCancellationRequested)
        {
            ConsoleFormatting.Header();
            ShowStatus();
            string choice = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("[bold]Main menu[/]").PageSize(14).AddChoices(
                "1. Start server and chat",
                "2. Chat with running server",
                "3. Start server only",
                "4. Stop or restart server",
                "5. Benchmark and auto-tune",
                "6. Download, verify, or repair model",
                "7. Verify, rebuild, update, or roll back runtime",
                "8. Settings and profiles",
                "9. API integration details",
                "10. Diagnostics and redacted support bundle",
                "11. Factory reset",
                "0. Exit"));
            try
            {
                if (choice.StartsWith("1.", StringComparison.Ordinal)) { await EnsureServerAsync(cancellationToken); await new ChatConsole(services).RunAsync(_settings, cancellationToken); }
                else if (choice.StartsWith("2.", StringComparison.Ordinal)) { if (await EnsureAttachedAsync(cancellationToken)) await new ChatConsole(services).RunAsync(_settings, cancellationToken); }
                else if (choice.StartsWith("3.", StringComparison.Ordinal)) await EnsureServerAsync(cancellationToken);
                else if (choice.StartsWith("4.", StringComparison.Ordinal)) await StopRestartAsync(cancellationToken);
                else if (choice.StartsWith("5.", StringComparison.Ordinal)) { await EnsureFactoryPreflightAsync(cancellationToken); await new BenchmarkConsole(services).RunAsync(_settings, cancellationToken); _settings = await services.Settings.LoadAsync(cancellationToken); }
                else if (choice.StartsWith("6.", StringComparison.Ordinal)) await ModelMenuAsync(cancellationToken);
                else if (choice.StartsWith("7.", StringComparison.Ordinal)) await RuntimeMenuAsync(cancellationToken);
                else if (choice.StartsWith("8.", StringComparison.Ordinal)) { await new SettingsConsole(services).RunAsync(_settings, cancellationToken); _settings = await services.Settings.LoadAsync(cancellationToken); }
                else if (choice.StartsWith("9.", StringComparison.Ordinal)) await ShowApiAsync(cancellationToken);
                else if (choice.StartsWith("10.", StringComparison.Ordinal)) await DiagnosticsAsync(cancellationToken);
                else if (choice.StartsWith("11.", StringComparison.Ordinal)) await FactoryResetAsync(cancellationToken);
                else if (choice.StartsWith("0.", StringComparison.Ordinal)) return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                await services.Logger.ErrorAsync("menu-operation-failed", ex.Message, ex, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                AnsiConsole.MarkupLine($"[red]Operation failed:[/] {Markup.Escape(ex.Message)}");
            }
            AnsiConsole.MarkupLine("[grey]Press Enter to continue.[/]");
            Console.ReadLine();
        }
    }

    private void ShowStatus()
    {
        string state = services.Supervisor.Status.State.ToString();
        AnsiConsole.MarkupLine($"Profile: [cyan]{Markup.Escape(_settings.ActiveProfile)}[/]  Server: [cyan]{Markup.Escape(state)}[/]  API: [cyan]http://127.0.0.1:{_settings.Server.Port}/v1[/]");
        if (_settings.Lan.Enabled) AnsiConsole.MarkupLine("[red bold]LAN mode is enabled.[/] Access is API-key protected and restricted by the configured firewall rule.");
    }

    private async Task EnsureServerAsync(CancellationToken cancellationToken)
    {
        await EnsureFactoryPreflightAsync(cancellationToken).ConfigureAwait(false);
        await AnsiConsole.Status().StartAsync("Starting model server...", async _ => await services.Supervisor.StartAsync(_settings, cancellationToken).ConfigureAwait(false));
        AnsiConsole.MarkupLine("[green]Server is healthy.[/]");
    }

    private async Task<bool> EnsureAttachedAsync(CancellationToken cancellationToken)
    {
        if (services.Supervisor.Status.State is ServerLifecycleState.Running or ServerLifecycleState.External) return true;
        if (await services.Supervisor.AttachIfHealthyAsync(_settings, cancellationToken).ConfigureAwait(false)) return true;
        AnsiConsole.MarkupLine("[yellow]No healthy server is running.[/]");
        return false;
    }

    private async Task EnsureFactoryPreflightAsync(CancellationToken cancellationToken)
    {
        _hardware ??= await AnsiConsole.Status().StartAsync("Checking EVO-X2 and Vulkan memory...", async _ => await services.Hardware.ProbeAsync(cancellationToken).ConfigureAwait(false));
        ConsoleFormatting.ShowHardware(_hardware);
        if (!_hardware.PassesFactoryPreflight) throw new InvalidOperationException("Factory hardware preflight did not pass. Correct the listed items before loading the model.");
        if (string.IsNullOrWhiteSpace(_settings.Paths.ModelDirectory)) throw new InvalidOperationException("Select a model directory in Settings and profiles, then download the model.");
        ModelVerificationResult verified = await services.Model.VerifyAsync(_settings, cancellationToken).ConfigureAwait(false);
        if (!verified.Success) throw new InvalidOperationException("The model is not verified. Use Download, verify, or repair model.");
    }

    private async Task StopRestartAsync(CancellationToken cancellationToken)
    {
        string action = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("Server operation").AddChoices("Stop", "Restart", "Back"));
        if (action == "Stop") await services.Supervisor.StopAsync(cancellationToken).ConfigureAwait(false);
        else if (action == "Restart") { await EnsureFactoryPreflightAsync(cancellationToken); await services.Supervisor.RestartAsync(_settings, cancellationToken).ConfigureAwait(false); }
    }

    private async Task ModelMenuAsync(CancellationToken cancellationToken)
    {
        string action = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("Model operation").AddChoices("Verify", "Download or repair", "Change model directory", "Back"));
        if (action == "Back") return;
        if (action == "Change model directory")
        {
            string path = AnsiConsole.Prompt(new TextPrompt<string>("Model directory:").DefaultValue(string.IsNullOrWhiteSpace(_settings.Paths.ModelDirectory) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "FlashNextModels", "Qwen3.8-Flash-Next") : _settings.Paths.ModelDirectory));
            string full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
            Directory.CreateDirectory(full);
            DriveInfo drive = new(Path.GetPathRoot(full)!);
            AnsiConsole.MarkupLine($"Available: [cyan]{drive.AvailableFreeSpace / 1073741824.0:N1} GiB[/]; minimum: [yellow]115 GiB[/]; recommended: [green]140 GiB[/].");
            _settings.Paths.ModelDirectory = full;
            await services.Settings.SaveAsync(_settings, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (string.IsNullOrWhiteSpace(_settings.Paths.ModelDirectory)) throw new InvalidOperationException("Choose a model directory first.");
        if (action == "Verify")
        {
            ModelVerificationResult result = await services.Model.VerifyAsync(_settings, cancellationToken).ConfigureAwait(false);
            ShowMessages(result.Messages, result.Success);
            return;
        }
        AnsiConsole.MarkupLine("[yellow]Disclosure:[/] This installs Unsloth UD-Q4_K_XL production weights, the shared Q8_0 MTP draft model, and the F16 vision projector from one immutable revision. Quantization can still differ from the unquantized model.");
        string acceptance = AnsiConsole.Ask<string>("Type [bold]ACCEPT[/] to accept Qwen Community License 1.0 and continue:");
        if (!acceptance.Equals("ACCEPT", StringComparison.Ordinal)) { AnsiConsole.MarkupLine("Download cancelled."); return; }
        ModelVerificationResult download = await services.Model.DownloadOrRepairAsync(_settings, true, cancellationToken).ConfigureAwait(false);
        ShowMessages(download.Messages, download.Success);
    }

    private async Task RuntimeMenuAsync(CancellationToken cancellationToken)
    {
        string action = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("Runtime operation").AddChoices("Verify current", "Build pinned runtime and activate after smoke tests", "Roll back", "Back"));
        if (action == "Back") return;
        RuntimeVerificationResult result;
        if (action == "Verify current") result = await services.Runtime.VerifyCurrentAsync(_settings, cancellationToken).ConfigureAwait(false);
        else if (action == "Roll back") { await services.Supervisor.StopAsync(cancellationToken); result = await services.Runtime.RollbackAsync(_settings, cancellationToken).ConfigureAwait(false); }
        else { await EnsureFactoryPreflightAsync(cancellationToken); await services.Supervisor.StopAsync(cancellationToken); result = await services.Runtime.BuildAndActivateAsync(_settings, cancellationToken).ConfigureAwait(false); }
        ShowMessages(result.Messages, result.Success);
        if (result.BinaryHashes.Count > 0)
        {
            Table table = new Table().RoundedBorder().AddColumn("Binary").AddColumn("SHA-256");
            foreach ((string name, string hash) in result.BinaryHashes) table.AddRow(Markup.Escape(name), Markup.Escape(hash));
            AnsiConsole.Write(table);
        }
    }

    private async Task ShowApiAsync(CancellationToken cancellationToken)
    {
        _ = await services.Secrets.GetApiKeyAsync(cancellationToken).ConfigureAwait(false);
        Table table = new Table().RoundedBorder().AddColumn("Item").AddColumn("Value");
        table.AddRow("Base URL", $"http://127.0.0.1:{_settings.Server.Port}/v1");
        table.AddRow("Health", $"http://127.0.0.1:{_settings.Server.Port}/health");
        table.AddRow("Metrics", $"http://127.0.0.1:{_settings.Server.Port}/metrics");
        table.AddRow("API key file", Markup.Escape(services.Secrets.ApiKeyFilePath));
        table.AddRow("Examples", Markup.Escape(Path.Combine(services.Paths.ApplicationDirectory, "integrations")));
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("The full key is never printed. Integration examples read it from the protected file or an environment variable.");
    }

    private async Task DiagnosticsAsync(CancellationToken cancellationToken)
    {
        _hardware = await services.Hardware.ProbeAsync(cancellationToken).ConfigureAwait(false);
        ConsoleFormatting.ShowHardware(_hardware);
        if (AnsiConsole.Confirm("Create a redacted support bundle?", true))
        {
            string path = await services.Diagnostics.CreateBundleAsync(_settings, _hardware, cancellationToken).ConfigureAwait(false);
            AnsiConsole.MarkupLine($"[green]Bundle created:[/] {Markup.Escape(path)}");
        }
    }

    private async Task FactoryResetAsync(CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("Factory reset preserves verified model files and runtime slots. It restores settings and rotates local API security material.");
        string confirmation = AnsiConsole.Ask<string>("Type [bold]RESET FLASHNEXT[/] to continue:");
        if (!confirmation.Equals("RESET FLASHNEXT", StringComparison.Ordinal)) return;
        await services.Supervisor.StopAsync(cancellationToken).ConfigureAwait(false);
        if (_settings.Lan.Enabled) await services.Lan.DisableAsync(_settings, cancellationToken).ConfigureAwait(false);
        _settings = await services.Settings.ResetToFactoryAsync(cancellationToken).ConfigureAwait(false);
        await services.Secrets.RotateApiKeyAsync(cancellationToken).ConfigureAwait(false);
        AnsiConsole.MarkupLine("[green]Factory settings and local API security were restored. Model and runtime files were preserved.[/]");
    }

    private static void ShowMessages(IEnumerable<string> messages, bool success)
    {
        AnsiConsole.MarkupLine(success ? "[green]Operation completed successfully.[/]" : "[red]Operation did not complete successfully.[/]");
        foreach (string message in messages) AnsiConsole.MarkupLine("• " + Markup.Escape(message));
    }
}
