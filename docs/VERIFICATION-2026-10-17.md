# 17 October 2026 — provider application launch links

- Popup quota-card logos and names now have separate keyboard-focusable buttons with hover/pressed/focus states, explicit target tooltips and accessible Invoke actions.
- Mapping: OpenAI logo → ChatGPT; Codex name → Codex; Gemini logo → Gemini; Gemini name → Antigravity; Claude logo/name → Claude.
- Installed targets were resolved without starting them: ChatGPT and Claude use their registered Windows AppsFolder identities; Codex uses the installed ChatGPT package's registered `codex://` protocol; Gemini and Antigravity use exact-name Start Menu Programs shortcuts. Codex CLI is not used. Resolution is on demand and adds no background polling or resident helper.
- Launch failures appear inline in the relevant provider card. Error references are cleared on snapshot rebuild/hide. Synthetic preview mode never launches applications.
- WPF regression passed: all six mappings, one request per click, accessible Invoke, target tooltips, per-card launch error display and refresh cleanup; existing taskbar spacing, popup anchoring/focus dismissal and token-history checks also passed.
- Synthetic quota screenshot inspected: brand header layout stays compact. Publish succeeded and local installation was updated. No GitHub push or release was requested in this pass.
- All five resolver targets were verified, but actual third-party applications were not opened during automated tests. This is target-resolution and UI-event verification, not end-to-end visual acceptance of each launched application.
- Final installed DLL matches publish output; supervisor Running. Native integration with the actually open panel passed for PID28364: indicator1743,1399–2093,1433; popup1743,839–2093,1399, gap0. The immediate startup probe ran before attachment; a later popup-closed assertion was inappropriate because the panel was open, so the final check used popup-open. Taskbar source is unchanged from the accepted version.
