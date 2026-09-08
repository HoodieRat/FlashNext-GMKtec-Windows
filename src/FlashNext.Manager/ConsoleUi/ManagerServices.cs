using FlashNext.Core.Interfaces;
using FlashNext.Core.Services;
using FlashNext.Infrastructure.Windows.Security;
using FlashNext.Infrastructure.Windows.System;

namespace FlashNext.Manager.ConsoleUi;

public sealed record ManagerServices(
    PlatformPaths Paths,
    ISettingsStore Settings,
    WindowsSecretStore Secrets,
    IRuntimeSupervisor Supervisor,
    IHardwareProbe Hardware,
    IRuntimeManager Runtime,
    IModelManager Model,
    ISystemTelemetryProvider Telemetry,
    IDiagnosticsBundleService Diagnostics,
    ILanModeService Lan,
    StructuredFileLogger Logger,
    ConversationStore Conversations);
