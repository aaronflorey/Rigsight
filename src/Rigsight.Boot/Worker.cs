using System.Diagnostics.Eventing.Reader;
using System.Runtime.InteropServices;
using Rigsight.Core.Boot;

namespace Rigsight.Boot;

internal static class Worker
{
    internal static DateTimeOffset BootTime
    {
        get
        {
            using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, "*[System[Provider[@Name='Microsoft-Windows-Kernel-General'] and EventID=12]]") { ReverseDirection = true });
            using var ev = reader.ReadEvent();
            return ev?.TimeCreated is { } time ? new DateTimeOffset(time.ToUniversalTime()) : DateTimeOffset.UtcNow.AddMilliseconds(-Environment.TickCount64);
        }
    }
    internal sealed class Gate : IDisposable
    {
        private readonly Mutex mutex = new(false, @"Global\Rigsight.Boot.Control");
        public Gate() { try { if (!mutex.WaitOne(TimeSpan.FromMinutes(4))) throw new TimeoutException("Boot recording is busy. Try again after capture completion."); } catch (AbandonedMutexException) { } }
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }
    internal static Gate Control() => new();
    private static BootState State() => BootStore.Read<BootState>(BootStore.StatePath) ?? new();
    public static void Run()
    {
        using var single = new Mutex(true, @"Global\Rigsight.Boot.Worker", out bool created);
        if (!created) return;
        Setup.ValidateStorage();
        DateTimeOffset boot = BootTime;
        BootStore.Log($"Worker starting; boot {boot:O}, uptime {Environment.TickCount64 / 1000.0:F1}s");
        var state = State();
        if (!state.Enabled) return;
        if (state.ShouldCapture(boot))
        {
            using (Control())
            {
                state = State();
                if (!state.Enabled) return;
                state.Status = "Recording this boot. Finishing 120 seconds after interactive login, or at the boot/size limit.";
                BootStore.Write(BootStore.StatePath, state);
            }
            string? reason;
            do
            {
                if (!State().Enabled) return;
                double? login = null;
                try { if (LoginTime() is { } loggedOn) login = Environment.TickCount64 / 1000.0 - (DateTimeOffset.UtcNow - loggedOn).TotalSeconds; }
                catch (Exception ex) { BootStore.Log("Login time unavailable; the boot limit still applies. " + ex); }
                reason = BootRules.StopReason(Environment.TickCount64 / 1000.0, login, BootStore.Size(BootStore.Temporary), FreeBytes());
                if (reason is null) Thread.Sleep(2000);
            } while (reason is null);
            using (Control())
            {
                state = State();
                if (!state.Enabled) return;
                BootStore.Retain(2 * BootRules.CaptureLimit);
                string folder = Path.Combine(BootStore.Captures, boot.ToUniversalTime().ToString("yyyyMMdd-HHmmss"));
                Directory.CreateDirectory(folder);
                state.PendingDirectory = folder;
                state.NeedsStop = true;
                state.HandledBoot = boot;
                state.NeedsArm = true;
                state.Status = "Saving the boot recording…";
                BootStore.Write(BootStore.StatePath, state);
                BootStore.Write(Path.Combine(folder, "capture.json"), new CaptureInfo(boot, reason));
                CompleteCapture(state, boot);
            }
        }
        using (Control())
        {
            state = State();
            if (state.Enabled && state.NeedsStop) CompleteCapture(state, boot);
            else if (state.Enabled && state.NeedsArm) Arm(state, boot);
        }
        state = State();
        if (state.PendingDirectory is { } pending)
        {
            string etl = Path.Combine(pending, "boot.etl");
            try
            {
                BootStore.Log($"Parsing {etl}");
                // A separate process contains native parser failures. A native crash becomes a logged processing failure.
                Wpr.Run(Environment.ProcessPath!, "--parse", pending);
                BootStore.Log($"Parsed report {pending}");
            }
            catch (Exception ex)
            {
                BootStore.Log(ex.ToString());
                BootStore.Write(Path.Combine(pending, "error.json"), new { Error = "This trace could not be processed. Open logs, fix the reported dependency or capture problem, then restart normally to record again." });
            }
            using (Control())
            {
                state = State();
                state.PendingDirectory = null;
                if (state.Enabled) state.Status = File.Exists(Path.Combine(pending, "report.json")) ? "Report ready. Recording is armed for the next boot." : "Trace processing failed. Open logs for details. The next boot is armed.";
                BootStore.Write(BootStore.StatePath, state);
                BootStore.Retain();
            }
        }
    }
    private static void CompleteCapture(BootState state, DateTimeOffset boot)
    {
        string folder = state.PendingDirectory ?? throw new IOException("No destination for the pending capture.");
        try
        {
            Wpr.Stop(Path.Combine(folder, "boot.etl"));
            state.NeedsStop = false;
            BootStore.Write(BootStore.StatePath, state);
            // These are only this instance's temporary files; its merged trace is now safely saved.
            foreach (string file in Directory.GetFiles(BootStore.Temporary)) File.Delete(file);
        }
        catch (Exception ex)
        {
            state.Status = "Could not save the boot trace. Open logs. The worker will retry; raw data is kept and will not be re-armed over.";
            BootStore.Write(BootStore.StatePath, state);
            BootStore.Log(ex.ToString());
            throw;
        }
        if (state.Enabled) Arm(state, boot);
    }
    private static void Arm(BootState state, DateTimeOffset boot)
    {
        try
        {
            BootStore.Retain(2 * BootRules.CaptureLimit);
            Wpr.Arm();
            state.NeedsArm = false;
            state.ArmedDuringBoot = boot;
            state.Status = "Recording is armed for the next boot; processing the saved trace…";
            BootStore.Write(BootStore.StatePath, state);
            BootStore.Log("Re-armed boot recording");
        }
        catch (Exception ex)
        {
            state.Status = "Could not re-arm recording. Open logs; the background task will retry.";
            BootStore.Write(BootStore.StatePath, state);
            BootStore.Log(ex.ToString());
            throw;
        }
    }
    private static long FreeBytes() => new DriveInfo(Path.GetPathRoot(BootStore.Root)!).AvailableFreeSpace;

    // WTS supplies the original interactive logon time, even if this worker starts late.
    private static DateTimeOffset? LoginTime()
    {
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var pointer, out int count)) throw new System.ComponentModel.Win32Exception();
        try
        {
            DateTimeOffset? first = null;
            for (int i = 0; i < count; i++)
            {
                var session = Marshal.PtrToStructure<Session>(pointer + i * Marshal.SizeOf<Session>());
                if (session.Id == 0) continue;
                if (!WTSQuerySessionInformation(IntPtr.Zero, session.Id, 24, out var buffer, out int bytes)) continue;
                try
                {
                    if (bytes < Marshal.SizeOf<SessionInfo>()) continue;
                    var info = Marshal.PtrToStructure<SessionInfo>(buffer);
                    if (string.IsNullOrEmpty(info.User) || info.LogonTime <= 0) continue;
                    var time = DateTimeOffset.FromFileTime(info.LogonTime);
                    if (first is null || time < first) first = time;
                }
                finally { WTSFreeMemory(buffer); }
            }
            return first;
        }
        finally { WTSFreeMemory(pointer); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Session { public int Id; public IntPtr Name; public int State; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct SessionInfo
    {
        public int State, SessionId, IncomingBytes, OutgoingBytes, IncomingFrames, OutgoingFrames, IncomingCompressedBytes, OutgoingCompressedBytes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Station;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)] public string Domain;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)] public string User;
        public long ConnectTime, DisconnectTime, LastInputTime, LogonTime, CurrentTime;
    }
    [DllImport("wtsapi32.dll", EntryPoint = "WTSEnumerateSessionsW", SetLastError = true)] private static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);
    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)] private static extern bool WTSQuerySessionInformation(IntPtr server, int session, int infoClass, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);
}
internal sealed record CaptureInfo(DateTimeOffset Boot, string StopReason);
