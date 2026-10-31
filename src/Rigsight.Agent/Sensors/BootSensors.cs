using Rigsight.Core;
using Rigsight.Core.Boot;

namespace Rigsight.Agent.Sensors;

// Reuse the agent's hardware access; a second sensor reader can contend with board controllers.
internal static class BootSensors
{
    private static readonly List<BootSensor> Samples = [];
    private static long next;
    public static void Record(SensorHost host)
    {
        long now = Environment.TickCount64;
        if (now < next || now > BootRules.MaximumBootSeconds * 1000L) return;
        next = now + 10_000;
        try
        {
            if (BootStore.Read<BootState>(BootStore.StatePath)?.Enabled != true) return;
            var readings = new Dictionary<string, double>();
            foreach (string key in new[] { KeySensors.CpuTemp, KeySensors.GpuTemp, KeySensors.CpuClock, KeySensors.GpuClock, KeySensors.RamLoad })
                if (host.Read(key) is { } value && double.IsFinite(value)) readings[key] = value;
            if (readings.Count == 0) return;
            Samples.Add(new BootSensor(DateTimeOffset.UtcNow, readings));
            BootStore.Write(Path.Combine(RigsightPaths.DataDir, "boot-sensors.json"), Samples);
        }
        catch (Exception ex) { Log.Error("boot-sensors", ex); next = now + 60_000; }
    }
}
