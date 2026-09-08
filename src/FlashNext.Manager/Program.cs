using System.Text;
using FlashNext.Core.Services;
using FlashNext.Infrastructure.Windows.Diagnostics;
using FlashNext.Infrastructure.Windows.Hardware;
using FlashNext.Infrastructure.Windows.Models;
using FlashNext.Infrastructure.Windows.Runtime;
using FlashNext.Infrastructure.Windows.Security;
using FlashNext.Infrastructure.Windows.System;
using FlashNext.Manager.ConsoleUi;
using Spectre.Console;

Console.OutputEncoding = new UTF8Encoding(false);
Console.InputEncoding = Encoding.UTF8;
Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("FlashNext Manager requires Windows 11 x64.");
    return 3;
}

using Mutex managerMutex = new(true, "Local\\FlashNextManager.Manager", out bool firstInstance);
if (!firstInstance)
{
    Console.Error.WriteLine("FlashNext Manager is already running for this user.");
    return 4;
}

PlatformPaths paths = new();
paths.EnsureUserDirectories();
SecretRedactor redactor = new();
StructuredFileLogger logger = new(paths.LogsDirectory, redactor);
WindowsProcessRunner processRunner = new();
WindowsSecretStore secrets = new(paths, redactor);
SettingsStore settings = new(paths.SettingsPath, paths.FactorySettingsPath);
RuntimeSupervisor supervisor = new(paths, secrets, processRunner, logger);
ManagerServices services = new(
    paths,
    settings,
    secrets,
    supervisor,
    new WindowsHardwareProbe(processRunner, paths),
    new RuntimeManager(paths, processRunner, logger),
    new ModelManager(paths, logger),
    new WindowsSystemTelemetryProvider(),
    new DiagnosticsBundleService(paths, secrets),
    new LanModeService(paths),
    logger,
    new ConversationStore(paths.ConversationsDirectory));

try
{
    await secrets.GetOrCreateApiKeyAsync();
    int? commandResult = await CommandLineModes.TryRunAsync(Environment.GetCommandLineArgs().Skip(1).ToArray(), services);
    if (commandResult is int exitCode) return exitCode;
    ManagerApplication application = new(services);
    await application.RunAsync();
    return 0;
}
catch (Exception ex)
{
    await logger.ErrorAsync("manager-fatal", "The manager stopped because of an unhandled error.", ex);
    AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
    return 1;
}
finally
{
    await supervisor.DisposeAsync();
}
