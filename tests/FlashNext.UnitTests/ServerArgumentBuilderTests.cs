using FlashNext.Core.Models;
using FlashNext.Core.Services;

namespace FlashNext.UnitTests;

public sealed class ServerArgumentBuilderTests
{
    private const string PerformanceHelp = " -t -tb --device --fit -ot --load-mode --lazy-mode --prio --prio-batch --ngram-on-disk --ngram-io-threads --ngram-cache --ngram-direct-io --no-context-shift --parallel --alias";
    private const string DraftCacheHelp = " --spec-draft-device --spec-draft-prio --spec-draft-prio-batch --spec-draft-type-k --spec-draft-type-v";

    [Fact]
    public void BuildsFactoryCommandAsDiscreteArguments()
    {
        using TestDirectory directory = new();
        string model = directory.File("model");
        Directory.CreateDirectory(Path.Combine(model, "UD-Q4_K_XL"));
        Directory.CreateDirectory(Path.Combine(model, "MTP"));
        File.WriteAllBytes(Path.Combine(model, "UD-Q4_K_XL", "Qwen3.8-Flash-Next-UD-Q4_K_XL-00001-of-00004.gguf"), "GGUF"u8.ToArray());
        File.WriteAllBytes(Path.Combine(model, "MTP", "mtp-Qwen3.8-Flash-Next-shared-Q8_0.gguf"), "GGUF"u8.ToArray());
        string key = directory.File("key.txt"); File.WriteAllText(key, "secret");
        AppSettings settings = SettingsStoreTests.CreateSettings(); settings.Paths.ModelDirectory = model;
        string help = "-m -md -ngl -fa -ctk -ctv -b -ub --n-gpu-layers-draft --spec-type --spec-draft-p-min --spec-draft-n-max -c --host --port --api-key-file --metrics --jinja --no-agent --no-ui --cache-prompt --allowed-origin" + PerformanceHelp + DraftCacheHelp;
        IReadOnlyList<string> args = new ServerArgumentBuilder().Build(settings, directory.Path, key, help);
        Assert.Contains("--spec-type", args);
        Assert.Contains("draft-mtp", args);
        Assert.Equal("on", args[args.ToList().IndexOf("-fa") + 1]);
        Assert.Equal("q8_0", args[args.ToList().IndexOf("-ctk") + 1]);
        Assert.Equal("q8_0", args[args.ToList().IndexOf("-ctv") + 1]);
        Assert.DoesNotContain("-ot", args);
        Assert.DoesNotContain("--lazy-mode", args);
        Assert.Equal("2", args[args.ToList().IndexOf("--prio") + 1]);
        Assert.Equal("Vulkan0", args[args.ToList().IndexOf("--spec-draft-device") + 1]);
        Assert.Contains("--no-agent", args);
        Assert.Contains("--no-ui", args);
        Assert.Contains("127.0.0.1", args);
        Assert.DoesNotContain("0.0.0.0", args);
    }

    [Fact]
    public void RejectsExtraArgumentThatOverridesManagedSecurity()
    {
        Assert.True(ServerArgumentBuilder.IsReservedExtraArgument("--host"));
        Assert.True(ServerArgumentBuilder.IsReservedExtraArgument("--api-key=unsafe"));
        Assert.True(ServerArgumentBuilder.IsReservedExtraArgument("--agent"));
        Assert.True(ServerArgumentBuilder.IsReservedExtraArgument("--image-max-tokens=2048"));
        Assert.True(ServerArgumentBuilder.IsReservedExtraArgument("--threads"));
        Assert.True(ServerArgumentBuilder.IsReservedExtraArgument("--ngram-on-disk"));
        Assert.True(ServerArgumentBuilder.IsReservedExtraArgument("--no-ngram-direct-io"));
        Assert.True(ServerArgumentBuilder.IsReservedExtraArgument("--lazy-mode"));
        Assert.True(ServerArgumentBuilder.IsReservedExtraArgument("--spec-draft-device"));
        Assert.False(ServerArgumentBuilder.IsReservedExtraArgument("--spec-draft-adaptive"));
        Assert.False(ServerArgumentBuilder.IsReservedExtraArgument("--spec-draft-n-min"));
    }

    [Fact]
    public void RejectsRuntimeWithoutRequiredFeature()
    {
        using TestDirectory directory = new();
        AppSettings settings = SettingsStoreTests.CreateSettings();
        settings.Paths.ModelDirectory = directory.Path;
        Assert.Throws<InvalidOperationException>(() => new ServerArgumentBuilder().Build(settings, directory.Path, directory.File("key"), "-m"));
    }

