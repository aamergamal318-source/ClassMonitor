# Engineering tasks

- [x] Read attached scope; preserve repository, history and MIT license.
- [x] Validate external agent capabilities and report limitations.
- [x] Research official product guides and evaluate runtime/UI/transport choices.
- [x] Record initial architecture and threat decisions before implementation.
- [ ] Audit current features with source/test evidence; complete traceability matrix.
- [ ] Install workspace-local supported .NET SDK and keep baseline build reproducible.
- [ ] Implement explicit enrollment, mutual TLS pins, classroom grants and bounded authorized lessons.
- [ ] Gate every capture/control/command operation; add visible session/control/recording state.
- [ ] Integrate validated persistent settings, Arabic/Hebrew/English, theme and modern dashboard.
- [ ] Verify classroom groups, health/presence, reconnection and bounded stream requests.
- [ ] Verify messages, approved file paths, session focus/policies and safe cleanup.
- [ ] Implement opt-in recording with retention controls and audit.
- [ ] Execute headless unit, integration, authorization and recovery tests.
- [x] Make SecurityChecks report setup/check/background failures as exit codes without crash dialogs.
- [ ] Apply the same crash-free reporting to ConsoleChecks, UpdateChecks, LoadProbe and ScreenStreaming.
- [ ] Benchmark 10/20/30 simulated enrolled peers with measured resource/latency/throughput results.
- [ ] Obtain independent review through actually available agents; disclose external tool limitations.
- [ ] Create local teacher/agent installation packages and verify uninstall paths in isolated test directories.
- [ ] Update progress, feature/settings matrix, deployment/privacy/security/contribution guidance.
- [ ] Obtain explicit approval before public release or real-device deployment.

## Single-file setup (`ClassMonitorSetup.exe`), requested 2026-11-01

Goal: one exe installs Teacher or Student on a device; later updates come from inside the application, not from setup. Start these when the owner asks; do all of them together.

- [ ] Add Start menu and desktop shortcuts plus an optional "launch after install" choice to setup; remove them on uninstall and roll them back on failure.
- [ ] Support devices with several Windows accounts; setup currently installs for the current account only. Decide the approach with the owner, since all-users setup needs elevation.
- [ ] Add a visible, opt-in student setup path for automatic start and firewall access, and a clear next step for enrollment. Keep the rule that setup never enables these silently and never starts monitoring.
- [ ] Make `scripts/Publish-Release.ps1` build setup after the ZIP, verify its payload and attach both to the release. The ZIP must stay attached: in-app update downloads it.
- [ ] Reduce the 410 MB per-device install size, mainly the 229 MB setup copy kept for uninstall.
- [ ] Keep the installed version in Apps correct after an in-app update (receipt and `DisplayVersion`); first confirm the suspected stale value with a test.
- [ ] Test on Windows for real: setup UI, install, uninstall, and in-app update download from a release. Not yet executed.
- [ ] Prepare a stable release so in-app update works; it only reads the latest stable public release. **Publishing needs the owner's explicit approval at that moment, because it reaches every installed copy.**
