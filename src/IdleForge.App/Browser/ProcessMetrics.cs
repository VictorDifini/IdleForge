using Microsoft.Web.WebView2.Core;
using System.Diagnostics;

namespace IdleForge.App.Browser;

public sealed record ProcessMetricsSnapshot(
    int ProcessCount,
    double CpuPercent,
    double PrivateMemoryMb,
    double WorkingSetMb,
    IReadOnlyDictionary<string, int> ProcessesByKind,
    TimeSpan SampleDuration);

public static class ProcessMetricsCollector
{
    public static async Task<ProcessMetricsSnapshot> CaptureAsync(CoreWebView2Environment environment, TimeSpan duration, bool includeHostProcess = true)
    {
        var webViewProcesses = environment.GetProcessInfos();
        var kinds = webViewProcesses.GroupBy(x => x.Kind.ToString()).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        var ids = webViewProcesses.Select(x => (int)x.ProcessId).ToHashSet();
        if (includeHostProcess) ids.Add(Environment.ProcessId);

        var before = Read(ids); var stopwatch = Stopwatch.StartNew(); await Task.Delay(duration); stopwatch.Stop(); var after = Read(ids);
        var cpuSeconds = after.Where(x => before.ContainsKey(x.Key)).Sum(x => (x.Value.Cpu - before[x.Key].Cpu).TotalSeconds);
        var cpu = cpuSeconds / stopwatch.Elapsed.TotalSeconds / Environment.ProcessorCount * 100d;
        return new(after.Count, Math.Round(cpu, 2), Math.Round(after.Values.Sum(x => x.PrivateBytes) / 1024d / 1024d, 1),
            Math.Round(after.Values.Sum(x => x.WorkingSet) / 1024d / 1024d, 1), kinds, stopwatch.Elapsed);
    }

    private static Dictionary<int, Sample> Read(IEnumerable<int> ids)
    {
        var result = new Dictionary<int, Sample>();
        foreach (var id in ids)
        {
            try { using var process = Process.GetProcessById(id); result[id] = new(process.TotalProcessorTime, process.PrivateMemorySize64, process.WorkingSet64); }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
        return result;
    }

    private sealed record Sample(TimeSpan Cpu, long PrivateBytes, long WorkingSet);
}