    [Fact]
    public void AcceptsLlamaCppCommaSeparatedHelpAliases()
    {
        Assert.True(ServerArgumentBuilder.ContainsOption("-m,    --model FNAME", "-m"));
        Assert.True(ServerArgumentBuilder.ContainsOption("--spec-draft-model, -md, --model-draft FNAME", "-md"));
        Assert.True(ServerArgumentBuilder.ContainsOption("-ngl,  --gpu-layers, --n-gpu-layers N", "-ngl"));
        Assert.True(ServerArgumentBuilder.ContainsOption("-c,    --ctx-size N", "-c"));
        Assert.True(ServerArgumentBuilder.ContainsOption("--ui,  --webui, --no-ui, --no-webui", "--no-ui"));
        Assert.True(ServerArgumentBuilder.ContainsOption("--cache-prompt, --no-cache-prompt", "--cache-prompt"));
        Assert.False(ServerArgumentBuilder.ContainsOption("-md, --model-draft FNAME", "-m"));
        Assert.False(ServerArgumentBuilder.ContainsOption("--no-ui-mcp-proxy, --no-webui-mcp-proxy", "--no-ui"));
    }

    [Fact]
    public void BuildsFromRealLlamaServerHelpFormatting()
    {
        using TestDirectory directory = new();
        string model = directory.File("model");
        Directory.CreateDirectory(Path.Combine(model, "UD-Q4_K_XL"));
        Directory.CreateDirectory(Path.Combine(model, "MTP"));
        File.WriteAllBytes(Path.Combine(model, "UD-Q4_K_XL", "Qwen3.8-Flash-Next-UD-Q4_K_XL-00001-of-00004.gguf"), "GGUF"u8.ToArray());
        File.WriteAllBytes(Path.Combine(model, "MTP", "mtp-Qwen3.8-Flash-Next-shared-Q8_0.gguf"), "GGUF"u8.ToArray());
        string key = directory.File("key.txt"); File.WriteAllText(key, "secret");
        AppSettings settings = SettingsStoreTests.CreateSettings(); settings.Paths.ModelDirectory = model;
        const string help = """
            -m,    --model FNAME
            -ngl,  --gpu-layers, --n-gpu-layers N
            -fa, --flash-attn [on|off|auto]
            -ctk, --cache-type-k TYPE
            -ctv, --cache-type-v TYPE
            -b, --batch-size N
            -ub, --ubatch-size N
            -t, --threads N
            -tb, --threads-batch N
            -dev, --device Vulkan0
            -fit, --fit [on|off]
            -ot, --override-tensor PATTERN=BUFFER_TYPE
            -lm, --load-mode MODE
            --lazy-mode [on|off|auto]
            --prio N
            --prio-batch N
            --ngram-on-disk
            --ngram-io-threads N
            --ngram-cache MiB
            --ngram-direct-io, --no-ngram-direct-io
            --context-shift, --no-context-shift
            -c,    --ctx-size N
            --spec-draft-ngl, -ngld, --gpu-layers-draft, --n-gpu-layers-draft N
            --spec-draft-model, -md, --model-draft FNAME
            --spec-draft-device DEVICE
            --spec-draft-prio N
            --spec-draft-prio-batch N
            --spec-type none,draft-simple,draft-mtp
            --spec-draft-p-min --spec-draft-n-max N
            --spec-draft-type-k TYPE --spec-draft-type-v TYPE
            --host HOST
            --port PORT
            --api-key-file FNAME
            --metrics
            --jinja
            -ag, --agent, --no-agent
            --ui, --webui, --no-ui, --no-webui
            --cache-prompt, --no-cache-prompt
            --parallel N
            --alias NAME
            """;
        IReadOnlyList<string> args = new ServerArgumentBuilder().Build(settings, directory.Path, key, help);
        Assert.Contains("-md", args);
        Assert.Contains("--spec-type", args);
        Assert.Contains("draft-mtp", args);
        Assert.Equal("2048", args[args.ToList().IndexOf("-b") + 1]);
        Assert.Equal("2048", args[args.ToList().IndexOf("-ub") + 1]);
        Assert.DoesNotContain("--ngram-on-disk", args);
        Assert.Equal("Vulkan0", args[args.ToList().IndexOf("--device") + 1]);
        Assert.Equal("q8_0", args[args.ToList().IndexOf("--spec-draft-type-k") + 1]);
    }

