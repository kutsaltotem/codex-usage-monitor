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
