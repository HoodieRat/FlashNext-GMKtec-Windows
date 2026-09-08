using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FlashNext.Core.Interfaces;
using FlashNext.Core.Models;
using FlashNext.Core.Services;
using FlashNext.Infrastructure.Windows.Control;
using FlashNext.Infrastructure.Windows.System;

namespace FlashNext.IntegrationTests;

public sealed class ControlApiTests
{
    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public async Task PatchesHigherDepthAndBatchesWithRestartRequired(int depth)
    {
        await using Host host = await Host.CreateAsync();
        string patch = JsonSerializer.Serialize(new { profiles = new Dictionary<string, object> { ["coding-balanced"] = new { mtpNMax = depth } }, server = new { batchSize = 4096, ubatchSize = 2048, extraArguments = new[] { "--spec-draft-adaptive", "--spec-draft-n-min", "2" } } });
        ControlApiResult result = await host.Router.HandleAsync("PATCH", "/flashnext/settings", host.Bearer, patch, IPAddress.Loopback);
        Assert.Equal(200, result.StatusCode);
        using JsonDocument json = JsonDocument.Parse(result.Body);
        Assert.True(json.RootElement.GetProperty("restartRequired").GetBoolean());
        AppSettings saved = await host.Store.LoadAsync();
        Assert.Equal(depth, saved.GetActiveProfile().MtpNMax);
        Assert.Equal(2048, saved.Server.UBatchSize);
        ControlApiResult invalid = await host.Router.HandleAsync("PATCH", "/flashnext/settings", host.Bearer, """{"server":{"batchSize":1024,"ubatchSize":2048}}""", IPAddress.Loopback);
        Assert.Equal(400, invalid.StatusCode);
        Assert.Equal(4096, (await host.Store.LoadAsync()).Server.BatchSize);
    }

