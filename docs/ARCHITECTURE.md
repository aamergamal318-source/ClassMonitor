# Architecture

## Decisions before implementation
See ADR-001 (incremental desktop modernization), ADR-002 (enrollment and authorization), ADR-003 (streaming and performance). Retain the existing teacher, agent, shared protocol and updater projects. Upgrade to .NET 10 LTS using a workspace-local SDK when available; preserve the .NET 9 baseline in git. WinForms remains the desktop shell in this increment to preserve tested functionality. Modular settings/localization/theme services reduce migration cost; WPF is an evaluated future shell option, not a parallel rewrite.

## Components and ownership
- `ClassMonitor.Teacher`: classroom dashboard and authorized teacher workflows.
- `ClassMonitor.Agent`: interactive managed endpoint, capture, visible lesson/control/recording state, reversible OS operations.
- `ClassMonitor.Shared`: contracts, bounded framing, settings, identity/trust/grants, permission/session enforcement, audit and update transactions.
- `ClassMonitor.Desktop`: shared desktop update/settings/localization controls.
- `ClassMonitor.Updater`: isolated replacement/recovery worker.
- `installer/`: local, explicit, reversible packaging; no deployment during tests.
- `tests/`: headless unit/integration, authenticated loopback peers, failure/recovery and measured simulated load. Visual tests are gated.

## Data and protocol
Legacy `Password`, `Port`, `AgentVisible`, `ManualHosts` properties remain readable. New settings and enrollment are versioned additions. Preserving file format does not authorize legacy insecure authentication: unpaired/default-password endpoints require explicit migration. Trust is established out of band from administrator-reviewed public certificate fingerprints. Agent-side classroom grants are authoritative; UI filters are not an authorization boundary.

Mutual pinned TLS authenticates each endpoint. Handshakes and frames are bounded and timed; unauthenticated discovery cannot start monitoring. A valid classroom lesson context and operation permission are required before capture or commands. Stream requests have validated dimensions/cadence and bounded backpressure. Pending/stale/error states are visible to the teacher.

## Tool integration constraints
Claude Code absent, external Codex CLI unauthenticated, REA MCP absent/unsupported Node runtime. Native isolated agents can implement/review; these must not be reported as external Claude/Codex reviews. No signing credential or installer compiler has been detected yet. No actual school device deployment is authorized.
