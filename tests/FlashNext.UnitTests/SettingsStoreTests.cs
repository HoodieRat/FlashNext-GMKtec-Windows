using System.Text.Json;
using FlashNext.Core.Models;
using FlashNext.Core.Services;

namespace FlashNext.UnitTests;

public sealed class SettingsStoreTests
{
    [Fact]
    public async Task CreatesFactorySettingsAndPersistsValidatedChanges()
    {
        using TestDirectory directory = new();
        string factory = directory.File("factory.json");
        await File.WriteAllTextAsync(factory, JsonSerializer.Serialize(CreateSettings()));
        SettingsStore store = new(directory.File("settings.json"), factory);
        AppSettings loaded = await store.LoadAsync();
        Assert.Equal("coding-balanced", loaded.ActiveProfile);
        loaded.Server.Port = 18080;
        await store.SaveAsync(loaded);
        AppSettings reloaded = await store.LoadAsync();
        Assert.Equal(18080, reloaded.Server.Port);
    }

    [Fact]
    public async Task RecoversFromBackupAndPreservesInvalidDiagnosticCopy()
    {
        using TestDirectory directory = new();
        string factory = directory.File("factory.json");
        await File.WriteAllTextAsync(factory, JsonSerializer.Serialize(CreateSettings()));
        string settingsPath = directory.File("settings.json");
        SettingsStore store = new(settingsPath, factory);
        AppSettings settings = await store.LoadAsync();
        settings.Server.Port = 18081;
        await store.SaveAsync(settings);
        settings.Server.Port = 18082;
        await store.SaveAsync(settings);
        await File.WriteAllTextAsync(settingsPath, "{broken");
        AppSettings recovered = await store.LoadAsync();
        Assert.Equal(18081, recovered.Server.Port);
        Assert.Single(Directory.GetFiles(directory.Path, "settings.json.invalid-*.json"));
    }

