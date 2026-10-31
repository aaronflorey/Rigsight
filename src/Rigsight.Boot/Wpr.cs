using System.Diagnostics;
using Rigsight.Core.Boot;

namespace Rigsight.Boot;

internal static class Wpr
{
    public const string Instance = "RigsightBoot";
    private static string Exe => Path.Combine(Environment.SystemDirectory, "wpr.exe");
    private static string Profile => Path.Combine(AppContext.BaseDirectory, "Boot.wprp");
    public static string Run(string exe, params string[] arguments)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException($"Could not start {exe}");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        int timeout = arguments.FirstOrDefault() == "--parse" ? 900_000 : 180_000;
        bool timedOut = !process.WaitForExit(timeout);
        if (timedOut)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
        string output = stdout.GetAwaiter().GetResult() + Environment.NewLine + stderr.GetAwaiter().GetResult();
        BootStore.Log($"{exe} {string.Join(' ', arguments)}\nExit {process.ExitCode}\n{output}");
        if (timedOut) throw new TimeoutException($"{exe} did not finish within {timeout / 60000} minutes. Open boot logs for command output.");
        if (process.ExitCode != 0) throw new CommandFailure(process.ExitCode, $"{Path.GetFileName(exe)} failed (exit {process.ExitCode}). Open boot logs for the command output. Install/update Windows Performance Toolkit if WPR reports unsupported options.");
        return output;
    }
    public static void CheckInstalled()
    {
        if (!File.Exists(Exe)) throw new IOException("Windows Performance Recorder is missing. Install Windows Performance Toolkit from the Windows ADK, then enable recording again.");
        string help = Run(Exe, "-help", "boottrace");
        string general = Run(Exe, "-help", "advanced");
        foreach (string option in new[] { "addboot", "stopboot", "cancelboot" })
            if (!help.Contains(option, StringComparison.OrdinalIgnoreCase)) throw new IOException($"Installed WPR does not advertise {option}. Install the current Windows Performance Toolkit.");
        if (!general.Contains("instancename", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Installed WPR does not advertise isolated instances. Update Windows Performance Toolkit before enabling recording.");
        Run(Exe, "-profiles");
        string profiles = Run(Exe, "-profiles", Profile);
        if (!profiles.Contains("RigsightBoot", StringComparison.OrdinalIgnoreCase)) throw new IOException("WPR could not load Boot.wprp. Reinstall Rigsight and open boot logs.");
        Run(Exe, "-profiledetails", Profile + "!RigsightBoot");
    }
    public static void Arm()
    {
        Directory.CreateDirectory(BootStore.Temporary);
        Run(Exe, "-boottrace", "-addboot", Profile + "!RigsightBoot", "-filemode", "-recordtempto", BootStore.Temporary, "-instancename", Instance);
    }
    public static void Stop(string etl) => Run(Exe, "-boottrace", "-stopboot", etl, "-instancename", Instance);
    public static void Cancel()
    {
        try { Run(Exe, "-boottrace", "-cancelboot", "-instancename", Instance); }
        catch (CommandFailure ex) when (ex.ExitCode == unchecked((int)0xc5583000)) { BootStore.Log("This instance has no remaining trace profiles."); }
    }
    private sealed class CommandFailure(int exitCode, string message) : IOException(message)
    {
        public int ExitCode { get; } = exitCode;
    }
}