    [Fact]
    public void IncludesAdaptiveMtpExtrasAndKeepsFactorySpecType()
    {
        using TestDirectory directory = new();
        string model = directory.File("model");
        Directory.CreateDirectory(Path.Combine(model, "UD-Q4_K_XL"));
        Directory.CreateDirectory(Path.Combine(model, "MTP"));
        File.WriteAllBytes(Path.Combine(model, "UD-Q4_K_XL", "Qwen3.8-Flash-Next-UD-Q4_K_XL-00001-of-00004.gguf"), "GGUF"u8.ToArray());
        File.WriteAllBytes(Path.Combine(model, "MTP", "mtp-Qwen3.8-Flash-Next-shared-Q8_0.gguf"), "GGUF"u8.ToArray());
        string key = directory.File("key.txt"); File.WriteAllText(key, "secret");
        AppSettings settings = SettingsStoreTests.CreateSettings();
        settings.Paths.ModelDirectory = model;
        settings.Server.ExtraArguments = ["--spec-draft-adaptive", "--spec-draft-n-min", "3"];
        string help = "-m -md -ngl -fa -ctk -ctv -b -ub --n-gpu-layers-draft --spec-type --spec-draft-n-min --spec-draft-p-min --spec-draft-n-max --spec-draft-adaptive -c --host --port --api-key-file --metrics --jinja --no-agent --no-ui --cache-prompt" + PerformanceHelp + DraftCacheHelp;
        IReadOnlyList<string> args = new ServerArgumentBuilder().Build(settings, directory.Path, key, help);
        Assert.Equal("draft-mtp", args[args.ToList().IndexOf("--spec-type") + 1]);
        Assert.Contains("--spec-draft-adaptive", args);
        Assert.Contains("--spec-draft-n-min", args);
        Assert.Equal("3", args[args.ToList().IndexOf("--spec-draft-n-min") + 1]);
    }

    [Fact]
    public void AddsInstalledVisionProjectorToTheManagedCommand()
    {
        using TestDirectory directory = new();
        string model = directory.File("model");
        Directory.CreateDirectory(Path.Combine(model, "UD-Q4_K_XL"));
        Directory.CreateDirectory(Path.Combine(model, "MTP"));
        File.WriteAllBytes(Path.Combine(model, "UD-Q4_K_XL", "Qwen3.8-Flash-Next-UD-Q4_K_XL-00001-of-00004.gguf"), "GGUF"u8.ToArray());
        File.WriteAllBytes(Path.Combine(model, "MTP", "mtp-Qwen3.8-Flash-Next-shared-Q8_0.gguf"), "GGUF"u8.ToArray());
        string projector = Path.Combine(model, "mmproj-F16.gguf");
        File.WriteAllBytes(projector, "GGUF"u8.ToArray());
        string key = directory.File("key.txt"); File.WriteAllText(key, "secret");
        AppSettings settings = SettingsStoreTests.CreateSettings(); settings.Paths.ModelDirectory = model;
        string help = "-m -md -ngl -fa -ctk -ctv -b -ub --mmproj --image-min-tokens --image-max-tokens --n-gpu-layers-draft --spec-type --spec-draft-p-min --spec-draft-n-max -c --host --port --api-key-file --metrics --jinja --no-agent --no-ui --cache-prompt" + PerformanceHelp + DraftCacheHelp;

        IReadOnlyList<string> args = new ServerArgumentBuilder().Build(settings, directory.Path, key, help);

        Assert.Equal(projector, args[args.ToList().IndexOf("--mmproj") + 1]);
        Assert.Equal("1024", args[args.ToList().IndexOf("--image-min-tokens") + 1]);
        Assert.Equal("4096", args[args.ToList().IndexOf("--image-max-tokens") + 1]);
    }

