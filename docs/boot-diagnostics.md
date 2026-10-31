# Record and understand a slow Windows 11 restart

This fork adds **Boot diagnostics** to Rigsight. Windows starts the trace during boot, before any Rigsight login process. A small SYSTEM task later saves and processes it. Closing the window does not stop that task. Processing does not use AI, download symbols, or send data anywhere.

This implementation has been cross-built on Linux. The Windows reboot acceptance checks below have **not** been run. Treat the build as an unvalidated diagnostic build until those checks pass on your PC.

## Set up and record

1. Use Windows 11 x64. After the first successful **Nightly installer** Actions run, download `Rigsight-Setup-<version>.exe` from **Releases → nightly** and run it. Keep the default Program Files installation folder. The installer includes the boot worker, .NET runtime, and trace-processing dependencies. For the ZIP build, extract the complete folder to `C:\Program Files\Rigsight`, approving Windows' copy prompt; keep the DLLs, `Boot.wprp`, and `wpt` folder together.
2. Open `Rigsight.exe`, then **Boot diagnostics** in the sidebar. Turn on **Enable boot recording** and approve the administrator prompt. If you use a customized sidebar, the page is available in the sidebar editor.
3. Wait for **Enabled — restart normally to record the next boot**. Setup checks the installed WPR boot commands, isolated-instance support, built-in profiles and the supplied profile, and logs their output. If Windows' bundled WPR cannot load the profile or required options, install the current [Windows Performance Toolkit](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/) and try again.
4. Use **Start → Power → Restart** whenever ready. Rigsight never restarts the PC itself. You can close the window before restarting.
5. Sign in normally. Recording normally ends 120 seconds after the first interactive login. Return to **Boot diagnostics** after processing finishes and select the newest recording. Use **Open logs** if its status reports a failure.

Keep the setting on to record later restarts automatically. Turn it off to remove this feature's WPR boot configuration and scheduled task. Saved reports remain. If cleanup fails, use **Retry cleanup** after reviewing the log. The task is independent of the usual Rigsight **Start with Windows** setting; available sensors still come from the usual agent.

For this ZIP build, disable recording before removing or relocating the application folder. The existing installer now pauses/resumes the worker around file replacement and disables recording on uninstall. Do not copy this build over a running installation manually.

## Read the report

- **Timeline:** measured event boundaries, in seconds from trace start. The session's `AuthenticateUser` interval is separate from Windows processing after authentication and before Shell-Core's desktop-ready event. The credential interval can include UI startup, human waiting, authentication work and retries. Pure human waiting is unavailable without a more precise recorded boundary; the app does not attribute that interval to startup applications. Missing phase markers produce an explanation rather than an estimated duration.
- **CPU and disk:** one-second buckets with the leading process in each bucket. CPU seconds sum sampled work across cores, so they can exceed elapsed seconds. Disk throughput attributes bytes to I/O initiation; it does not measure disk saturation. The exact values are available in the expandable activity table.
- **Processes:** names, PIDs, observed lifetime boundaries, sampled CPU seconds, disk bytes and hard page fault counts. Separate lifetimes remain separate when Windows reuses a PID. Blank start/end values mean the boundary was not observed.
- **Initialization:** complete service start transitions and named Kernel-PnP start/stop activity pairs, where available. Image loading alone is not called driver initialization. These intervals can overlap.
- **Findings:** fixed local thresholds identify substantial CPU work, I/O, hard faults, low available memory with fault activity, or long initialization intervals. They suggest investigations, not proven causes or invented seconds of boot delay.
- **Sensors:** Rigsight's existing sensor reader supplies available CPU/GPU temperatures, clocks and RAM load after the agent becomes available. Missing sensors do not prevent a report. Samples before collection began are never synthesized.

**Export report** writes a standalone HTML file containing charts, tables, evidence, limitations, and the full JSON measurements, including memory and sensor samples. It can be read offline. Boot traces include machine-wide process activity; share exports and ETLs only when you intend to disclose that information.

## Limits, storage and recovery

