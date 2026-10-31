using System.Xml.Linq;
using System.Xml.Schema;
using Rigsight.Core.Boot;

int checks = 0;
void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
Check(BootRules.StopReason(129, 10, 0, long.MaxValue) is null, "Must keep the full login + 120s window");
Check(BootRules.StopReason(130, 10, 0, long.MaxValue)?.Contains("120") == true, "Stop exactly at login + 120s");
Check(BootRules.StopReason(599, null, 0, long.MaxValue) is null, "No premature no-login cutoff");
Check(BootRules.StopReason(600, null, 0, long.MaxValue)?.Contains("ten minutes") == true, "Login screen must not prevent the boot cutoff");
Check(BootRules.StopReason(10, null, BootRules.CaptureLimit, long.MaxValue)?.Contains("size") == true, "Size must stop capture without needing a login");
Check(BootRules.StopReason(10, null, 0, 0)?.Contains("space") == true, "Low disk space must stop capture");
var setupBoot = DateTimeOffset.Parse("2026-10-01T10:00:00Z");
var lifecycle = new BootState { Enabled = true, ArmedDuringBoot = setupBoot };
Check(!lifecycle.ShouldCapture(setupBoot), "Arming must not treat the current login monitoring as an early-boot trace");
var firstBoot = setupBoot.AddDays(1);
Check(lifecycle.ShouldCapture(firstBoot), "The first boot after arming must be captured");
lifecycle.HandledBoot = firstBoot; lifecycle.NeedsArm = true;
Check(!lifecycle.ShouldCapture(firstBoot), "A worker retry must not duplicate the same boot");
lifecycle.ArmedDuringBoot = firstBoot; lifecycle.NeedsArm = false;
Check(lifecycle.ShouldCapture(firstBoot.AddDays(1)), "Explicit re-arming must allow a second boot without UI setup");
lifecycle.Enabled = false;
Check(!lifecycle.ShouldCapture(firstBoot.AddDays(1)), "Disabled recording must not capture on later worker starts");
var report = new BootReport { RecordedAt = DateTimeOffset.UtcNow, DurationSeconds = 200, Processes = [new() { Name = "smss.exe", Id = 200, StartSeconds = 0.2 }, new() { Name = "<script>alert(1)</script>", Id = 300, StartSeconds = 4, CpuSeconds = 5, DiskBytes = 100 * 1048576L, HardFaults = 1200 }] };
BootTimeline.Populate(report, [new("Authentication start", 10, 1, "start"), new("Authentication end", 80, 1, "end"), new("Desktop ready", 81, 2, "wrong session"), new("Desktop ready", 92, 1, "right session")]);
Check(report.Phases.Single(p => p.Name.Contains("after authentication")).DurationSeconds == 12, "Login processing must exclude credentials and other sessions");
Check(report.Phases.Single(p => p.Name.Contains("Credential")).DurationSeconds == 70, "Credential/authentication window must stay separate");
Check(report.HasPreLoginEvents, "Recorded early smss creation must confirm pre-login coverage");
var partial = new BootReport(); BootTimeline.Populate(partial, []);
Check(partial.Phases.Count == 0 && !partial.HasPreLoginEvents, "Missing events must not produce invented timings or pre-login claims");
Check(partial.Limitations.Count >= 2, "Missing boundaries must be explained");
report.Findings = BootRules.Findings(report);
Check(report.Findings.Any(f => f.Contains("not seconds added")), "CPU findings must not invent boot delay");
Check(report.Findings.Any(f => f.Contains("normal executable loading")), "Hard faults must not be called proof of low RAM");
string html = BootExport.Html(report);
Check(!html.Contains("<script>") && html.Contains("&lt;script&gt;"), "Exported process names must not execute HTML");
Check(html.Contains("right session") && html.Contains("104857600"), "Export must include evidence and measured data");
string root = Path.Combine(Path.GetTempPath(), "Rigsight-BootChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    for (int i = 0; i < 12; i++)
    {
        string directory = Path.Combine(root, "Recordings", i.ToString("D2")); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "report.json"), "{}");
    }
    BootStore.RetainIn(root, 0);
    Check(Directory.GetDirectories(Path.Combine(root, "Recordings")).Length == 10, "Retention must keep ten latest recordings");
    Check(!Directory.Exists(Path.Combine(root, "Recordings", "01")) && Directory.Exists(Path.Combine(root, "Recordings", "11")), "Retention must remove oldest first");
    using (var large = File.Create(Path.Combine(root, "Recordings", "02", "boot.etl"))) large.SetLength(BootRules.StorageBudget);
    BootStore.RetainIn(root, 0);
    Check(!Directory.Exists(Path.Combine(root, "Recordings", "02")), "Total byte budget must remove an oversized oldest recording");
    BootStore.RetainIn(root, BootRules.StorageBudget - 1024);
    Check(BootStore.Size(root) <= 1024, "Retention must honor reserved capture space");
    var original = new BootState { Enabled = true, NeedsArm = true, PendingDirectory = "pending" };
    string state = Path.Combine(root, "state.json"); BootStore.Write(state, original);
    Check(BootStore.Read<BootState>(state) is { Enabled: true, NeedsArm: true, PendingDirectory: "pending" }, "Pending work and re-arm state must survive a worker restart");
    original.Enabled = false; BootStore.Write(state, original);
    Check(BootStore.Read<BootState>(state)?.Enabled == false, "Disable must replace persisted enabled state");
}
finally { Directory.Delete(root, true); }
var profile = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Boot.wprp"));
Check(profile.Descendants("MaximumFileSize").All(e => e.Attribute("FileMode")?.Value == "Sequential" && (int)e.Attribute("Value")! > 0), "Every collector must have a finite sequential file cap, preserving early events");
Check(profile.Descendants("MaximumFileSize").Sum(e => (long)e.Attribute("Value")!) * 1048576 == BootRules.CaptureLimit, "Worker and native capture limits must agree");
Console.WriteLine($"Passed {checks} boot lifecycle, timeline, finding, export, retention and profile checks.");

if (args.Length == 1)
{
    var schemas = new System.Xml.Schema.XmlSchemaSet();
    schemas.Add(null, args[0]);
    profile.Validate(schemas, (_, error) => throw new Exception(error.Message));
    Console.WriteLine("Boot.wprp validates against the supplied Microsoft WPR schema.");
}
