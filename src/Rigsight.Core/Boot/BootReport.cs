namespace Rigsight.Core.Boot;

public sealed class BootReport
{
    public DateTimeOffset RecordedAt { get; set; }
    public double DurationSeconds { get; set; }
    public bool HasPreLoginEvents { get; set; }
    public string StopReason { get; set; } = "";
    public List<string> Findings { get; set; } = [];
    public List<BootPhase> Phases { get; set; } = [];
    public List<BootProcess> Processes { get; set; } = [];
    public List<BootActivity> Activity { get; set; } = [];
    public List<BootInitialization> Initializations { get; set; } = [];
    public List<BootMemory> Memory { get; set; } = [];
    public List<BootSensor> Sensors { get; set; } = [];
    public List<string> Limitations { get; set; } = [];
    public override string ToString() => RecordedAt.ToLocalTime().ToString("g");
}
public sealed record BootPhase(string Name, double StartSeconds, double EndSeconds, string Evidence)
{
    public double DurationSeconds => EndSeconds - StartSeconds;
}
public sealed class BootProcess
{
    public int Id { get; set; }
    public int SessionId { get; set; }
    public string Name { get; set; } = "";
    public double? StartSeconds { get; set; }
    public double? EndSeconds { get; set; }
    public double CpuSeconds { get; set; }
    public long DiskBytes { get; set; }
    public int HardFaults { get; set; }
}
public sealed class BootActivity
{
    public int Second { get; set; }
    public double CpuSeconds { get; set; }
    public long DiskBytes { get; set; }
    public int HardFaults { get; set; }
    public string TopCpu { get; set; } = "";
    public string TopDisk { get; set; } = "";
}
public sealed record BootInitialization(string Name, string Kind, double StartSeconds, double DurationSeconds, string Evidence);
public sealed record BootSensor(DateTimeOffset Time, Dictionary<string, double> Readings);

public static class BootRules
{
    public const int AfterLoginSeconds = 120, MaximumBootSeconds = 600, MaximumRecordings = 10;
    public const long StorageBudget = 8L * 1024 * 1024 * 1024;
    public const long CaptureLimit = 640L * 1024 * 1024;
    public static string? StopReason(double uptime, double? loginUptime, long bytes, long freeBytes)
    {
        if (bytes >= CaptureLimit) return "Recording reached its size limit; the beginning is preserved.";
        if (freeBytes < 2L * CaptureLimit) return "Recording stopped to preserve free disk space.";
        if (uptime >= MaximumBootSeconds) return "Recording reached ten minutes from boot.";
        if (loginUptime is { } login && uptime >= login + AfterLoginSeconds) return "Recorded 120 seconds after interactive login.";
        return null;
    }
    public static List<string> Findings(BootReport report)
    {
        var result = new List<string>();
        if (report.Processes.MaxBy(p => p.CpuSeconds) is { CpuSeconds: >= 2 } cpu)
            result.Add($"Investigate {cpu.Name} (PID {cpu.Id}): {cpu.CpuSeconds:F1} sampled CPU seconds across all cores during this recording. This measures activity, not seconds added to boot.");
        if (report.Processes.MaxBy(p => p.DiskBytes) is { DiskBytes: >= 50 * 1024 * 1024 } disk)
            result.Add($"Investigate {disk.Name} (PID {disk.Id}): {disk.DiskBytes / 1048576.0:F1} MiB of measured disk I/O. Check whether its startup work is needed.");
        if (report.Processes.MaxBy(p => p.HardFaults) is { HardFaults: >= 1000 } faults)
            result.Add($"{faults.Name} had {faults.HardFaults:N0} hard page faults. These require disk reads but can be normal executable loading; they alone do not establish low RAM.");
        if (report.Memory.MinBy(m => m.AvailableBytes) is { AvailableBytes: < 256 * 1024 * 1024 } low && report.Processes.Sum(p => p.HardFaults) >= 1000)
            result.Add($"Available memory reached {low.AvailableBytes / 1048576.0:F0} MiB at {low.Second:F1} s, with substantial hard-fault activity in the recording. Investigate memory pressure; the trace does not prove that all faults or boot delay came from low RAM.");
        foreach (var init in report.Initializations.Where(i => i.DurationSeconds >= 2).OrderByDescending(i => i.DurationSeconds).Take(3))
            result.Add($"{init.Kind} {init.Name} took {init.DurationSeconds:F1} s in its recorded initialization interval. Investigate its logs; intervals may overlap.");
        if (result.Count == 0) result.Add("No activity crossed the local investigation thresholds. Review the timeline and process table; this does not prove the boot was fast.");
        return result;
    }
}

public sealed record BootMemory(double Second, long AvailableBytes, long CommittedBytes);
public sealed record BootMarker(string Name, double Second, int? Session, string Evidence);

public static class BootTimeline
{
    public static void Populate(BootReport report, List<BootMarker> markers)
    {
        var auth = markers.Where(m => m.Name == "Authentication start" && m.Session > 0).OrderBy(m => m.Second).FirstOrDefault();
        var authenticated = markers.Where(m => m.Name == "Authentication end" && auth is not null && m.Session == auth.Session && m.Second >= auth.Second).OrderBy(m => m.Second).FirstOrDefault();
        var ready = markers.Where(m => m.Name == "Desktop ready" && authenticated is not null && m.Session == authenticated.Session && m.Second >= authenticated.Second).OrderBy(m => m.Second).FirstOrDefault();
        if (auth is not null)
        {
            report.HasPreLoginEvents = report.Processes.Any(p => p.StartSeconds >= 0 && p.StartSeconds < auth.Second && p.Name.Equals("smss.exe", StringComparison.OrdinalIgnoreCase));
            report.Phases.Add(new BootPhase("Windows before authentication", 0, auth.Second, "Trace start → " + auth.Evidence));
            if (authenticated is not null)
                report.Phases.Add(new BootPhase("Credential entry and authentication (includes waiting)", auth.Second, authenticated.Second, auth.Evidence + " → " + authenticated.Evidence));
            if (ready is not null && authenticated is not null)
                report.Phases.Add(new BootPhase("Windows login processing after authentication", authenticated.Second, ready.Second, authenticated.Evidence + " → " + ready.Evidence));
        }
        report.Limitations.Add("Human credential waiting cannot be isolated from the AuthenticateUser interval: it can include credential UI, user interaction and authentication work. It is kept separate from Windows processing after authentication, and is not attributed to a startup app.");
        if (auth is null || authenticated is null) report.Limitations.Add("No complete session-identified authentication interval was decoded. Credential entry and post-authentication timing are unavailable.");
        if (ready is null) report.Limitations.Add("No matching desktop-ready marker was decoded; login completion duration is unavailable.");
        if (!report.HasPreLoginEvents) report.Limitations.Add("Early pre-login coverage was not confirmed by a recorded smss process start before authentication. Do not treat this as a complete boot trace.");
    }
}
