using FlashNext.Core.Models;
using Spectre.Console;

namespace FlashNext.Manager.ConsoleUi;

public static class ConsoleFormatting
{
    public static void Header()
    {
        AnsiConsole.Clear();
        AnsiConsole.Write(new FigletText("FlashNext").Color(Color.Aqua));
        AnsiConsole.MarkupLine("[grey]Qwen3.8-Flash-Next • Windows Vulkan • GMKtec EVO-X2[/]");
        AnsiConsole.Write(new Rule());
    }

    public static void ShowMetrics(ResponseMetrics value)
    {
        AnsiConsole.Write(new Rule("[aqua]Benchmark[/]").LeftJustified());
        AnsiConsole.Write(new Panel(Markup.Escape(FlashNext.Core.Services.BenchmarkReportFormatter.Format(value)))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Grey),
            Padding = new Padding(1, 0)
        });
    }

    public static void ShowHardware(HardwareReport report)
    {
        Table table = new Table().RoundedBorder();
        table.AddColumn("Hardware check"); table.AddColumn("Detected"); table.AddColumn("Result");
        table.AddRow("Windows", Markup.Escape(report.WindowsVersion), Pass(report.IsWindows11X64));
        table.AddRow("CPU", Markup.Escape(report.CpuName), Pass(report.MeetsCpuRequirement));
        table.AddRow("GPU", Markup.Escape(report.GpuName), Pass(report.MeetsGpuRequirement));
        string installedRam = report.PhysicalMemoryBytes > 0
            ? $"{report.PhysicalMemoryBytes / 1073741824.0:N1} GiB ({report.PhysicalMemoryDetectionSource})"
            : "N/A";
        table.AddRow("Installed physical RAM", Markup.Escape(installedRam), Pass(report.MeetsPhysicalMemoryRequirement));
        table.AddRow("OS-visible RAM", report.OsVisibleMemoryBytes > 0 ? $"{report.OsVisibleMemoryBytes / 1073741824.0:N1} GiB" : "N/A", "[grey]informational[/]");
        table.AddRow("Largest Vulkan local heap", report.LargestDeviceLocalHeapBytes is ulong bytes ? $"{bytes / 1073741824.0:N1} GiB" : "N/A", Pass(report.MeetsGpuHeapRequirement));
        table.AddRow("Vulkan device", Markup.Escape(string.IsNullOrWhiteSpace(report.VulkanDeviceName) ? "N/A" : report.VulkanDeviceName), report.MeetsGpuHeapRequirement ? "[green]usable[/]" : "[red]not ready[/]");
        table.AddRow("AMD driver", Markup.Escape(string.IsNullOrWhiteSpace(report.GpuDriverVersion) ? "N/A" : report.GpuDriverVersion), "[grey]reported[/]");
        AnsiConsole.Write(table);
        foreach (string warning in report.Warnings) AnsiConsole.MarkupLine($"[yellow]Warning:[/] {Markup.Escape(warning)}");
        foreach (string error in report.Errors) AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(error)}");
    }

    public static string FormatBytes(long? bytes) => bytes is long value ? FormatBytes((ulong)Math.Max(0, value)) : "N/A";
    public static string FormatBytes(ulong bytes)
    {
        string[] suffixes = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes; int index = 0;
        while (value >= 1024 && index < suffixes.Length - 1) { value /= 1024; index++; }
        return $"{value:N1} {suffixes[index]}";
    }
    private static string FormatRate(double? value) => value is double rate ? $"{rate:N2} tokens/s" : "N/A";
    private static string Pass(bool value) => value ? "[green]pass[/]" : "[red]fail[/]";
}
