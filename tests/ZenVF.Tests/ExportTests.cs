using ZenVF.Models;
using ZenVF.Services;

namespace ZenVF.Tests;

public class ExportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ZenVF.Tests", Guid.NewGuid().ToString("N"));

    public ExportTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void CsvAppendsClockLimitedAndVoltageSourceColumns()
    {
        string[] lines = ExportCsv();

        // New columns go at the end so existing column positions do not move.
        Assert.Contains(lines, l => l.StartsWith("# ccd,core_id,") && l.EndsWith(",eclk_actual_fmax_mhz,clock_limited"));
        Assert.Contains(lines, l => l.StartsWith("# requested_actual_fmax_mhz,") && l.EndsWith(",eclk_actual_fmax_mhz,clock_limited,voltage_source"));
        Assert.Contains(lines, l => l.Contains(",logical_cpu,") && l.EndsWith(",eclk_actual_fmax_mhz,voltage_source"));
    }

    [Fact]
    public void CsvMarksLimitedSteps()
    {
        string[] maxCoreRows = ExportCsv().Where(l => l.StartsWith("0,") && l.Split(',').Length == 14).ToArray();

        // Demo data marks steps >= 5800 MHz as clock limited.
        Assert.Contains(maxCoreRows, l => l.Split(',')[2] == "5900" && l.EndsWith(",1"));
        Assert.Contains(maxCoreRows, l => l.Split(',')[2] == "5700" && l.EndsWith(",0"));
    }

    [Fact]
    public void MarkdownUsesTheCorrectedLabelsAndNotes()
    {
        string path = Path.Combine(_dir, "report.md");
        ChartBuilder.ExportMarkdown(DemoData.CreateRunResult(), path, SingleCore());
        string text = File.ReadAllText(path);

        Assert.Contains("型号默认 SMU FMax 上限", text);
        Assert.DoesNotContain("原始 SMU FMax 上限", text);
        Assert.Contains("频率受限档位：5900、5800 MHz", text);
        Assert.Contains("电压来源：SMU PM table", text);
        Assert.Contains("电压来源：CPU VDDCR_VDD", text);
    }

    private string[] ExportCsv()
    {
        string path = Path.Combine(_dir, "data.csv");
        ChartBuilder.ExportCsv(DemoData.CreateRunResult(), path, SingleCore());
        return File.ReadAllLines(path);
    }

    private static SweepRunResult SingleCore() =>
        DemoData.CreateSingleCoreSweepResult(DemoData.CreateSettings(), DemoData.CreateTopology());
}
