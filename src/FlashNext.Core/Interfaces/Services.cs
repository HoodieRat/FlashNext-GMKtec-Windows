using FlashNext.Core.Models;

namespace FlashNext.Core.Interfaces;

public interface ISettingsStore
{
    string SettingsPath { get; }
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
    Task<AppSettings> ResetToFactoryAsync(CancellationToken cancellationToken = default);
    IReadOnlyList<string> Validate(AppSettings settings);
}

public interface ISecretStore
{
    string SecretPath { get; }
    Task<string> GetOrCreateApiKeyAsync(CancellationToken cancellationToken = default);
    Task<string> RotateApiKeyAsync(CancellationToken cancellationToken = default);
    Task<string> GetApiKeyAsync(CancellationToken cancellationToken = default);
    string Redact(string value);
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken cancellationToken = default);
}

public interface IRuntimeSupervisor : IAsyncDisposable
{
    ServerStatus Status { get; }
    Task<ServerStatus> StartAsync(AppSettings settings, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task<ServerStatus> RestartAsync(AppSettings settings, CancellationToken cancellationToken = default);
    Task<bool> AttachIfHealthyAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public interface IHardwareProbe
{
    Task<HardwareReport> ProbeAsync(CancellationToken cancellationToken = default);
}

public interface IRuntimeManager
{
    Task<RuntimeVerificationResult> VerifyCurrentAsync(AppSettings settings, CancellationToken cancellationToken = default);
    Task<RuntimeVerificationResult> BuildAndActivateAsync(AppSettings settings, CancellationToken cancellationToken = default);
    Task<RuntimeVerificationResult> RollbackAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public interface IModelManager
{
    Task<ModelVerificationResult> VerifyAsync(AppSettings settings, CancellationToken cancellationToken = default);
    Task<ModelVerificationResult> DownloadOrRepairAsync(AppSettings settings, bool acceptLicense, CancellationToken cancellationToken = default);
}

public interface ISystemTelemetryProvider
{
    Task<SystemTelemetry> CaptureAsync(int? processId, CancellationToken cancellationToken = default);
}

public interface IDiagnosticsBundleService
{
    Task<string> CreateBundleAsync(AppSettings settings, HardwareReport? hardware, CancellationToken cancellationToken = default);
}

public interface ILanModeService
{
    Task EnableAsync(AppSettings settings, CancellationToken cancellationToken = default);
    Task DisableAsync(AppSettings settings, CancellationToken cancellationToken = default);
}
