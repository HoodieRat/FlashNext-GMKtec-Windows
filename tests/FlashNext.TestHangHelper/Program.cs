using System.Diagnostics;
using System.Globalization;

namespace FlashNext.TestHangHelper;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        int sleepMilliseconds = 60_000;
        bool spawnChild = false;
        bool crash = false;
        string? stdoutPrefix = "hang-helper";
        for (int index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], "--sleep-ms", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                sleepMilliseconds = int.Parse(args[++index], CultureInfo.InvariantCulture);
                continue;
            }
            if (string.Equals(args[index], "--spawn-child", StringComparison.OrdinalIgnoreCase))
            {
                spawnChild = true;
                continue;
            }
            if (string.Equals(args[index], "--crash", StringComparison.OrdinalIgnoreCase))
            {
                crash = true;
                continue;
            }
            if (string.Equals(args[index], "--stdout-prefix", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                stdoutPrefix = args[++index];
                continue;
            }
        }

        Console.WriteLine($"{stdoutPrefix}: pid={Environment.ProcessId}");
        Console.Out.Flush();
        if (crash) Environment.FailFast("intentional hang-helper crash");
        if (spawnChild)
        {
            string self = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "FlashNext.TestHangHelper.exe";
            ProcessStartInfo child = new()
            {
                FileName = self,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            child.ArgumentList.Add("--sleep-ms");
            child.ArgumentList.Add(sleepMilliseconds.ToString(CultureInfo.InvariantCulture));
            child.ArgumentList.Add("--stdout-prefix");
            child.ArgumentList.Add("hang-child");
            Process.Start(child);
        }

        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMilliseconds(Math.Max(0, sleepMilliseconds));
        while (DateTimeOffset.UtcNow < deadline)
        {
            Thread.Sleep(200);
        }
        Console.WriteLine($"{stdoutPrefix}: completed");
        return 0;
    }
}