    [Fact]
    public async Task RecoversRepeatedlyWithoutDiagnosticNameCollisions()
    {
        using TestDirectory directory = new();
        string factory = directory.File("factory.json");
        await File.WriteAllTextAsync(factory, JsonSerializer.Serialize(CreateSettings()));
        string settingsPath = directory.File("settings.json");
        SettingsStore store = new(settingsPath, factory);
        AppSettings settings = await store.LoadAsync();
        settings.Server.Port = 18081;
        await store.SaveAsync(settings);
        settings.Server.Port = 18082;
        await store.SaveAsync(settings);

        await File.WriteAllTextAsync(settingsPath, "{broken");
        AppSettings recovered = await store.LoadAsync();
        Assert.Equal(18081, recovered.Server.Port);
        recovered.Server.Port = 18083;
        await store.SaveAsync(recovered);
        recovered.Server.Port = 18084;
        await store.SaveAsync(recovered);
        await File.WriteAllTextAsync(settingsPath, "{broken");
        recovered = await store.LoadAsync();
        Assert.Equal(18083, recovered.Server.Port);

        string[] diagnostics = Directory.GetFiles(directory.Path, "settings.json.invalid-*.json");
        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, path => Assert.Matches(@"settings\.json\.invalid-\d{8}-\d{6}-[a-f0-9]{32}\.json$", Path.GetFileName(path)));
    }

    [Fact]
    public void RejectsUnsafeFactoryServerValues()
    {
        using TestDirectory directory = new();
        SettingsStore store = new(directory.File("settings.json"), directory.File("factory.json"));
        AppSettings settings = CreateSettings();
        settings.Server.WebUi = true;
        settings.Server.Agent = true;
        settings.Chat.RecordContentInLogs = true;
        Assert.NotEmpty(store.Validate(settings));
    }

    [Fact]
    public void RejectsControlPortEqualToInferencePort()
    {
        using TestDirectory directory = new();
        SettingsStore store = new(directory.File("settings.json"), directory.File("factory.json"));
        AppSettings settings = CreateSettings();
        settings.Server.ControlPort = settings.Server.Port;
        Assert.Contains(store.Validate(settings), message => message.Contains("Control port", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PreservesConfiguredOutputCap()
    {
        using TestDirectory directory = new();
        AppSettings factorySettings = CreateSettings();
        factorySettings.Profiles["coding-balanced"].MaxOutputTokens = 4096;
        factorySettings.Profiles["coding-balanced"].ContextSize = 8192;
        await File.WriteAllTextAsync(directory.File("factory.json"), JsonSerializer.Serialize(factorySettings));
        SettingsStore store = new(directory.File("settings.json"), directory.File("factory.json"));
        AppSettings loaded = await store.LoadAsync();
        Assert.Equal(4096, loaded.Profiles["coding-balanced"].MaxOutputTokens);
        Assert.Equal(8192, loaded.Profiles["coding-balanced"].ContextSize);
    }

    [Fact]
    public async Task LoadsFactoryProfileWithoutChangingSavedSettings()
    {
        using TestDirectory directory = new();
        AppSettings factorySettings = CreateSettings();
        factorySettings.Profiles["coding-balanced"].MaxOutputTokens = 32768;
        factorySettings.Profiles["coding-balanced"].ContextSize = 65536;
        await File.WriteAllTextAsync(directory.File("factory.json"), JsonSerializer.Serialize(factorySettings));
        SettingsStore store = new(directory.File("settings.json"), directory.File("factory.json"));
        AppSettings saved = await store.LoadAsync();
        saved.Profiles["coding-balanced"].MaxOutputTokens = 4096;
        saved.Profiles["coding-balanced"].ContextSize = 8192;
        await store.SaveAsync(saved);

        AppSettings factory = await store.LoadFactoryAsync();
        AppSettings reloaded = await store.LoadAsync();

        Assert.Equal(32768, factory.Profiles["coding-balanced"].MaxOutputTokens);
        Assert.Equal(65536, factory.Profiles["coding-balanced"].ContextSize);
        Assert.Equal(4096, reloaded.Profiles["coding-balanced"].MaxOutputTokens);
        Assert.Equal(8192, reloaded.Profiles["coding-balanced"].ContextSize);
    }

    [Fact]
    public void RejectsUnsupportedSpecType()
    {
        using TestDirectory directory = new();
        SettingsStore store = new(directory.File("settings.json"), directory.File("factory.json"));
        AppSettings settings = CreateSettings();
        settings.Server.SpecType = "draft-dflash";
        Assert.Contains(store.Validate(settings), message => message.Contains("specType", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AcceptsMtpDepthFour()
    {
        using TestDirectory directory = new();
        SettingsStore store = new(directory.File("settings.json"), directory.File("factory.json"));
        AppSettings settings = CreateSettings();
        settings.Profiles["coding-balanced"].MtpNMax = 4;
        Assert.DoesNotContain(store.Validate(settings), message => message.Contains("MTP n-max", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UsesBalancedVisionDetailByDefaultAndRejectsUnknownModes()
    {
        using TestDirectory directory = new();
        SettingsStore store = new(directory.File("settings.json"), directory.File("factory.json"));
        AppSettings settings = CreateSettings();
        Assert.Equal("balanced", settings.Server.VisionDetail);
        settings.Server.VisionDetail = "tiny";
        Assert.Contains(store.Validate(settings), message => message.Contains("visionDetail", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UsesSupportedBatchDefaultsAndRejectsUnsupportedValues()
    {
        using TestDirectory directory = new();
        SettingsStore store = new(directory.File("settings.json"), directory.File("factory.json"));
        AppSettings settings = CreateSettings();
        Assert.Equal(2048, settings.Server.BatchSize);
        Assert.Equal(2048, settings.Server.UBatchSize);
        settings.Server.BatchSize = 1234;
        settings.Server.UBatchSize = 777;
        IReadOnlyList<string> errors = store.Validate(settings);
        Assert.Contains(errors, message => message.Contains("batchSize", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, message => message.Contains("ubatchSize", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReportsNullNestedSettingsAsValidationErrors()
    {
        using TestDirectory directory = new();
        SettingsStore store = new(directory.File("settings.json"), directory.File("factory.json"));
        AppSettings settings = CreateSettings();
        settings.Server = null!;
        settings.Profiles = null!;
        IReadOnlyList<string> errors = store.Validate(settings);
        Assert.Contains(errors, message => message.Contains("server is required", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, message => message.Contains("profiles is required", StringComparison.OrdinalIgnoreCase));
    }

    internal static AppSettings CreateSettings()
    {
        return new AppSettings
        {
            ActiveProfile = "coding-balanced",
            Server = new ServerSettings { Port = 8080, ControlPort = 18091 },
            Profiles = new Dictionary<string, InferenceProfile>(StringComparer.OrdinalIgnoreCase) { ["coding-balanced"] = new() { Thinking = true, ReasoningEffort = "medium", Temperature = 1, TopP = 0.95, TopK = 20, ContextSize = 32768, MaxOutputTokens = 4096, MtpNMax = 3 } }
        };
    }
}
