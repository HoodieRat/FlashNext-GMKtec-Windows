namespace FlashNext.Infrastructure.Windows.System;

public sealed class PlatformPaths
{
    public PlatformPaths(string? applicationDirectory = null, string? userDataRoot = null)
    {
        ApplicationDirectory = Path.GetFullPath(applicationDirectory ?? AppContext.BaseDirectory);
        UserDataRoot = Path.GetFullPath(userDataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlashNextManager"));
        ConfigDirectory = Path.Combine(UserDataRoot, "config");
        SettingsPath = Path.Combine(ConfigDirectory, "settings.json");
        SecretsPath = Path.Combine(UserDataRoot, "secrets.json");
        ApiKeyFilePath = Path.Combine(UserDataRoot, "api-key.txt");
        LogsDirectory = Path.Combine(UserDataRoot, "logs");
        MetricsDirectory = Path.Combine(UserDataRoot, "metrics");
        ConversationsDirectory = Path.Combine(UserDataRoot, "conversations");
        StateDirectory = Path.Combine(UserDataRoot, "state");
        BenchmarksDirectory = Path.Combine(UserDataRoot, "benchmarks");
        SupportDirectory = Path.Combine(UserDataRoot, "support");
    }

    public string ApplicationDirectory { get; }
    public string UserDataRoot { get; }
    public string ConfigDirectory { get; }
    public string SettingsPath { get; }
    public string SecretsPath { get; }
    public string ApiKeyFilePath { get; }
    public string LogsDirectory { get; }
    public string MetricsDirectory { get; }
    public string ConversationsDirectory { get; }
    public string StateDirectory { get; }
    public string BenchmarksDirectory { get; }
    public string SupportDirectory { get; }
    public string FactorySettingsPath => Path.Combine(ApplicationDirectory, "config", "factory.settings.json");
    public string ChatTemplatePath => Path.Combine(ApplicationDirectory, "config", "flashnext-chat.jinja");
    public string ModelLockPath => Path.Combine(ApplicationDirectory, "manifests", "model.lock.json");
    public string RuntimeLockPath => Path.Combine(ApplicationDirectory, "manifests", "runtime.lock.json");
    public string DependencyLockPath => Path.Combine(ApplicationDirectory, "manifests", "dependencies.lock.json");
    public string BootstrapLockPath => Path.Combine(ApplicationDirectory, "manifests", "bootstrap.lock.json");
    public string VulkanProbePath => Path.Combine(ApplicationDirectory, "FlashNext.VulkanProbe.exe");
    public string ModelDownloaderPath => Path.Combine(ApplicationDirectory, "scripts", "model_download.py");
    public string RuntimeBuildScriptPath => Path.Combine(ApplicationDirectory, "scripts", "Build-Runtime.ps1");
    public string RuntimeActivationScriptPath => Path.Combine(ApplicationDirectory, "scripts", "Activate-Runtime.ps1");
    public string LanScriptPath => Path.Combine(ApplicationDirectory, "scripts", "Set-LanMode.ps1");

    public void EnsureUserDirectories()
    {
        foreach (string directory in new[] { UserDataRoot, ConfigDirectory, LogsDirectory, MetricsDirectory, ConversationsDirectory, StateDirectory, BenchmarksDirectory, SupportDirectory }) Directory.CreateDirectory(directory);
    }
}
