# Windows verification — 6 October 2026

Release candidate:0.2.0, Windows 11 x64, .NET 10.0.401. This report contains no user account details, local home paths, credentials or production log exports.

## Verified

- Release/self-contained Windows x64 build:0 errors,0 warnings.
- Native taskbar probe: Explorer child window, WS_CHILD,350px width; connected popup350x560px, bottom edge at indicator top, gap0; no additional taskbar app button.
- Real WPF regression harness: physical pixel scrolling, hide/show history restoration, empty Claude filter followed by All restores Gemini records, isolated Claude opt-in publication preserves selection/view.
- Three quota line series and individual Claude filter; pinned token summary during scroll; lazy25-model dropdown with no12-model truncation; provider warning keeps reason/code and hides the global banner.
- All three taskbar groups are centered. Product screenshot exporter checks two 100% values fit into each fixed114 DIP group.
- Quota parser fixtures: zero/full/secondary Antigravity groups. Historical store reads both compact and legacy multiline JSON values without losing Gemini history.
- Installer syntax and isolated copy/hash checks: required app/icon/script files copied; test does not launch the app or change user shortcuts.
- Public screenshot exporter renders current WPF controls with synthetic snapshots; no session, real CLI scan or provider request occurs.
- Source and all existing Git commit blobs were scanned for common embedded token/private-key patterns; no matches found. Release package excludes personal histories, sessions, databases and logs.

## Live scope and limits

Codex and running Antigravity quota retrieval were validated locally. Antigravity Gemini and its secondaryClaude/GPT quota group remain distinct. A paid Claude Code account was not available for live validation; demoClaude values do not establish a working paid account connection.

The tested desktop has a standard bottom taskbar. Multiple DPI configurations, Explorer restarts, auto-hide and alternate taskbar orientations still need broader acceptance testing. Final user visual acceptance remains separate from automated geometry checks.

Software rendering and deferred visuals reduced short local memory observations to roughly 160–195 MiB working set. Runs were not controlled benchmarks, and software rendering shifts drawing work to CPU. No zero-resource claim is made.

## Reproduce

Run on Windows from the repository root with .NET 10 SDK:

```powershell
dotnet run --project tests/QuotaProbe/QuotaProbe.csproj -c Release
dotnet run --project tests/ScrollBehaviorProbe/ScrollBehaviorProbe.csproj -c Release
# Read-only native probe requires the installed app to be running:
dotnet run --project tests/TaskbarIntegrationProbe/TaskbarIntegrationProbe.csproj -c Release
# Export synthetic product screenshots:
dotnet run --project tools/ProductScreenshots/ProductScreenshots.csproj -c Release
.\scripts\publish-windows.ps1
```

The UI tests create their own offscreen WPF windows and isolated temporary data. They do not automate unrelated user applications.

## 7 October — startup and recovery fix (local, pending publication)

Startup was never enabled on the installed machine. Registered a per-user interactive logon task with10s delay, no runtime limit and no battery stop. Scheduler RestartOnFailure alone did not recover the forcibly terminated app in two attempts; an independent small .NET supervisor now blocks on the child process handle and waits1minute before restarting nonzero exits. A real forced termination recovered with a new app PID and taskbar indicator; supervisor short observed working set22MiB. Tray Exit returns0 and stops the supervisor by design; this branch was reviewed, not exercised through the tray UI. App startup no longer repeatedly replaces a running scheduler task, and background duplicate starts do not open the popup. Release builds and existing WPF regressions passed. Actual reboot/logon verification remains for the next user restart.

## 7 October: dynamic taskbar occupancy

Reference reviewed: CodeZeno/Claude-Code-Usage-Monitor commit623e8915a4ffc784c341aa2d6fdc71ac37f93e36. Independent C# implementation; no source copied. UI Automation samples visible taskbar controls off the UI thread every2seconds, independently of quota polling. Native TrayNotifyWnd and modern SystemTrayFrame supported. Unknown/stale samples fail closed after10seconds; no additional geometry subprocess per sample. Rightmost safe gap selected, with350/104/34DIP full/logos/AI density; no floating fallback. Popup remains350DIP readable width and aligns its bottom/right to the compact anchor. This does not reserve space in Explorer layout.

Five planner scenarios passed: expanding tray, crowded/logos, narrow/AI, completely occupied/no overlay,150%DPI. Existing ScrollBehaviorProbe passed. Installed native probe passed visible/embedded350px and actual tray non-overlap. Real Explorer restart, multiple displays and live compact transition remain untested. App working set observed around170MiB after start; no RAM reduction claim.

## 7 October: usability and layout stability follow-up

Added16DIP expansion clearance to prevent full/compact oscillation during tray animations; shrink remains immediate. Native tray location events request a fresh sample. UIA cache now queries only occupied control roles and tray frames, excluding text and irrelevant descendants. Unknown occupancy stays fail-closed. Compact logos/AI button expose provider summaries in tooltips. Indicator hiding clears stale pointer bounds and closes an attached popup; compact popup retains readable width. Native tray diagnostic now supports TrayNotifyWnd fallback.

Seven planner scenarios passed including expansion hysteresis, WPF tests passed including compact tooltips/anchor disappearance, build/publish and diff checks passed. Installed native probe: PID35428 visible/embedded350px, right2099 vs actual trayleft2105. Diagnostic returned[2105,1392,2560,1440]. Observed working sets app173MiB/supervisor23MiB; no measured memory reduction claim. Explorer restart/multiple monitor acceptance remains pending. Source remains local/uncommitted.

## 7 October: authorized GitHub verification

User explicitly requested verification and sending. Re-ran7planner scenarios, WPF regression tests and installed native integration probe: all passed (PID35428,350px,visible/embedded/no tray overlap). origin/main matched local HEAD before commit; no remote conflict. Added planner tests to Windows packaging CI. Actual reboot/Explorer restart/multiple displays remain outside this verification. Publicv0.2.0 release ZIP remains the earlier package; this update is source and CI-package publication.
