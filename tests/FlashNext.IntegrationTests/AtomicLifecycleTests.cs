using FlashNext.Core.Services;

namespace FlashNext.IntegrationTests;

public sealed class AtomicLifecycleTests
{
    [Fact]
    public void DirectoryPromotionPreservesPreviousSlot()
    {
        string root = Path.Combine(Path.GetTempPath(), "FlashNextIntegration", Guid.NewGuid().ToString("N"));
        try
        {
            string staging = Path.Combine(root, "staging"); string current = Path.Combine(root, "current"); string previous = Path.Combine(root, "previous");
            Directory.CreateDirectory(staging); Directory.CreateDirectory(current);
            File.WriteAllText(Path.Combine(staging, "version.txt"), "new");
            File.WriteAllText(Path.Combine(current, "version.txt"), "old");
            AtomicFile.PromoteDirectory(staging, current, previous);
            Assert.Equal("new", File.ReadAllText(Path.Combine(current, "version.txt")));
            Assert.Equal("old", File.ReadAllText(Path.Combine(previous, "version.txt")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
