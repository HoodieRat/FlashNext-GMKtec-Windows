using System.Text.Json;
using FlashNext.Core.Models;
using Spectre.Console;

namespace FlashNext.Manager.ConsoleUi;

public sealed class SettingsConsole(ManagerServices services)
{
    public async Task RunAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        bool done = false;
        while (!done)
        {
            ConsoleFormatting.Header();
            string choice = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("Settings and profiles").AddChoices(
                "Active profile", "Edit active profile", "MTP Draft Confidence", "Batch / UBatch", "Model directory", "Server port", "System prompt", "LAN mode", "Show validated JSON", "Back"));
            switch (choice)
            {
                case "Active profile":
                    settings.ActiveProfile = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("Select profile").AddChoices(settings.Profiles.Keys));
                    await SaveAsync(settings, cancellationToken).ConfigureAwait(false); break;
                case "Edit active profile": await EditProfileAsync(settings, cancellationToken).ConfigureAwait(false); break;
                case "MTP Draft Confidence":
                    AnsiConsole.MarkupLine("Stops extending an MTP draft when draft-token confidence falls below this threshold. Higher values can reduce wasted speculative work at deeper MTP settings.");
                    AnsiConsole.MarkupLine("Applies to Fixed and Adaptive MTP. Separate from sampler Min-P.");
                    settings.Server.SpecDraftPMin = AnsiConsole.Prompt(new SelectionPrompt<double>().Title("MTP Draft Confidence — Spec Draft P-Min (restart required)")
                        .AddChoices(0.00, 0.50, 0.65, 0.75, 0.80)
                        .UseConverter(value => value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + (value == 0 ? " — Off / current behavior (default)" : "") + (value == settings.Server.SpecDraftPMin ? " (current)" : "")));
                    await SaveAsync(settings, cancellationToken).ConfigureAwait(false);
                    AnsiConsole.MarkupLine("[yellow]Restart the server to apply MTP Draft Confidence.[/]");
                    Wait(); break;
                case "Batch / UBatch":
                    settings.Server.BatchSize = AnsiConsole.Prompt(new SelectionPrompt<int>().Title("Batch (restart required)").AddChoices(1024, 2048, 4096));
                    settings.Server.UBatchSize = AnsiConsole.Prompt(new SelectionPrompt<int>().Title("UBatch (restart required; at most Batch)").AddChoices(new[] { 256, 512, 1024, 2048 }.Where(size => size <= settings.Server.BatchSize)));
                    await SaveAsync(settings, cancellationToken).ConfigureAwait(false);
                    AnsiConsole.MarkupLine("[yellow]Restart the server to apply Batch / UBatch.[/]");
                    Wait(); break;
                case "Model directory": await EditModelDirectoryAsync(settings, cancellationToken).ConfigureAwait(false); break;
                case "Server port":
                    settings.Server.Port = AnsiConsole.Prompt(new TextPrompt<int>("Port:").DefaultValue(settings.Server.Port).Validate(static port => port is >= 1024 and <= 65535 ? ValidationResult.Success() : ValidationResult.Error("Use port 1024 through 65535.")));
                    await SaveAsync(settings, cancellationToken).ConfigureAwait(false); break;
                case "System prompt":
                    settings.Chat.SystemPrompt = AnsiConsole.Prompt(new TextPrompt<string>("System prompt:").DefaultValue(settings.Chat.SystemPrompt));
                    await SaveAsync(settings, cancellationToken).ConfigureAwait(false); break;
                case "LAN mode": await ConfigureLanAsync(settings, cancellationToken).ConfigureAwait(false); break;
                case "Show validated JSON":
                    AnsiConsole.Write(new Panel(new Text(JsonSerializer.Serialize(settings, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }))).Header("settings.json").Expand());
                    Wait(); break;
                case "Back": done = true; break;
            }
        }
    }

    private async Task EditProfileAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        InferenceProfile profile = settings.GetActiveProfile();
        profile.Thinking = AnsiConsole.Confirm("Thinking enabled?", profile.Thinking);
        profile.ReasoningEffort = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("Reasoning effort").AddChoices("none", "low", "medium", "high").UseConverter(value => value == profile.ReasoningEffort ? value + " (current)" : value)).Replace(" (current)", string.Empty, StringComparison.Ordinal);
        profile.Temperature = AskDouble("Temperature", profile.Temperature, 0, 2);
        profile.TopP = AskDouble("Top-p", profile.TopP, 0, 1);
        profile.TopK = AskInt("Top-k", profile.TopK, 0, 1000);
        profile.MinP = AskDouble("Min-p", profile.MinP, 0, 1);
        profile.PresencePenalty = AskDouble("Presence penalty", profile.PresencePenalty, -2, 2);
        profile.RepetitionPenalty = AskDouble("Repetition penalty", profile.RepetitionPenalty, 0.1, 2);
        profile.ContextSize = AskInt("Context size", profile.ContextSize, 4096, 131072);
        profile.MaxOutputTokens = AskInt("Maximum output tokens", Math.Min(profile.MaxOutputTokens, profile.ContextSize), 1, profile.ContextSize);
        string mtp = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("MTP (restart required)").AddChoices("Off", "Fixed", "Adaptive"));
        // Preserve other runtime extras while replacing the managed adaptive controls.
        List<string> extras = [];
        for (int i = 0; i < settings.Server.ExtraArguments.Count; i++)
        {
            string argument = settings.Server.ExtraArguments[i];
            if (argument.Equals("--spec-draft-adaptive", StringComparison.OrdinalIgnoreCase)) continue;
            if (argument.Equals("--spec-draft-n-min", StringComparison.OrdinalIgnoreCase)) { i++; continue; }
            extras.Add(argument);
        }
        settings.Server.SpecType = mtp == "Off" ? "none" : "draft-mtp";
        if (mtp != "Off") profile.MtpNMax = AskInt(mtp == "Adaptive" ? "Adaptive maximum (minimum 2)" : "Fixed MTP depth", Math.Max(mtp == "Adaptive" ? 2 : 1, profile.MtpNMax), mtp == "Adaptive" ? 2 : 1, 6);
        if (mtp == "Adaptive") extras.AddRange(["--spec-draft-adaptive", "--spec-draft-n-min", "2"]);
        settings.Server.ExtraArguments = extras;
        await SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        AnsiConsole.MarkupLine("[yellow]Restart the server to apply context or MTP changes.[/]");
        Wait();
    }

    private async Task EditModelDirectoryAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        string defaultPath = string.IsNullOrWhiteSpace(settings.Paths.ModelDirectory) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "FlashNextModels", "Qwen3.8-Flash-Next") : settings.Paths.ModelDirectory;
        string path = AnsiConsole.Prompt(new TextPrompt<string>("Model directory:").DefaultValue(defaultPath));
        path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        Directory.CreateDirectory(path);
        DriveInfo drive = new(Path.GetPathRoot(path)!);
        AnsiConsole.MarkupLine($"Available: [cyan]{drive.AvailableFreeSpace / 1073741824.0:N1} GiB[/]. Minimum is 115 GiB; 140 GiB is recommended.");
        settings.Paths.ModelDirectory = path;
        await SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        Wait();
    }

    private async Task ConfigureLanAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (settings.Lan.Enabled)
        {
            if (!AnsiConsole.Confirm("Disable LAN mode and remove the FlashNext firewall rule?", true)) return;
            await services.Supervisor.StopAsync(cancellationToken).ConfigureAwait(false);
            await services.Lan.DisableAsync(settings, cancellationToken).ConfigureAwait(false);
            settings.Lan.Enabled = false;
            settings.Lan.BindAddress = "127.0.0.1";
            settings.Lan.CorsOrigins.Clear();
            await SaveAsync(settings, cancellationToken).ConfigureAwait(false);
            AnsiConsole.MarkupLine("[green]LAN mode disabled. Factory loopback binding restored.[/]");
            Wait(); return;
        }
        AnsiConsole.MarkupLine("[red bold]Advanced LAN exposure warning[/]");
        AnsiConsole.MarkupLine("LAN clients can send prompts to the model. The API key remains mandatory. Use a private network, restrict remote addresses, and specify exact CORS origins only when required.");
        string confirmation = AnsiConsole.Ask<string>("Type [bold]ENABLE LAN[/] to continue:");
        if (!confirmation.Equals("ENABLE LAN", StringComparison.Ordinal)) return;
        settings.Lan.BindAddress = AnsiConsole.Prompt(new TextPrompt<string>("Bind address:").DefaultValue("0.0.0.0"));
        settings.Lan.AllowedRemoteAddress = AnsiConsole.Prompt(new TextPrompt<string>("Firewall remote address or CIDR:").DefaultValue("LocalSubnet"));
        string origins = AnsiConsole.Prompt(new TextPrompt<string>("Comma-separated CORS origins, or leave blank:").AllowEmpty());
        settings.Lan.CorsOrigins = origins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        foreach (string origin in settings.Lan.CorsOrigins) if (!Uri.TryCreate(origin, UriKind.Absolute, out _)) throw new InvalidDataException($"CORS origin '{origin}' is invalid.");
        settings.Lan.Enabled = true;
        IReadOnlyList<string> errors = services.Settings.Validate(settings);
        if (errors.Count > 0) { settings.Lan.Enabled = false; throw new InvalidDataException(string.Join(Environment.NewLine, errors)); }
        await services.Secrets.GetOrCreateApiKeyAsync(cancellationToken).ConfigureAwait(false);
        await services.Supervisor.StopAsync(cancellationToken).ConfigureAwait(false);
        await services.Lan.EnableAsync(settings, cancellationToken).ConfigureAwait(false);
        await SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        AnsiConsole.MarkupLine("[green]LAN mode enabled with an API-key requirement and restricted firewall rule.[/]");
        Wait();
    }

    private async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> errors = services.Settings.Validate(settings);
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        await services.Settings.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        AnsiConsole.MarkupLine("[green]Settings saved atomically.[/]");
    }

    private static int AskInt(string label, int current, int minimum, int maximum) => AnsiConsole.Prompt(new TextPrompt<int>($"{label}:").DefaultValue(current).Validate(value => value >= minimum && value <= maximum ? ValidationResult.Success() : ValidationResult.Error($"Use {minimum} through {maximum}.")));
    private static double AskDouble(string label, double current, double minimum, double maximum) => AnsiConsole.Prompt(new TextPrompt<double>($"{label}:").DefaultValue(current).Validate(value => value >= minimum && value <= maximum ? ValidationResult.Success() : ValidationResult.Error($"Use {minimum} through {maximum}.")));
    private static void Wait() { AnsiConsole.MarkupLine("[grey]Press Enter to continue.[/]"); Console.ReadLine(); }
}
