using System.Text.Json;

namespace Rigsight.Core.Boot;

// Machine-wide worker output: setup grants read access only to the enabling user and administrators.
public static class BootStore
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RigsightBoot");
    public static string Logs => Path.Combine(Root, "boot.log");
    public static string Captures => Path.Combine(Root, "Recordings");
    public static string Temporary => Path.Combine(Root, "Capture");
    public static string StatePath => Path.Combine(Root, "state.json");
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static void Write<T>(string path, T value)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
        File.Move(tmp, path, overwrite: true);
    }
    public static T? Read<T>(string path) => File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : default;
    public static void Log(string message) => Core.Log.WriteTo(Logs, "boot", message);
    public static long Size(string directory) => Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0;
    public static List<string> RecentDirectories() => !Directory.Exists(Captures) ? [] : Directory.GetDirectories(Captures).OrderDescending(StringComparer.Ordinal).ToList();
    public static void Retain(long reserve = 0) => RetainIn(Root, reserve);
    internal static void RetainIn(string root, long reserve)
    {
        string captures = Path.Combine(root, "Recordings");
        var directories = Directory.Exists(captures) ? Directory.GetDirectories(captures).OrderDescending(StringComparer.Ordinal).ToList() : [];
        long bytes = Size(root);
        while (directories.Count > 0 && (directories.Count > BootRules.MaximumRecordings || bytes + reserve > BootRules.StorageBudget))
        {
            string oldest = directories[^1];
            bytes -= Size(oldest);
            Directory.Delete(oldest, recursive: true);
            directories.RemoveAt(directories.Count - 1);
            Core.Log.WriteTo(Path.Combine(root, "boot.log"), "boot", $"Retention deleted {oldest}");
        }
        if (bytes + reserve > BootRules.StorageBudget) throw new IOException("Boot storage is full. Disable recording and open logs to review the capture folder.");
    }
}

public sealed class BootState
{
    public bool ShouldCapture(DateTimeOffset boot) => Enabled && PendingDirectory is null
        && Math.Abs((ArmedDuringBoot - boot).TotalSeconds) >= 10
        && (HandledBoot is null || Math.Abs((HandledBoot.Value - boot).TotalSeconds) >= 10);
    public bool Enabled { get; set; }
    public bool CleanupPending { get; set; }
    public DateTimeOffset ArmedDuringBoot { get; set; }
    public DateTimeOffset? HandledBoot { get; set; }
    public string Status { get; set; } = "Recording is off. Enable it, then restart normally.";
    public bool NeedsArm { get; set; }
    public bool NeedsStop { get; set; }
    public string? PendingDirectory { get; set; }
    public string SensorFile { get; set; } = "";
}
