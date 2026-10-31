using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Rigsight.Core;
using Rigsight.Core.Boot;

namespace Rigsight.Boot;

internal static class Setup
{
    internal const string TaskName = "Rigsight Boot Recording";
    public static void Set(bool enabled, string? ownerSid = null)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new UnauthorizedAccessException("Administrator approval is required to change boot recording.");
        if (enabled)
        {
            if (!MachineSetup.OnlyAdminsCanChange(AppContext.BaseDirectory)) throw new IOException("Install Rigsight in Program Files before enabling boot recording, so the SYSTEM worker cannot be replaced by an ordinary account.");
            var owner = new SecurityIdentifier(ownerSid ?? identity.User!.Value);
            
            var acl = new DirectorySecurity();
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            acl.SetOwner(admins);
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
                acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            var directory = new DirectoryInfo(BootStore.Root);
            if (directory.Exists) ValidateStorage();
            else directory.Create(acl);
            ValidateStorage();
            directory.SetAccessControl(acl);
        }
        using var control = Worker.Control();
        if (!enabled && !Directory.Exists(BootStore.Root)) return;
        ValidateStorage();
        if (!enabled && !File.Exists(BootStore.StatePath)) return;
        var state = BootStore.Read<BootState>(BootStore.StatePath) ?? new();
        if (enabled && state.Enabled) return;
        BootStore.Log(enabled ? "Setting up boot recording" : "Disabling boot recording");
        if (!enabled)
        {
            state.Enabled = false;
            state.CleanupPending = true;
            state.NeedsArm = false;
            state.NeedsStop = false;
            state.Status = "Recording is off. Previous reports are kept.";
            BootStore.Write(BootStore.StatePath, state);
            // Persist off before cancellation: a worker about to finish must never re-arm afterward.
            Exception? failure = null;
            try { Wpr.Cancel(); } catch (Exception ex) { failure = ex; }
            try { DeleteTask(); } catch (Exception ex) { failure ??= ex; }
            if (failure is not null)
            {
                state.Status = "Recording is off, but cleanup failed. Open logs and use Retry cleanup.";
                BootStore.Write(BootStore.StatePath, state);
                throw new IOException("Recording is marked off, but cleanup needs attention. Use Retry cleanup; open logs for details.", failure);
            }
            state.CleanupPending = false;
            BootStore.Write(BootStore.StatePath, state);
            return;
        }
        Wpr.CheckInstalled();
        Directory.CreateDirectory(BootStore.Captures);
        BootStore.Retain(2 * BootRules.CaptureLimit);
        state.Enabled = true;
        state.PendingDirectory = null;
        state.NeedsStop = false;
        state.CleanupPending = false;
        state.NeedsArm = true;
        state.ArmedDuringBoot = Worker.BootTime;
        state.Status = "Preparing recording…";
        using (var profile = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + ownerSid))
        {
            string home = Environment.ExpandEnvironmentVariables(profile?.GetValue("ProfileImagePath") as string ?? "");
            state.SensorFile = Path.Combine(home, "AppData", "Local", "Rigsight", "boot-sensors.json");
        }
        BootStore.Write(BootStore.StatePath, state);
        try
        {
            RegisterTask();
            Wpr.Arm();
            state.NeedsArm = false;
            state.Status = "Enabled — restart normally to record the next boot. The window can stay closed.";
            BootStore.Write(BootStore.StatePath, state);
        }
        catch
        {
            state.Enabled = false;
            state.Status = "Setup failed. Open logs, fix the reported problem, and enable recording again.";
            BootStore.Write(BootStore.StatePath, state);
            try { Wpr.Cancel(); } catch (Exception ex) { BootStore.Log(ex.ToString()); }
            try { DeleteTask(); } catch (Exception ex) { BootStore.Log(ex.ToString()); }
            throw;
        }
    }
    internal static void ValidateStorage()
    {
        var directory = new DirectoryInfo(BootStore.Root);
        if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Boot storage must not be a directory link. Ask an administrator to inspect " + BootStore.Root);
        var acl = directory.GetAccessControl();
        var owner = acl.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (!acl.AreAccessRulesProtected || owner is null || !(owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || owner.IsWellKnown(WellKnownSidType.LocalSystemSid)))
            throw new IOException("Boot storage is not protected. Ask an administrator to move the existing RigsightBoot folder aside, then enable recording again.");
    }
    private static void DeleteTask()
    {
        if (File.Exists(Path.Combine(Environment.SystemDirectory, "Tasks", TaskName)))
        {
            EndTask();
            Wpr.Run(RigsightPaths.TaskScheduler, "/Delete", "/TN", TaskName, "/F");
        }
    }
    private static void EndTask()
    {
        try { Wpr.Run(RigsightPaths.TaskScheduler, "/End", "/TN", TaskName); }
        catch (IOException ex) { BootStore.Log("Task may already be stopped: " + ex); }
    }
    // The installer must release mapped worker/parser files before replacing them.
    public static void PauseForUpdate(bool pause)
    {
        if (!Directory.Exists(BootStore.Root)) return;
        ValidateStorage();
        using var control = Worker.Control();
        if (BootStore.Read<BootState>(BootStore.StatePath)?.Enabled != true) return;
        Wpr.Run(RigsightPaths.TaskScheduler, "/Change", "/TN", TaskName, pause ? "/Disable" : "/Enable");
        if (pause) EndTask();
        else Wpr.Run(RigsightPaths.TaskScheduler, "/Run", "/TN", TaskName);
    }
    private static void RegisterTask()
    {
        string exe = SecurityElement.Escape(Environment.ProcessPath)!;
        string xml = $"""
            <?xml version="1.0" encoding="utf-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Finishes early-boot traces and builds local boot reports without the Rigsight window.</Description></RegistrationInfo>
              <Triggers><BootTrigger><Repetition><Interval>PT1M</Interval><StopAtDurationEnd>false</StopAtDurationEnd></Repetition><Enabled>true</Enabled></BootTrigger></Triggers>
              <Principals><Principal id="System"><UserId>S-1-5-18</UserId><LogonType>ServiceAccount</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><StartWhenAvailable>true</StartWhenAvailable><ExecutionTimeLimit>PT1H</ExecutionTimeLimit><RestartOnFailure><Interval>PT1M</Interval><Count>3</Count></RestartOnFailure></Settings>
              <Actions Context="System"><Exec><Command>{exe}</Command><Arguments>--worker</Arguments><WorkingDirectory>{SecurityElement.Escape(AppContext.BaseDirectory)}</WorkingDirectory></Exec></Actions>
            </Task>
            """;
        string path = Path.Combine(BootStore.Root, "task.xml");
        File.WriteAllText(path, xml, Encoding.Unicode);
        Wpr.Run(RigsightPaths.TaskScheduler, "/Create", "/TN", TaskName, "/XML", path, "/F");
    }
}