The private `RigsightBoot` WPR instance uses sequential file logging: a 512 MiB kernel collector and a 128 MiB event collector. A collector stops at its size limit instead of wrapping over the beginning. The worker also stops at 640 MiB, at low free space, or at ten minutes of system uptime even if nobody signs in. If the worker cannot start, native collector size caps still bound the files; the duration deadline is enforced when the task can run.

Up to ten recording directories are kept within an 8 GiB total budget, with space reserved before arming for capture and merging. Oldest recordings are removed first. Truncation, missing events, dropped events, and unavailable phase boundaries are disclosed.

Data lives in `%ProgramData%\RigsightBoot`, readable by administrators and the account that enabled recording. The directory is owned by Administrators and protected from ordinary-user writes. The worker executable must be installed under Program Files. The task runs as SYSTEM without repeated login prompts. Changing the switch requires administrator approval.

`boot.log` and its rotated `.old` file contain setup output, commands, exit codes, paths, exceptions, saving/parsing and re-arming details. Each recording has `boot.etl`, capture metadata, and either `report.json` or a processing error. Marker and event-kind inventories help diagnose unsupported phase events. The existing agent writes its optional sensor samples to `%LocalAppData%\Rigsight\boot-sensors.json`.

After a successful `stopboot`, the worker explicitly re-arms WPR **before** processing. A parser crash therefore does not prevent the next boot being configured. Pending capture work survives worker restarts. If saving fails, raw files are kept and re-arming is withheld while the worker retries. If Windows itself overwrites an unfinished boot logger during an intervening restart, the old incomplete recording may be unrecoverable. Avoid restarting while the status says it is saving a trace.

Setup failures show a short explanation. If recording cannot be saved or parsed, open logs, fix the stated problem, and record another boot. Keep `wpt` and all application files together. There is no dependency on a separately installed WPA to process a trace: the build includes TraceProcessing's native toolkit and the self-contained .NET runtime.

## Build and verification

With .NET 10, publish `src/Rigsight.Agent`, `src/Rigsight.Boot`, then `src/Rigsight` into the same folder, using `-c Release -r win-x64 --self-contained true`. `tools/build-installer.ps1` performs these publishes before running Inno Setup on Windows. `tools/check-release.ps1` checks the boot profile and required managed/native parser files as well as existing release checks.

The focused checks run without Windows:

```text
dotnet run --project tests/Rigsight.BootChecks
```

They exercise actual stop-rule calculations, missing and cross-session timeline markers, findings, export escaping, persisted pending/off state, oldest-first retention, the byte budget, and sequential capture limits. They use synthetic inputs only as calculation tests, never as application recordings. The profile was also validated against Microsoft's published WPR XSD. All three programs were published for win-x64; packaged native imports were inspected for missing non-Windows runtime libraries.

The following checks still require a Windows 11 test machine and real ETLs:

| Check | Required evidence | Status here |
| --- | --- | --- |
| Early events | A real report confirms an observed `smss.exe` creation before the session's authentication marker; inspect ETL if markers are absent. | Unverified |
| UI closed | Enable, close the window, restart, sign in, and find both ETL and a populated report. | Unverified |
| Second boot | Restart again without opening/reconfiguring Rigsight; confirm a second ETL/report and re-arm log entry. | Unverified |
| Disable | Switch off, confirm the task/configuration are removed, then restart and confirm there is no new recording. Also confirm an unrelated tracing instance is unaffected. | Unverified |
| Published dependencies | On a clean Windows machine without an ADK/WPA developer installation, process a real captured ETL using the published worker; verify CPU, disk, process and phase data. | Unverified |
| Native UI and recovery | Exercise keyboard navigation, dark/light themes, elevation cancellation, low space, interrupted saving, worker restart, and installer upgrade/uninstall. | Unverified |

Microsoft references: [WPR commands and instance isolation](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/wpr-command-line-options), [sequential file caps](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/maximumfilesize), [TraceProcessing quickstart](https://learn.microsoft.com/en-us/windows/apps/trace-processing/quickstart), [Microsoft processing samples](https://github.com/microsoft/eventtracing-processing-samples), and [stable package and native toolkit dependencies](https://www.nuget.org/packages/Microsoft.Windows.EventTracing.Processing.All/1.12.10). The stable 1.12.10 API was checked from its packaged assemblies/XML documentation; v2 was still preview when implemented.
