# Settings and console implementation matrix

This document covers the independently implemented settings/presentation module. The integration coordinator owns `AppConfig`, `MainForm`, agent transport and administrative operations. A validated preference is not a grant of authority. An exposed field must be wired to its operational hook before a release is described as complete; the parent feature matrix tracks integration evidence.

## Integration contract

Add an additive `AppConfig.Settings` property of type `ClassMonitor.Shared.Settings.SchoolSettings`; retain legacy JSON property names. Validate settings before accepting/loading them. `SettingsCenterForm(current, persistAndApply, openEnrollment?, openUpdates?, openDiagnostics?)` edits a clone; the callback must atomically save and apply the draft, or throw and preserve the old state. Import/export uses standalone schema-versioned preference JSON and never carries enrollment, identity keys or class secrets.

Call `TeacherExperience.Apply(form, settings)` when each supported form opens and after preferences change. This applies direction/theme/static catalog strings and original vector-derived icons. It never opens a form or initiates monitoring. `DashboardFilterBar` exposes `FilterChanged`, `SearchText`, `PresenceFilter`, `SetResultCount` and `ApplyPreferences`; the owner filters device tiles using `DashboardQuery.Matches` and enrolled/health state.

## Validated fields and operational hooks

| Group/fields | Defaults and validation | Required operational application | Module evidence |
|---|---|---|---|
| General.Language | `en`; `en/ar/he` | Apply localizer and form direction; Arabic/Hebrew RTL, English LTR | Headless form construction/direction; every catalog key translated; roundtrip; switch to English |
| General.Theme | `system`; system/light/dark | Apply palette/renderers to forms and menus; honor Windows high contrast | Contrast checks for light/dark palettes; no window displayed |
| General.NotificationsEnabled | true | Gate optional tray/desktop notifications; never disable mandatory session/control/recording visibility | Persistence tested; owner must wire notification path |
| Network.AgentPort / DiscoveryPort | 5900/5901; 1024–65535; distinct | Bind listener, advertise correct port, connect from paired device record | Validation/persistence tested; owner applies socket changes on controlled restart |
| Network.DiscoveryEnabled / LocalInterfaceAddress | true / empty; empty or IPv4 | Start/stop discovery and bind selected local adapter, retaining manual enrolled connections | Address validation tested; owner must validate adapter is present |
| Network.ConnectTimeoutSeconds / HandshakeTimeoutSeconds | 10/10; 2–60 | Linked cancellation/deadlines for connection and TLS/authentication | Bounds/persistence tested; parent network tests cover actual timeout |
| Network.HealthTimeoutSeconds | 15; 5–300 | Compute stale health/presence from last local receive time | Bounds/persistence tested; owner supplies device state |
| Display.ThumbnailWidth / ThumbnailHeight | 280/205; 160–640 / 120–480 | Resize grid tiles with DPI scaling | Bounds/persistence tested; visible layout/Narrator review remains required |
| Display.MaxFrameWidth / Quality / FramesPerSecond | 640/50/1; 320–1920 / 10–90 / 0.2–10 | Send bounded ScreenRequest; `IntervalMs` is 100–5000 | Valid profile cadence/monitor tested |
| Display.BandwidthMbps / AdaptiveQuality | 8/true; 0.25–100 | `AdaptiveStreamProfile.ObserveFrame/CreateRequest` estimates demand; `BandwidthLimiter.WaitAsync` must gate actual bytes sent | Congestion adaptation, actual wait and cancellation tested; sender integration required |
| Files.ReceiveDirectory / CollectDirectory | Desktop/ClassMonitor; LocalAppData/ClassMonitor/Collected | Use approved roots; `ApprovedFilePaths.ResolveFileName/ResolveRelative` before access | Traversal, alternate streams, rooted paths, reserved Windows names tested |
| Files.MaximumTransferMiB | 4; 1–8 | Enforce before encoding/allocating/saving using `CheckLength`; agent policy may be stricter | Exact limit, over-limit and negative size tested |
| Recording.AllowRecording | false | Require both this opt-in and an enrolled classroom recording grant; explicit action and visible indicator mandatory | Persistence tested; preference cannot grant authorization |
| Recording.Directory / RetentionDays / MaximumStorageMiB | LocalAppData/ClassMonitor/Recordings; 7; 2048; 1–365 days / 64–102400 MiB | Root records `classmonitor-*.avi`, stops at quota, invokes owned retention only under policy | Expired owned top-level files pruned; current/unrelated recordings preserved; owned storage counted |
| Layout.RememberWindowBounds / WindowWidth / WindowHeight / SidebarWidth | true / 1180 / 760 / 220; width 900–7680, height 600–4320, sidebar 160–480 | Apply/capture layout, clamp restoration to actual screen working area | Validation/persistence tested; parent captures bounds on save/exit |
| Layout.DeviceSort / DeviceOrder | name / empty; name/presence, at most 500 distinct IDs | Sort/reorder actual enrolled tiles and persist drag ordering | Duplicate/invalid order rejected; cloned order detached |
| Policies.ConfirmBeforeFocus / MaximumFocusMinutes | true / 60; 1–240 | Confirm optional focus request; enforce expiry no later than authorized lesson/policy | Bounds/persistence tested; agent remains final authority |
| Policies.RestoreOnSessionEnd | true, cannot be disabled | Always restore session-owned restrictions | False rejected; no disabling control exposed |
| Security / Updates / Diagnostics sections | Only added when owner provides a real action callback | Open actual enrollment/update/diagnostic workflow | No inert placeholder sections; callbacks survive preference import/reset |

Every field is serialized, cloned and validated. Core setting groups roundtrip through atomic standalone profile persistence with a `.previous` backup. Corrupt, oversized, missing-schema and duplicate-property JSON is rejected without overwriting it. Settings import does not overwrite legacy `AppConfig` properties or enrollment.

## Scope and release gates

- Legacy workflows not yet present in `UiText` remain English. This is an incremental trilingual foundation, not a claim of complete translation of every existing dialog, dynamic status message or error. Arabic/Hebrew linguistic review, Narrator checks, keyboard navigation on physical Windows and high-DPI visual review remain release gates.
- The catalog translates static controls and owner-draws legacy quality/size combo choices without changing their selected indices or firing behavior-change handlers. Original command glyphs are generated by `CommandIcons` in code; no reference-product artwork is used.
- Theming preserves student tile selection/status colors and screen imagery. Mandatory privacy notices cannot be disabled by the general notification preference.
- File helpers reject existing junction/symlink components; this does not remove filesystem time-of-check/time-of-use races. Managed agent directories and enrollment need administrator-controlled permissions. Do not infer a complete privileged-file security boundary from a string-path check.
- Retention owns only top-level `classmonitor-*.avi` files; it never retroactively erases unrelated or historical differently named recordings. The integrating recorder must use that naming convention and stop according to quota.
- No controls for school roles, arbitrary commands, activation systems, microphones/cameras or hidden-agent behavior are introduced here.

## Executed checks

Command from the isolated console worktree:

```powershell
& 'C:/Classroom Spy Professional12/.tools/dotnet/dotnet.exe' run --project tests/ConsoleChecks -c Release
```

The test executable targets .NET 10 Windows and can reference the baseline .NET 9 projects before parent migration. Tests construct settings forms without showing them; no screen fixture, deployment or real recording/file policy is run. The latest executed count belongs in the coordinator's PROGRESS report and test output; do not equate these component checks to full classroom integration.
