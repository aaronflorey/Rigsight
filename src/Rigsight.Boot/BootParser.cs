using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Processes;
using Rigsight.Core.Boot;

namespace Rigsight.Boot;

internal static class BootParser
{
    private static readonly Guid Shell = new("30336ed4-e327-447c-9de0-51b652c86108");
    public static void Parse(string directory)
    {
        string etl = Path.Combine(directory, "boot.etl");
        var capture = BootStore.Read<CaptureInfo>(Path.Combine(directory, "capture.json")) ?? throw new IOException("Missing capture metadata.");
        // Explicit toolkit path prevents a developer's ADK installation from hiding incomplete publishing.
        using var trace = TraceProcessor.Create(etl, new TraceProcessorSettings
        {
            ToolkitPath = Path.Combine(AppContext.BaseDirectory, "wpt"), AllowLostEvents = true,
            SuppressFirstTimeSetupMessage = true
        });
        var metadata = trace.UseMetadata();
        var processes = trace.UseProcesses();
        var cpu = trace.UseCpuSamplingData();
        var disk = trace.UseDiskIOData();
        var faults = trace.UseHardFaults();
        var memory = trace.UseMemoryUtilizationData();
        var events = trace.UseGenericEvents();
        var services = trace.UseServices();
        trace.Process();
        var meta = metadata;
        if (processes.Result.Processes.Count == 0) throw new InvalidDataException("No process events were decoded. Check the boot profile and WPR capture output in boot.log, then record another restart.");
        double Time(TraceTimestamp timestamp) => (timestamp.DateTimeOffset - meta.StartTime).TotalSeconds;
        var report = new BootReport { RecordedAt = meta.StartTime, DurationSeconds = (meta.StopTime - meta.StartTime).TotalSeconds, StopReason = capture.StopReason };
        if (cpu.Result.Samples.Count == 0) report.Limitations.Add("No CPU samples were decoded. CPU usage values are unavailable, not evidence of an idle system.");
        if (disk.Result.Activity.Count == 0) report.Limitations.Add("No disk I/O events were decoded. Disk usage values are unavailable, not evidence of no disk work.");
        if (meta.LostEventCount > 0 || meta.LostBufferCount > 0)
            report.Limitations.Add($"ETW lost {meta.LostEventCount} events and {meta.LostBufferCount} buffers. Measurements are incomplete.");
        var rows = new Dictionary<IProcess, BootProcess>(ReferenceEqualityComparer.Instance);
        var unknown = new BootProcess { Id = -1, Name = "Unattributed" };
        BootProcess Row(IProcess? process)
        {
            if (process is null) return unknown;
            if (!rows.TryGetValue(process, out var row))
            {
                row = new BootProcess { Id = process.Id, SessionId = process.SessionId, Name = process.ImageName ?? "Unknown", StartSeconds = process.CreateTime is { IsPartial: false } start ? Time(start) : null, EndSeconds = process.ExitTime is { IsPartial: false } end ? Time(end) : null };
                rows.Add(process, row);
            }
            return row;
        }
        foreach (var process in processes.Result.Processes) Row(process);
        var buckets = new SortedDictionary<int, BootActivity>();
        var bySecond = new Dictionary<(int Second, BootProcess Process), (double Cpu, long Disk)>();
        BootActivity Bucket(double second)
        {
            int key = Math.Max(0, (int)Math.Floor(second));
            if (!buckets.TryGetValue(key, out var bucket)) buckets[key] = bucket = new BootActivity { Second = key };
            return bucket;
        }
        foreach (var sample in cpu.Result.Samples)
        {
            if (sample.Process?.Id == 0) continue; // Idle samples are not CPU work.
            var row = Row(sample.Process);
            double seconds = (double)sample.Weight.TotalSeconds;
            row.CpuSeconds += seconds;
            var bucket = Bucket(Time(sample.Timestamp));
            bucket.CpuSeconds += seconds;
            var key = (bucket.Second, row);
            var prior = bySecond.GetValueOrDefault(key);
            bySecond[key] = (prior.Cpu + seconds, prior.Disk);
        }
        foreach (var io in disk.Result.Activity)
        {
            var row = Row(io.IssuingProcess);
            row.DiskBytes += io.Size.Bytes;
            var bucket = Bucket(Time(io.InitializeTime));
            bucket.DiskBytes += io.Size.Bytes;
            var key = (bucket.Second, row);
            var prior = bySecond.GetValueOrDefault(key);
            bySecond[key] = (prior.Cpu, prior.Disk + io.Size.Bytes);
        }
        foreach (var fault in faults.Result.Faults)
        {
            Row(fault.FaultingProcess).HardFaults++;
            Bucket(Time(fault.Timestamp)).HardFaults++;
        }
        foreach (var group in bySecond.GroupBy(x => x.Key.Second))
        {
            var topCpu = group.MaxBy(x => x.Value.Cpu);
            var topDisk = group.MaxBy(x => x.Value.Disk);
            buckets[group.Key].TopCpu = topCpu.Value.Cpu > 0 ? $"{topCpu.Key.Process.Name} ({topCpu.Key.Process.Id})" : "";
            buckets[group.Key].TopDisk = topDisk.Value.Disk > 0 ? $"{topDisk.Key.Process.Name} ({topDisk.Key.Process.Id})" : "";
        }
        report.Activity = buckets.Values.ToList();
        report.Processes = rows.Values.Append(unknown).Where(p => p.Id != -1 || p.CpuSeconds > 0 || p.DiskBytes > 0 || p.HardFaults > 0).OrderByDescending(p => p.CpuSeconds).ToList();
        foreach (var snapshot in memory.Result.Snapshots)
            report.Memory.Add(new BootMemory(Time(snapshot.Timestamp), snapshot.FreeListSize.Bytes + snapshot.ZeroListSize.Bytes + snapshot.StandbyListSize.Bytes, snapshot.CommitSize.Bytes));
        var markers = new List<BootMarker>();
        var eventKinds = new HashSet<string>();
        var starts = new Dictionary<(Guid Provider, int Task, Guid Activity, int Thread), (double Time, string Name)>();
        foreach (var e in events.Result.Events)
        {
            double time = Time(e.Timestamp);
            if (e.ProviderName is "Microsoft-Windows-Winlogon" or "Microsoft-Windows-Kernel-PnP")
                eventKinds.Add($"{e.ProviderName}/{e.Id} task={e.TaskName} opcode={e.Opcode} activity={e.ActivityName}");
            bool winlogon = e.ProviderName == "Microsoft-Windows-Winlogon";
            if (winlogon && e.TaskName == "AuthenticateUser" && e.Opcode == 1)
                markers.Add(new BootMarker("Authentication start", time, e.Process?.SessionId is > 0 ? e.Process.SessionId : e.SessionId, $"{e.ProviderName}/{e.Id} {e.TaskName} Start"));
            if (winlogon && e.TaskName == "AuthenticateUser" && e.Opcode == 2)
                markers.Add(new BootMarker("Authentication end", time, e.Process?.SessionId is > 0 ? e.Process.SessionId : e.SessionId, $"{e.ProviderName}/{e.Id} {e.TaskName} Stop"));
            if (e.ProviderId == Shell && e.Id == 27231)
                markers.Add(new BootMarker("Desktop ready", time, e.Process?.SessionId is > 0 ? e.Process.SessionId : e.SessionId, "Shell-Core/27231 ExplorerStartToDesktopReady"));
            if (e.ProviderName != "Microsoft-Windows-Kernel-PnP" || e.ActivityId == Guid.Empty) continue;
            string? name = e.Fields.Where(f => f.Name is "DeviceInstanceId" or "DriverName" or "DeviceName").Select(f => f.ToString()).FirstOrDefault();
            var key = (e.ProviderId, e.Task, e.ActivityId, 0);
            if (e.Opcode == 1 && name is not null) starts[key] = (time, name);
            if (e.Opcode == 2 && starts.Remove(key, out var begin) && time >= begin.Time)
                report.Initializations.Add(new BootInitialization(begin.Name, "Device/driver", begin.Time, time - begin.Time, $"Kernel-PnP {e.TaskName}, paired activity {e.ActivityId}"));
        }
        foreach (var service in services.Result.Services)
            foreach (var transition in service.Transitions)
            {
                if (transition.TransitionType != Microsoft.Windows.EventTracing.Services.ServiceTransitionType.Start || transition.StartEvent is null || transition.EndEvent is null) continue;
                double start = Time(transition.StartEvent.Timestamp), end = Time(transition.EndEvent.Timestamp);
                if (end >= start) report.Initializations.Add(new BootInitialization(service.Name, "Service", start, end - start, "Services start transition → completed state"));
            }
        report.Initializations = report.Initializations.OrderByDescending(i => i.DurationSeconds).ToList();
        BootTimeline.Populate(report, markers);
        if (report.Initializations.Count == 0) report.Limitations.Add("No complete named service/driver initialization intervals were decoded. Driver image loads alone do not measure initialization time.");
        if (report.Memory.Count == 0) report.Limitations.Add("No memory-list snapshots were decoded. Hard page faults are reported separately and do not alone prove memory pressure.");
        var state = BootStore.Read<BootState>(BootStore.StatePath);
        if (state is not null && File.Exists(state.SensorFile) && new FileInfo(state.SensorFile).Length < 1_000_000)
        {
            try
            {
                report.Sensors = (BootStore.Read<List<BootSensor>>(state.SensorFile) ?? []).Where(s => s.Time >= meta.StartTime && s.Time <= meta.StopTime).ToList();
            }
            catch (Exception ex) { BootStore.Log($"Sensors unavailable: {ex}"); }
        }
        if (report.Sensors.Count == 0) report.Limitations.Add("No Rigsight sensor readings were available during capture. Sensors begin when the existing agent can collect them; no earlier readings are inferred.");
        report.Findings = BootRules.Findings(report);
        BootStore.Write(Path.Combine(directory, "report.json"), report);
        BootStore.Write(Path.Combine(directory, "markers.json"), markers);
        BootStore.Write(Path.Combine(directory, "event-kinds.json"), eventKinds);
        BootStore.Log($"Trace processed: {report.Processes.Count} processes, {cpu.Result.Samples.Count} CPU samples, {disk.Result.Activity.Count} I/Os, {markers.Count} login markers; pre-login evidence {report.HasPreLoginEvents}");
    }
}
