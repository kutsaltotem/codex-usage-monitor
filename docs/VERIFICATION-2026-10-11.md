# 11 October 2026 — visual and usability refinement

Local installation updated; source changes are not yet committed or published.

## Changes

- Consistent navy surfaces, segmented tabs, larger remaining percentages, balanced quota grid gutters and no trailing provider-card gap.
- Readable compact token KPIs with exact-value tooltips; model expander restyled to match the panel and remains lazy.
- Percentage axis on quota history, a real trailing 24-hour filter, one tooltip entry per provider/hour, and gaps rather than connecting missing measurements.
- Escape closes the panel; Ctrl+R refreshes. Refresh shows a busy state and result in its tooltip. Accessible action names and keyboard focus borders added.
- Shortcut/tray access falls back near the taskbar when the indicator has no safe slot. Panel height fits the current working area.
- Frozen color brushes reused instead of reparsing/reallocating each render. No memory reduction benchmark or guarantee.
- Offline synthetic interactive preview added to ProductScreenshots; it does not run provider refreshes. The normal application retains its focus-dismiss behavior.

## Verification

- WPF regression probe passed: pixel scroll, indicator sizing, compact tooltips, panel alignment, focus dismissal, Gemini/history persistence, empty Claude filter, lazy model expansion and opt-in publication.
- New checks passed: old/future quota samples excluded and Turkish compact token formatting.
- Seven layout planner scenarios passed, including tray expansion and density hysteresis.
- Installed native probe passed after installation: PID 36428, visible 350px child, no actual tray overlap. This is not an Explorer restart or multi-monitor test.
- Computer Use inspected the actual WPF preview window with synthetic data: tabs, scrolling, chart tooltip and model expansion. The embedded production window was not exposed as targetable by the helper; it was checked with the read-only native integration probe.
- Build/publish and git diff check passed. Synthetic screenshots refreshed under docs/images; no personal account screenshots published.

Real reboot/Explorer restart/multiple monitors and high-DPI visual acceptance remain unverified in this pass.

## Follow-up: simpler token recording controls

- Recording settings remain last and collapse to one row showing the enabled app count. Expanding reveals one labeled on/off switch per app; storage periods and the separate clear action are visible together.
- Expansion survives automatic settings publication. Failed saves retain an error message. No existing user choices or records were changed as part of the update.
- Removed the repeated token disclaimer; the explanation remains available in a tooltip. Preview setting changes now operate only on synthetic in-memory data.
- WPF regression passed, including Claude opt-in, preserved selection, and expanded settings after publication. Computer Use checked the synthetic preview's Claude Code switch, storage period change and collapse behavior.
- Published and installed locally. Native indicator probe passed for PID 29064 after startup settled (the immediate initial probe ran before the child existed). Source remains uncommitted/unpublished.
- Added reproducible synthetic screenshots: `docs/images/token-records.png` and `docs/images/token-record-settings.png`.


## Follow-up: taskbar visuals, logos and interaction

- Replaced the runtime JPEG/white-tile marks with shared frozen vector drawings. Original SVGs, pinned source attribution and the bundled MIT notice are retained under assets/vector. Gemini keeps its layered gradient; OpenAI and Claude use readable light/orange marks on dark surfaces. The same vector sources serve the popup.
- Transparent overall bar, balanced 22px logo frame and percentage pills, softer translucent provider surfaces, fixed350/104/34-DIP footprints, hover/pressed/focus feedback. Opening the panel changes the attached edge style without widening the bar.
- Real Button controls provide an accessible Invoke action and Enter/Space behavior. Ctrl+click requests refresh; concurrent manual refresh requests are ignored while a request is running, with a busy cursor/outline/tooltip. Capsule padding is inside the button; thin borders/gaps use an unhandled mouse-release fallback.
- Small state markers survive compact modes. Tooltip text includes cached/partial/error state, remaining percentage, reset and relative measurement age; no permanent 'current' claim. Removed the one-minute taskbar visual-tree rebuild; tooltip age is computed when opened. No RAM benchmark or reduction claim.
- Opening from the tray/shortcut honors the user's disabled taskbar-indicator preference.
- Expanded WPF probe passed: populated %100/%100 fit, centering, frozen vector sources, accessible Invoke, one Click event per toggle, fixed attached width, cached/partial compact marker/state/age, bounded compact loading, and existing popup/token regressions. Seven layout-planner scenarios passed.
- Computer Use synthetic preview confirmed bar click closes the panel and the next click reopens it in the same position before the final hit-area adjustment. In the final preview the helper returned 'point outside window bounds' / 'no monitor found for window'; final physical edge clicking is therefore not claimed as verified. The final WPF regression passed after that adjustment.
- Local publish succeeded; installed main assembly hash matches the publish output and the asset license is present. Final read-only native probe: PID45104, visible embedded350px indicator at1743,1399–2093,1433, no actual tray overlap, popup closed, startup task Running.
- Compact popup native assertion now allows a350-DIP readable panel right-aligned/clamped above a104/34-DIP bar. Real compact-mode popup, reboot, Explorer restart and multi-monitor/high-DPI acceptance remain untested.
- Synthetic normal/attached/warning/logo-only/minimal screenshots were refreshed. Source changes remain uncommitted and unpublished.


