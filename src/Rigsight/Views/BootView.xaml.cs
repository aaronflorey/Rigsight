using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Rigsight.Core;
using Rigsight.Core.Boot;

namespace Rigsight.Views;

public partial class BootView : UserControl
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private BootReport? report;
    private string[] directories = [];
    private bool busy;
    public BootView()
    {
        InitializeComponent();
        Loaded += (_, _) => { Refresh(); timer.Start(); };
        Unloaded += (_, _) => timer.Stop();
        timer.Tick += (_, _) => Refresh();
    }
    private void Refresh()
    {
        if (busy) return;
        try
        {
            var state = BootStore.Read<BootState>(BootStore.StatePath) ?? new();
            Recording.IsChecked = state.Enabled;
            Status.Text = state.Status;
            Cleanup.Visibility = state.CleanupPending ? Visibility.Visible : Visibility.Collapsed;
            var current = BootStore.RecentDirectories().ToArray();
            // A pending recording becomes selectable when parsing finishes, without changing its directory name.
            var labels = current.Select(d => Path.GetFileName(d) + (File.Exists(Path.Combine(d, "report.json")) ? " · report ready" : File.Exists(Path.Combine(d, "error.json")) ? " · processing failed" : " · saving / processing")).ToArray();
            if (Recent.ItemsSource is not string[] previous || !previous.SequenceEqual(labels))
            {
                int selected = Recent.SelectedIndex;
                directories = current;
                Recent.ItemsSource = labels;
                Recent.SelectedIndex = labels.Length == 0 ? -1 : Math.Clamp(selected, 0, labels.Length - 1);
            }
        }
        catch (Exception ex) { Fail("Cannot read boot recording status. Open logs or ask the administrator who enabled recording to grant this account access.", ex); }
    }
    private async void Recording_Click(object sender, RoutedEventArgs e) => await SetRecording(Recording.IsChecked == true);
    private async void Cleanup_Click(object sender, RoutedEventArgs e) => await SetRecording(false);
    private async Task SetRecording(bool enable)
    {
        if (busy) return;
        busy = true;
        Recording.IsEnabled = false;
        Status.Text = "Waiting for administrator approval and recording setup…";
        try
        {
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Rigsight.Boot.exe")) { UseShellExecute = true, Verb = "runas" };
            start.ArgumentList.Add(enable ? "--enable" : "--disable");
            if (enable) start.ArgumentList.Add(WindowsIdentity.GetCurrent().User!.Value);
            using var process = Process.Start(start) ?? throw new IOException("Setup did not start.");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new IOException($"Setup exited with code {process.ExitCode}. Review the setup explanation and open logs.");
        }
        catch (Exception ex) { Fail("Recording setup did not finish. Approve the Windows prompt, install Rigsight in Program Files, or open logs for the reported error.", ex); }
        finally { busy = false; Recording.IsEnabled = true; }
        // Keep a failed operation's explanation on screen until the next status refresh.
        try { Recording.IsChecked = BootStore.Read<BootState>(BootStore.StatePath)?.Enabled == true; } catch { Recording.IsChecked = false; }
    }
    private void Recent_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        report = null;
        Report.Visibility = Visibility.Collapsed;
        Export.IsEnabled = false;
        Empty.Visibility = Visibility.Visible;
        if (Recent.SelectedIndex < 0 || Recent.SelectedIndex >= directories.Length) return;
        try
        {
            report = BootStore.Read<BootReport>(Path.Combine(directories[Recent.SelectedIndex], "report.json"));
            if (report is null) { Empty.Text = "This recording has no report yet. If processing failed, open logs for the explanation and next action."; return; }
            Empty.Visibility = Visibility.Collapsed;
            Report.Visibility = Visibility.Visible;
            Report.Tag = Math.Max(1, report.DurationSeconds);
            Coverage.Text = $"{report.RecordedAt.ToLocalTime():f} · {report.DurationSeconds:F1} s recorded · Pre-login events {(report.HasPreLoginEvents ? "confirmed" : "not confirmed")}\n{report.StopReason}";
            Findings.ItemsSource = report.Findings;
            Phases.ItemsSource = report.Phases;
            Processes.ItemsSource = report.Processes;
            Activity.ItemsSource = report.Activity;
            Initializations.ItemsSource = report.Initializations;
            Limitations.ItemsSource = report.Limitations;
            CpuChart.SetReport(report);
            DiskChart.SetReport(report);
            Sensors.Text = report.Sensors.Count == 0 ? "Sensors unavailable during this recording." : $"{report.Sensors.Count} sensor samples, beginning {(report.Sensors[0].Time - report.RecordedAt).TotalSeconds:F1} s after trace start. " + string.Join(" · ", report.Sensors.SelectMany(s => s.Readings).GroupBy(x => x.Key).Select(g => $"{g.Key}: {g.Min(x => x.Value):F1}–{g.Max(x => x.Value):F1}")) + ". Temperatures °C; clocks MHz; RAM load %. Full samples are included in export.";
            Memory.Text = report.Memory.Count == 0 ? "Memory snapshots unavailable." : $"Lowest observed available memory: {report.Memory.Min(m => m.AvailableBytes) / 1048576.0:N0} MiB; peak committed memory: {report.Memory.Max(m => m.CommittedBytes) / 1048576.0:N0} MiB.";
            Export.IsEnabled = true;
        }
        catch (Exception ex) { Fail("Cannot open this report. Open logs and record another boot.", ex); }
    }
    private void Logs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = File.Exists(BootStore.Logs) ? BootStore.Logs : RigsightPaths.LogFile;
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "notepad.exe")) { UseShellExecute = true, ArgumentList = { path } })?.Dispose();
        }
        catch (Exception ex) { Fail("Could not open logs. Find boot.log in ProgramData\\RigsightBoot.", ex); }
    }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (report is null) return;
        var picker = new Microsoft.Win32.SaveFileDialog { Filter = "Boot report (*.html)|*.html", FileName = $"Rigsight-boot-{report.RecordedAt:yyyyMMdd-HHmmss}.html" };
        if (picker.ShowDialog() != true) return;
        try { File.WriteAllText(picker.FileName, BootExport.Html(report)); }
        catch (Exception ex) { Fail("Could not export. Choose a writable folder with available space.", ex); }
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();
    private void Fail(string explanation, Exception ex) { Status.Text = explanation; Log.Error("boot-ui", ex); }
}
