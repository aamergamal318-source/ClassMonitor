# Changelog

All notable changes to this project will be documented in this file.

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
