using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Rigsight.Core.Boot;

public static class BootExport
{
    public static string Html(BootReport report)
    {
        static string E(object? value) => WebUtility.HtmlEncode(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "Unavailable");
        var html = new StringBuilder("<!doctype html><html lang='en'><meta charset='utf-8'><meta name='viewport' content='width=device-width'><title>Rigsight boot report</title><style>body{font:15px Segoe UI,sans-serif;max-width:1200px;margin:32px auto;padding:0 20px;color:#20242b;background:#fafbfc}table{border-collapse:collapse;width:100%;margin:16px 0}td,th{padding:8px;text-align:left;border-bottom:1px solid #cbd1da}h2{margin-top:32px}svg{width:100%;height:160px;border:1px solid #cbd1da}pre{white-space:pre-wrap;overflow-wrap:anywhere}p,li{line-height:1.6}</style><h1>Boot diagnostics</h1>");
        html.Append($"<p>{E(report.RecordedAt)} · {report.DurationSeconds.ToString("F1", CultureInfo.InvariantCulture)} seconds recorded · Pre-login coverage {(report.HasPreLoginEvents ? "confirmed" : "not confirmed")}</p><p>{E(report.StopReason)}</p><h2>What to investigate</h2><ul>");
        foreach (string finding in report.Findings) html.Append($"<li>{E(finding)}</li>");
        html.Append("</ul><h2>Timeline · seconds from trace start</h2><table><tr><th>Phase<th>Start s<th>End s<th>Duration s<th>Evidence");
        foreach (var phase in report.Phases) html.Append($"<tr><td>{E(phase.Name)}<td>{E(phase.StartSeconds)}<td>{E(phase.EndSeconds)}<td>{E(phase.DurationSeconds)}<td>{E(phase.Evidence)}");
        html.Append("</table>");
        void Chart(string title, Func<BootActivity, double> value)
        {
            double max = Math.Max(1, report.Activity.Select(value).DefaultIfEmpty(0).Max());
            double duration = Math.Max(1, report.DurationSeconds);
            var points = new List<string>();
            var activity = report.Activity.ToDictionary(p => p.Second);
            for (int second = 0; second <= Math.Ceiling(duration); second++)
            {
                double v = activity.TryGetValue(second, out var sample) ? value(sample) : 0;
                points.Add(FormattableString.Invariant($"{Math.Min(second / duration, 1) * 1000:F2},{140 - v / max * 130:F2}"));
            }
            html.Append($"<h2>{E(title)}</h2><p>Scale 0–{E(max)}; time 0–{E(duration)} seconds. Exact values and leading processes appear below.</p><svg viewBox='0 0 1000 150' role='img' aria-label='{E(title)}'><polyline fill='none' stroke='#236bb4' stroke-width='2' points='{string.Join(' ', points)}'/></svg>");
        }
        Chart("CPU seconds per second across all cores (sampled)", p => p.CpuSeconds);
        Chart("Disk MiB per second, attributed at I/O initiation", p => p.DiskBytes / 1048576.0);
        html.Append("<h2>Processes</h2><p>Blank start/end: boundary not observed. CPU work does not equal time added to boot. Disk bytes include measured reads and writes.</p><table><tr><th>Process<th>PID<th>Start s<th>End s<th>CPU s<th>Disk bytes<th>Hard faults");
        foreach (var p in report.Processes) html.Append($"<tr><td>{E(p.Name)}<td>{p.Id}<td>{E(p.StartSeconds)}<td>{E(p.EndSeconds)}<td>{E(p.CpuSeconds)}<td>{p.DiskBytes}<td>{p.HardFaults}");
        html.Append("</table><h2>Activity by second</h2><table><tr><th>Second<th>CPU s<th>Top CPU<th>Disk bytes<th>Top disk<th>Hard faults");
        foreach (var p in report.Activity) html.Append($"<tr><td>{p.Second}<td>{E(p.CpuSeconds)}<td>{E(p.TopCpu)}<td>{p.DiskBytes}<td>{E(p.TopDisk)}<td>{p.HardFaults}");
        html.Append("</table><h2>Initialization intervals</h2><table><tr><th>Name<th>Kind<th>Start s<th>Duration s<th>Evidence");
        foreach (var p in report.Initializations) html.Append($"<tr><td>{E(p.Name)}<td>{E(p.Kind)}<td>{E(p.StartSeconds)}<td>{E(p.DurationSeconds)}<td>{E(p.Evidence)}");
        html.Append("</table><h2>Coverage and limits</h2><ul>");
        foreach (string limitation in report.Limitations) html.Append($"<li>{E(limitation)}</li>");
        // Escaped JSON makes all sensor, memory and process measurements portable without executable script.
        html.Append($"</ul><details><summary>All measured data (JSON, including sensors and memory)</summary><pre>{E(JsonSerializer.Serialize(report, BootStore.Json))}</pre></details></html>");
        return html.ToString();
    }
}
