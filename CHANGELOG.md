# Changelog

All notable changes to this project will be documented in this file.

## [1.2.0] - Unreleased

### Added
- One installer, `ClassMonitorSetup.exe`, for teacher and student computers, with Start menu and desktop shortcuts, an open-after-install choice, and opt-in sign-in start and firewall access.
- A computer password chosen when installing a student computer. The teacher console starts empty and adds each computer with its address and password; only a slow salted hash is stored and the password never crosses the network.
- Enrollment by file for power and program commands: the student exports a request, the teacher signs it, and an administrator imports it after comparing fingerprints.
- Bounded lessons started by the teacher for one classroom. Nothing is viewed or controlled outside a lesson, and every operation is checked on the student computer.
- Visible student state: a distinct tray icon during a lesson, and a banner the student cannot close while the screen is controlled, recorded or shown to the class.
- Questions to students, classroom scoping, device search and presence filters, saved layout, and Arabic, Hebrew and English interface text.
- Administrator tools in setup to import enrollment and remove restrictions left by a sudden interruption.

### Changed
- **Breaking:** the shared class password is gone. Run the new setup on each student computer and add it to the teacher console.
- Connections use mutual TLS pinned to both enrolled identities.
- Agent settings moved from `%ProgramData%\ClassMonitor` to `%LOCALAPPDATA%\ClassMonitor`. They hold preferences only and grant nothing.
- Sign-in start is now a per-account setting with no administrator rights. The earlier elevated scheduled task is no longer created.
- File transfer is limited to approved folders and a configurable size. Program launch is limited to Notepad, Calculator and Paint.
- Recordings and snapshots go to the recording folder, follow retention and storage limits, and show the student a notice.
- In-app updates keep the installed version shown in Windows Apps correct.
- Executables are compressed, and setup no longer leaves a copy of itself on each computer.

### Removed
- Hiding the student tray icon, through the legacy `AgentVisible` value.

### Status
- The reported black-screen issue on affected student computers is still unconfirmed.

## [1.1.0] - 2026-11-01

### Added
- Background stable-release checks on every teacher and agent startup, with manual recheck and update notifications.
- In-app package download, GitHub SHA-256 validation, exact component extraction, version validation and installation after explicit user action.
- Separate updater waits for the running app, preserves settings and a previous executable, restarts the updated app, and rolls back immediate installation/startup failures.
- Headless updater tests for metadata, integrity, offline behavior, staging, parent-process waiting, replacement and rollback.
- Repeatable release packaging script and Arabic development roadmap based on the reference product's public guide and current source.

### Changed
- Visual capture tests now require explicit `--visual`; normal startup opens no capture-test fixture.
- The reported black-screen issue on affected student machines remains under investigation.

## [1.0.1-diagnostics.1] - 2026-11-01

### Added
- User-invoked student screen preview and copyable diagnostic report (`--diagnose`).
- Teacher live-view diagnostic report with frame counts, JPEG decoding and image darkness.
- Repeatable desktop capture, TLS transport, real streaming-session and published-agent UI checks.

### Status
- Diagnostic prerelease; the reported black-screen issue is not yet reproduced on an affected student computer or confirmed fixed.
- See DIAGNOSTICS.md for collecting reports; no capture backend changes are included.

## [0.1.0] - 2026-11-01

### Added
- Initial open-source release of the ClassMonitor project.
- Teacher console and student agent structure.
- Shared protocol and Windows local-network monitoring features.
- Security documentation and responsible-use notes.
- CI workflow for building the solution on GitHub.

### Notes
- Project is intended for use on devices you administer and where users are aware of monitoring.
- See README for setup, architecture, build instructions, and legal/responsible use guidance.
