# Engineering tasks

- [x] Read attached scope; preserve repository, history and MIT license.
- [x] Validate external agent capabilities and report limitations.
- [x] Research official product guides and evaluate runtime/UI/transport choices.
- [x] Record initial architecture and threat decisions before implementation.
- [x] Audit current features with source/test evidence; complete traceability matrix.
- [x] Install workspace-local supported .NET SDK and keep baseline build reproducible.
- [x] Implement explicit enrollment, mutual TLS pins, classroom grants and bounded authorized lessons.
- [x] Gate every capture/control/command operation; add visible session/control/recording state.
- [x] Integrate validated persistent settings, Arabic/Hebrew/English, theme and modern dashboard.
- [x] Verify classroom groups, health/presence, reconnection and bounded stream requests.
- [x] Verify messages, approved file paths, session focus/policies and safe cleanup.
- [x] Implement opt-in recording with retention controls and audit.
- [x] Execute headless unit, integration, authorization and recovery tests.
- [x] Make SecurityChecks report setup/check/background failures as exit codes without crash dialogs.
- [x] Apply the same crash-free reporting to ConsoleChecks, UpdateChecks, LoadProbe and ScreenStreaming.
- [x] Benchmark 10/20/30 simulated enrolled peers with measured resource/latency/throughput results.
- [x] Obtain independent review through actually available agents; disclose external tool limitations.
- [x] Create local teacher/agent installation packages and verify uninstall paths in isolated test directories.
- [x] Update progress, feature/settings matrix, deployment/privacy/security/contribution guidance.
- [ ] Obtain explicit approval before public release or real-device deployment.

## Single-file setup (`ClassMonitorSetup.exe`), requested 2026-11-01

Goal: one exe installs Teacher or Student on a device; later updates come from inside the application, not from setup. Start these when the owner asks; do all of them together.

- [x] Add Start menu and desktop shortcuts plus an optional "launch after install" choice to setup; remove them on uninstall and roll them back on failure.
- [x] Support devices with several Windows accounts: each account installs and enrolls separately, with no administrator rights for install. An all-users install was not built, because identities are per-profile and the in-app updater needs a writable folder; the owner can overrule this.
- [x] Add a visible, opt-in student setup path for automatic start and firewall access, and a clear next step for enrollment. Keep the rule that setup never enables these silently and never starts monitoring.
- [x] Make `scripts/Publish-Release.ps1` build setup after the ZIP, verify its payload and attach both to the release. The ZIP must stay attached: in-app update downloads it.
- [x] Reduce the 410 MB per-device install size, mainly the 229 MB setup copy kept for uninstall.
- [x] Keep the installed version in Apps correct after an in-app update (receipt and `DisplayVersion`); first confirm the suspected stale value with a test.
- [x] Password when installing a student computer; the teacher console shows only computers added with their address and password.
- [ ] Finish real Windows testing. Done on one computer: Teacher setup UI, install and uninstall, real keys over mutual TLS, real GitHub download and hash, the real agent added with a password and streaming during a lesson over loopback. Still open: the same between two computers, the new student setup window and firewall prompt, administrator enrollment import, restriction cleanup, and an update to a newer stable release.
- [ ] Publish a stable release so in-app update works; it only reads the latest stable public release. Version 1.2.0, changelog and release script are prepared. **Publishing needs the owner's explicit approval at that moment, because it reaches every installed copy.**
