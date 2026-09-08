using System.Globalization;
using System.Text.Json;
using FlashNext.Core.Models;
using FlashNext.Core.Services;

namespace FlashNext.UnitTests;

public sealed class SpecDraftConfidenceTests
{
    [Theory]
    [InlineData(0.00, "0.00")]
    [InlineData(0.50, "0.50")]
    [InlineData(0.65, "0.65")]
    [InlineData(0.75, "0.75")]
    [InlineData(0.80, "0.80")]
    public async Task RoundTripsAndBuildsInvariantArgumentsForFixedAndAdaptiveModes(double value, string expected)
    {
        using TestDirectory directory = new();
        SettingsStore store = new(directory.File("settings.json"), directory.File("factory.json"));
        AppSettings settings = SettingsStoreTests.CreateSettings();
        settings.Paths.ModelDirectory = directory.Path;
        Directory.CreateDirectory(directory.File("UD-Q4_K_XL"));
        Directory.CreateDirectory(directory.File("MTP"));
        File.WriteAllText(directory.File("UD-Q4_K_XL/Qwen3.8-Flash-Next-UD-Q4_K_XL-00001-of-00004.gguf"), "fixture");
        File.WriteAllText(directory.File("MTP/mtp-Qwen3.8-Flash-Next-shared-Q8_0.gguf"), "fixture");
        string key = directory.File("key.txt");
        File.WriteAllText(key, "test-key");
        const string help = "-m -md -ngl -fa -ctk -ctv -b -ub -t -tb --device --fit -ot --load-mode --lazy-mode --prio --prio-batch --ngram-on-disk --ngram-io-threads --ngram-cache --ngram-direct-io --no-context-shift --n-gpu-layers-draft --spec-type --spec-draft-n-max --spec-draft-p-min --spec-draft-n-min --spec-draft-adaptive --spec-draft-device --spec-draft-prio --spec-draft-prio-batch --spec-draft-type-k --spec-draft-type-v -c --host --port --api-key-file --metrics --jinja --no-agent --no-ui --cache-prompt --parallel --alias";
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            settings.Server.SpecDraftPMin = value;
            settings.GetActiveProfile().MinP = 0.05;
            await store.SaveAsync(settings);
            settings = await store.LoadAsync();
            Assert.Equal(value, settings.Server.SpecDraftPMin);
            Assert.Equal(0.05, settings.GetActiveProfile().MinP);
            foreach (int depth in Enumerable.Range(1, 6))
            foreach (bool adaptive in new[] { false, true })
            {
                if (adaptive && depth < 2) continue;
                settings.GetActiveProfile().MtpNMax = depth;
                settings.Server.ExtraArguments = adaptive ? ["--spec-draft-adaptive", "--spec-draft-n-min", "2"] : [];
                List<string> args = [.. new ServerArgumentBuilder().Build(settings, directory.Path, key, help)];
                Assert.Equal(expected, args[args.IndexOf("--spec-draft-p-min") + 1]);
                Assert.Single(args, argument => argument == "--spec-draft-p-min");
                Assert.Equal(adaptive, args.Contains("--spec-draft-adaptive"));
                Assert.DoesNotContain("--min-p", args);
            }
            settings.GetActiveProfile().MinP = 0.1;
            Assert.Equal(value, settings.Server.SpecDraftPMin);
            Assert.Throws<InvalidOperationException>(() => new ServerArgumentBuilder().Build(settings, directory.Path, key, help.Replace("--spec-draft-p-min", "")));
            settings.Server.SpecType = "none";
            List<string> off = [.. new ServerArgumentBuilder().Build(settings, directory.Path, key, help)];
            Assert.Equal(expected, off[off.IndexOf("--spec-draft-p-min") + 1]);
            Assert.DoesNotContain("-md", off);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public void LegacySettingsUseReliableThresholdButLegacySnapshotsStayUnknown()
    {
        Assert.Equal(0.75, JsonSerializer.Deserialize<ServerSettings>("{}")!.SpecDraftPMin);
        RuntimeConfiguration old = new(32768, 4, "draft-mtp", false, 0, 2048, 512, "balanced", 8080, "127.0.0.1", 99, 99, "");
        Assert.Null(old.SpecDraftPMin);
        Assert.Null(JsonSerializer.Deserialize<RuntimeConfiguration>(JsonSerializer.Serialize(old))!.SpecDraftPMin);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(0.7)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void RejectsUnsupportedThresholds(double value)
    {
        using TestDirectory directory = new();
        AppSettings settings = SettingsStoreTests.CreateSettings();
        settings.Server.SpecDraftPMin = value;
        Assert.Contains(new SettingsStore(directory.File("settings.json"), directory.File("factory.json")).Validate(settings), error => error.Contains("specDraftPMin"));
    }

    [Theory]
    [InlineData("--spec-draft-p-min")]
    [InlineData("--draft-p-min=0.75")]
    public void RejectsExtraArgumentOverridesThatWouldMakeReportsInaccurate(string argument)
    {
        using TestDirectory directory = new();
        AppSettings settings = SettingsStoreTests.CreateSettings();
        settings.Server.ExtraArguments = [argument];
        Assert.Contains(new SettingsStore(directory.File("settings.json"), directory.File("factory.json")).Validate(settings), error => error.Contains("override"));
    }
}