## Follow-up: missing taskbar indicator

- The user reported no visible indicator even on the primary monitor. Earlier WS_VISIBLE/parent/position checks did not establish that its pixels were presented. Subsequent read-only tests found no cloaking or occlusion at the three capsule centers at the sampled times; the paint failure itself was not captured.
- Added the missing Windows 10/11 compatibility manifest, preserving asInvoker. Microsoft requires modern Windows awareness for layered child HWNDs. The user confirmed the manifest alone did not solve visibility; an attachment-time repaint experiment also did not solve it and was removed.
- Replaced the layered WPF taskbar surface with a normal child HWND and native window regions covering only the rounded capsules. Gaps and outside corners expose Explorer directly; capsule backing is opaque navy. Attached capsule top edges become square. Region shapes are cached; transient/failed GDI handles are released, while Windows owns successfully installed regions. The popup retains its existing renderer.
- Stable-position anchoring now repairs child sibling order if Explorer places another child above it. This is resilience work; occlusion was not proven as the incident's cause.
- WPF probe passed, including native region centers/gaps/attached corners at 350/104/34 DIP and simulated 100/125/150/200% scales, plus the existing button/popup/token regressions. These are geometry checks, not real high-DPI monitor acceptance.
- Installed native probe passed for PID46164 at1743,1399–2093,1433: child parent matches Explorer, WS_EX_LAYERED=false, region kind3, all capsule centers inside and both gaps outside, hit tests correct, no actual tray overlap, popup closed. Installed EXE/DLL hashes match publish output; startup supervisor task Running. Synthetic PNG export now renders the capsule tree without its opaque HWND backing.
- The user confirmed that the indicator is still not visible on the primary monitor after the normal-HWND change. This incident remains unresolved; the next required evidence is a current taskbar screenshot, since Computer Use exposes no targetable taskbar window. Earlier manifest/repaint attempts are explicitly not recorded as successful fixes. No RAM reduction measurement, reboot/Explorer restart or multi-monitor acceptance is claimed. The indicator is still hosted on the primary taskbar only. Source remains uncommitted/unpublished.

References: https://learn.microsoft.com/en-us/windows/win32/winmsg/using-windows ; https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowrgn

## Recovery comparison: 11 October, continued

- Direct `HwndSource` hosting and a cached-bitmap GDI/WinForms presenter also passed native geometry/hit tests but were confirmed invisible by the user. These experiments were removed from production source; their code is retained locally outside the repository for diagnosis.
- Restoring the HEAD taskbar component while retaining the current popup and token UI brought the bar back. The user explicitly confirmed: "Evet, geri geldi". This isolates the regression to the changed taskbar component rather than establishing a general Explorer failure.
- Reapplying the complete new capsule/button/vector content on the restored WPF host made it disappear again; the user confirmed that result. The exact visual change responsible is not yet proven.
- Current comparison installation uses the old opaque capsule/row layout and original WPF layered-window attachment, with shared frozen brand drawings replacing only the JPEG marks and their white tile backing. Real visibility of this comparison is awaiting the user's response.
- Current regression checks cover the restored host, three provider quota pairs, single release dispatch, fixed350-DIP panel attachment and the existing popup/token behaviors. Removed normal-HWND region/GDI checks describe discarded implementations and are not final acceptance criteria.
- Installed assembly hash matches the publish output; startup supervisor is Running. Read-only native integration passed at1807,1399–2157,1433, with no actual tray overlap. This does not prove displayed pixels. No reboot, Explorer restart, real high-DPI/multi-monitor acceptance, memory improvement or GitHub publication is claimed.
## Confirmed visibility and quota spacing

- The user confirmed the comparison with the old opaque Border/StackPanel capsule structure and shared vector logos is visible, and supplied a taskbar screenshot. The earlier complete Button/capsule redesign remains reverted; the exact internal compositor failure is not identified.
- Preserved the working layered WPF attachment and Border/StackPanel structure. Changed only measurements:102-DIP row,21-DIP logo without inner padding,35-DIP quota pills,6-DIP logo-to-first-value gap,5-DIP inter-value gap. Removed the cramped separator. Normal provider width remains114 DIP and total bar width350 DIP.
- Quota labels use12-DIP tabular numerals. Missing-quota labels share the same first-value position; brands keep aligned leading positions even when one provider lacks readings.
- Regression passed for %0/%1/%9/%10/%99/%100: no text clipping, fixed value positions, exact gaps and no capsule overflow. Existing popup/token regression also passed. Final spacing is installed locally; physical visual acceptance of the new spacing remains separate from the user's preceding visibility confirmation.
- Final native check: shell-relative samples belong to the indicator, bounds1775,1399–2125,1433, but global WindowFromPoint samples hit an unrelated full-screen Chrome_RenderWidgetHostHWND with WS_EX_TRANSPARENT. The probe therefore fails its unobscured criterion; no final global hit-test/visual acceptance is claimed. Startup Running and installed/publish DLL hashes match.

- The user accepted the final spacing (süper bu halini koru) and explicitly requested GitHub publication. Final synthetic screenshots were regenerated from this accepted source; no account screenshots are included.