    [Fact]
    public async Task RejectsMissingBearerAndUnknownFields()
    {
        await using Host host = await Host.CreateAsync();
        ControlApiResult missing = await host.Router.HandleAsync("GET", "/flashnext/status", null, "", IPAddress.Loopback);
        Assert.Equal(401, missing.StatusCode);
        ControlApiResult unknown = await host.Router.HandleAsync("PATCH", "/flashnext/settings", host.Bearer, """{"server":{"webUi":true}}""", IPAddress.Loopback);
        Assert.Equal(400, unknown.StatusCode);
        Assert.Contains("not writable", unknown.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PatchingTemperatureDoesNotRequireRestart()
    {
        await using Host host = await Host.CreateAsync();
        ControlApiResult result = await host.Router.HandleAsync("PATCH", "/flashnext/settings", host.Bearer, """{"profiles":{"coding-balanced":{"temperature":0.4}}}""", IPAddress.Loopback);
        Assert.Equal(200, result.StatusCode);
        using JsonDocument document = JsonDocument.Parse(result.Body);
        Assert.True(document.RootElement.GetProperty("saved").GetBoolean());
        Assert.False(document.RootElement.GetProperty("restartRequired").GetBoolean());
        AppSettings loaded = await host.Store.LoadAsync();
        Assert.Equal(0.4, loaded.Profiles["coding-balanced"].Temperature);
    }

    [Fact]
    public async Task PatchingMtpRequiresRestartAndStartStopWork()
    {
        await using Host host = await Host.CreateAsync();
        ControlApiResult patch = await host.Router.HandleAsync("PATCH", "/flashnext/settings", host.Bearer, """{"profiles":{"coding-balanced":{"mtpNMax":2}}}""", IPAddress.Loopback);
        Assert.Equal(200, patch.StatusCode);
        using JsonDocument document = JsonDocument.Parse(patch.Body);
        Assert.True(document.RootElement.GetProperty("restartRequired").GetBoolean());
        ControlApiResult start = await host.Router.HandleAsync("POST", "/flashnext/server/start", host.Bearer, "", IPAddress.Loopback);
        Assert.Equal(200, start.StatusCode);
        Assert.Equal(1, host.Supervisor.Starts);
        ControlApiResult status = await host.Router.HandleAsync("GET", "/flashnext/status", host.Bearer, "", IPAddress.Loopback);
        Assert.Contains("Running", status.Body, StringComparison.Ordinal);
        ControlApiResult stop = await host.Router.HandleAsync("POST", "/flashnext/server/stop", host.Bearer, "", IPAddress.Loopback);
        Assert.Equal(200, stop.StatusCode);
        Assert.Equal(1, host.Supervisor.Stops);
        ControlApiResult spec = await host.Router.HandleAsync("PATCH", "/flashnext/settings", host.Bearer, """{"server":{"specType":"ngram-simple"}}""", IPAddress.Loopback);
        Assert.Equal(200, spec.StatusCode);
        using JsonDocument specDoc = JsonDocument.Parse(spec.Body);
        Assert.True(specDoc.RootElement.GetProperty("restartRequired").GetBoolean());
        ControlApiResult baseline = await host.Router.HandleAsync("PATCH", "/flashnext/settings", host.Bearer, """{"server":{"specType":"none"}}""", IPAddress.Loopback);
        Assert.Equal(200, baseline.StatusCode);
        using JsonDocument baselineDoc = JsonDocument.Parse(baseline.Body);
        Assert.True(baselineDoc.RootElement.GetProperty("restartRequired").GetBoolean());
        ControlApiResult dflash = await host.Router.HandleAsync("PATCH", "/flashnext/settings", host.Bearer, """{"server":{"specType":"draft-dflash"}}""", IPAddress.Loopback);
        Assert.Equal(400, dflash.StatusCode);
        ControlApiResult priority = await host.Router.HandleAsync("PATCH", "/flashnext/settings", host.Bearer, """{"server":{"extraArguments":["--prio","2"]}}""", IPAddress.Loopback);
        Assert.Equal(400, priority.StatusCode);
        AppSettings withPriority = await host.Store.LoadAsync();
        Assert.Empty(withPriority.Server.ExtraArguments);
    }

    [Fact]
    public async Task HttpListenerServesHealthWithBearer()
    {
        await using Host host = await Host.CreateAsync();
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        await using ControlApiServer server = new(host.Store, host.Supervisor, host.Secrets, host.Paths);
        await server.StartAsync(port);
        using HttpClient client = new();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", host.Secrets.Key);
        HttpResponseMessage response = await client.GetAsync(server.Prefix + "health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class Host : IAsyncDisposable
    {
        private Host(string root, SettingsStore store, FakeSupervisor supervisor, MemorySecrets secrets, PlatformPaths paths, ControlApiRouter router)
        {
            Root = root;
            Store = store;
            Supervisor = supervisor;
            Secrets = secrets;
            Paths = paths;
            Router = router;
            Bearer = "Bearer " + secrets.Key;
        }

        public string Root { get; }
        public SettingsStore Store { get; }
        public FakeSupervisor Supervisor { get; }
        public MemorySecrets Secrets { get; }
        public PlatformPaths Paths { get; }
        public ControlApiRouter Router { get; }
        public string Bearer { get; }

        public static async Task<Host> CreateAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "FlashNextTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string factory = Path.Combine(root, "factory.json");
            AppSettings settings = SettingsStoreTestsSettings.Create();
            await File.WriteAllTextAsync(factory, JsonSerializer.Serialize(settings, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            SettingsStore store = new(Path.Combine(root, "settings.json"), factory);
            await store.SaveAsync(settings);
            FakeSupervisor supervisor = new();
            MemorySecrets secrets = new();
            PlatformPaths paths = new(root, root);
            return new Host(root, store, supervisor, secrets, paths, new ControlApiRouter(store, supervisor, secrets, paths));
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
            return ValueTask.CompletedTask;
        }
    }

    private static class SettingsStoreTestsSettings
    {
        public static AppSettings Create()
        {
            return new AppSettings
            {
                ActiveProfile = "coding-balanced",
                Server = new ServerSettings { Port = 8080, ControlPort = 18091 },
                Profiles = new Dictionary<string, InferenceProfile>(StringComparer.OrdinalIgnoreCase)
                {
                    ["coding-balanced"] = new() { Thinking = true, ReasoningEffort = "medium", Temperature = 1, TopP = 0.95, TopK = 20, ContextSize = 32768, MaxOutputTokens = 4096, MtpNMax = 3 }
                }
            };
        }
    }

    private sealed class MemorySecrets : ISecretStore
    {
        public string SecretPath => "memory";
        public string Key { get; } = new string('k', 40);
        public Task<string> GetOrCreateApiKeyAsync(CancellationToken cancellationToken = default) => Task.FromResult(Key);
        public Task<string> RotateApiKeyAsync(CancellationToken cancellationToken = default) => Task.FromResult(Key);
        public Task<string> GetApiKeyAsync(CancellationToken cancellationToken = default) => Task.FromResult(Key);
        public string Redact(string value) => value;
    }

    private sealed class FakeSupervisor : IRuntimeSupervisor
    {
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public ServerStatus Status { get; private set; } = new() { State = ServerLifecycleState.Stopped };

        public Task<bool> AttachIfHealthyAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<ServerStatus> StartAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            Starts++;
            Status = new ServerStatus { State = ServerLifecycleState.Running, ProcessId = 42, IsOwnedProcess = true, BaseUri = new Uri("http://127.0.0.1:8080/") };
            return Task.FromResult(Status);
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            Stops++;
            Status = new ServerStatus { State = ServerLifecycleState.Stopped };
            return Task.CompletedTask;
        }

        public async Task<ServerStatus> RestartAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            await StopAsync(cancellationToken).ConfigureAwait(false);
            return await StartAsync(settings, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
