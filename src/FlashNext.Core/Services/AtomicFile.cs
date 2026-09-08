using System.Text;

namespace FlashNext.Core.Services;

public static class AtomicFile
{
    public static async Task WriteTextAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporary = fullPath + ".new-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        using (FileStream stream = new(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(true);
        }
        if (File.Exists(fullPath))
        {
            string backup = fullPath + ".bak";
            File.Replace(temporary, fullPath, backup, true);
        }
        else
        {
            File.Move(temporary, fullPath);
        }
    }

    public static void PromoteDirectory(string staging, string current, string previous)
    {
        staging = Path.GetFullPath(staging);
        current = Path.GetFullPath(current);
        previous = Path.GetFullPath(previous);
        if (!Directory.Exists(staging)) throw new DirectoryNotFoundException(staging);
        if (Directory.Exists(previous)) Directory.Delete(previous, true);
        if (Directory.Exists(current)) Directory.Move(current, previous);
        try
        {
            Directory.Move(staging, current);
        }
        catch
        {
            if (!Directory.Exists(current) && Directory.Exists(previous)) Directory.Move(previous, current);
            throw;
        }
    }
}
