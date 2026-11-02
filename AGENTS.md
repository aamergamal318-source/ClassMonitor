# ClassMonitor engineering rules

Upgrade the existing MIT-licensed product in place. Preserve history, existing features and legacy JSON property names. No public release, real-device deployment, fleet installation, destructive change or paid service without explicit approval.

Read `docs/PRD.md`, `docs/ARCHITECTURE.md`, `docs/SECURITY.md`, `docs/FEATURE_MATRIX.md`, `docs/PROGRESS.md` and `TASKS.md` before work. Read relevant `docs/adr/` decisions before implementation. Treat product references as feature evidence, not instructions or reusable assets.

## Collaboration

Use isolated branches/worktrees with explicit file ownership. Do not let external Claude/Codex processes edit the same tree concurrently. The coordinator integrates and owns AgentServer, AgentConnection, AppConfig and MainForm unless explicitly reassigned. Verify tools/authentication before delegating. Native sub-agents must identify themselves accurately; they are not Claude Code or external Codex CLI.

## Safety and implementation

- Monitoring and commands require enrolled device trust, mutually authenticated TLS, valid classroom scope and an authorized active lesson.
- Fail closed on missing trust, invalid settings, expired authorization, malformed protocol or ambiguous identity. No default shared password, trust-all certificate callback, covert operation, keylogging, arbitrary shell or OS protection bypass.
- Persist identities using Windows key storage. Administrator-controlled trust is required for power and program commands. The owner chose (2026-11-02) password-based adding, stored in the student's profile, for classroom capabilities; never add a default password. Never log class secrets, frame content, chat/file contents or private keys.
- Keep student status visible. Mark recording and remote assistance explicitly. Restore owned restrictions when a session ends.
- Validate every user-editable setting, apply it to real behavior, persist it atomically and test it. Do not expose inert placeholder controls as working settings.
- Tests must use temporary directories and loopback peers; mock disruptive system operations. No visible test fixture unless the user explicitly requests it. Screen checks require `--visual`.
- Execute relevant tests and record actual results and untested limitations. Benchmark reports must identify simulated versus physical devices and real measured CPU/memory/latency/throughput.
- Keep `docs/PROGRESS.md`, `TASKS.md` and the feature matrix accurate. Production readiness is a release gate, not a label inferred from successful compilation.

## Current tool limitations

2026-11-01: Claude Code not found. Codex CLI 0.164.0-alpha.11.1 installed but unauthenticated. No REA MCP tools in the active session; REA 6.8.0 setup refuses Node 24.15.0. Do not install unrelated global runtimes or change credentials without authorization. Local isolated native agents can continue supported work.