    [Fact]
    public void MaximumVisionDetailLeavesImageTokenLimitsAtRuntimeDefaults()
    {
        using TestDirectory directory = new();
        string model = directory.File("model");
        Directory.CreateDirectory(Path.Combine(model, "UD-Q4_K_XL"));
        Directory.CreateDirectory(Path.Combine(model, "MTP"));
        File.WriteAllBytes(Path.Combine(model, "UD-Q4_K_XL", "Qwen3.8-Flash-Next-UD-Q4_K_XL-00001-of-00004.gguf"), "GGUF"u8.ToArray());
        File.WriteAllBytes(Path.Combine(model, "MTP", "mtp-Qwen3.8-Flash-Next-shared-Q8_0.gguf"), "GGUF"u8.ToArray());
        File.WriteAllBytes(Path.Combine(model, "mmproj-F16.gguf"), "GGUF"u8.ToArray());
        string key = directory.File("key.txt"); File.WriteAllText(key, "secret");
        AppSettings settings = SettingsStoreTests.CreateSettings();
        settings.Paths.ModelDirectory = model;
        settings.Server.VisionDetail = "maximum";
        string help = "-m -md -ngl -fa -ctk -ctv -b -ub --mmproj --n-gpu-layers-draft --spec-type --spec-draft-p-min --spec-draft-n-max -c --host --port --api-key-file --metrics --jinja --no-agent --no-ui --cache-prompt" + PerformanceHelp + DraftCacheHelp;

        IReadOnlyList<string> args = new ServerArgumentBuilder().Build(settings, directory.Path, key, help);

        Assert.DoesNotContain("--image-min-tokens", args);
        Assert.DoesNotContain("--image-max-tokens", args);
    }

    [Fact]
    public void AllowsExperimentalNgramSimpleAndRejectsDflash()
    {
        using TestDirectory directory = new();
        string model = directory.File("model");
        Directory.CreateDirectory(Path.Combine(model, "UD-Q4_K_XL"));
        Directory.CreateDirectory(Path.Combine(model, "MTP"));
        File.WriteAllBytes(Path.Combine(model, "UD-Q4_K_XL", "Qwen3.8-Flash-Next-UD-Q4_K_XL-00001-of-00004.gguf"), "GGUF"u8.ToArray());
        File.WriteAllBytes(Path.Combine(model, "MTP", "mtp-Qwen3.8-Flash-Next-shared-Q8_0.gguf"), "GGUF"u8.ToArray());
        string key = directory.File("key.txt"); File.WriteAllText(key, "secret");
        AppSettings settings = SettingsStoreTests.CreateSettings();
        settings.Paths.ModelDirectory = model;
        settings.Server.SpecType = "ngram-simple";
        string help = "-m -md -ngl -fa -ctk -ctv -b -ub --n-gpu-layers-draft --spec-type --spec-draft-p-min --spec-draft-n-max -c --host --port --api-key-file --metrics --jinja --no-agent --no-ui --cache-prompt" + PerformanceHelp + DraftCacheHelp;
        IReadOnlyList<string> args = new ServerArgumentBuilder().Build(settings, directory.Path, key, help);
        Assert.Contains("ngram-simple", args);
        Assert.DoesNotContain("draft-mtp", args);
        settings.Server.SpecType = "draft-dflash";
        Assert.Throws<InvalidDataException>(() => new ServerArgumentBuilder().Build(settings, directory.Path, key, help));
    }

    [Fact]
    public void BuildsMtpOffBaselineWithoutDraftModelOrDepthArguments()
    {
        using TestDirectory directory = new();
        string model = directory.File("model");
        Directory.CreateDirectory(Path.Combine(model, "UD-Q4_K_XL"));
        File.WriteAllBytes(Path.Combine(model, "UD-Q4_K_XL", "Qwen3.8-Flash-Next-UD-Q4_K_XL-00001-of-00004.gguf"), "GGUF"u8.ToArray());
        string key = directory.File("key.txt"); File.WriteAllText(key, "secret");
        AppSettings settings = SettingsStoreTests.CreateSettings();
        settings.Paths.ModelDirectory = model;
        settings.Server.SpecType = "none";
        settings.Server.ExtraArguments = ["--spec-draft-adaptive", "--spec-draft-n-min", "3"];
        string help = "-m -ngl -fa -ctk -ctv -b -ub --spec-type --spec-draft-p-min -c --host --port --api-key-file --metrics --jinja --no-agent --no-ui --cache-prompt" + PerformanceHelp;

        IReadOnlyList<string> args = new ServerArgumentBuilder().Build(settings, directory.Path, key, help);

        Assert.Equal("none", args[args.ToList().IndexOf("--spec-type") + 1]);
        Assert.Equal("0.75", args[args.ToList().IndexOf("--spec-draft-p-min") + 1]);
        Assert.DoesNotContain("-md", args);
        Assert.DoesNotContain("--n-gpu-layers-draft", args);
        Assert.DoesNotContain("--spec-draft-n-max", args);
        Assert.DoesNotContain("--spec-draft-adaptive", args);
        Assert.DoesNotContain("--spec-draft-n-min", args);
    }
}
