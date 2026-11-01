# Verified progress

## Baseline, 2026-11-01
- Existing MIT ClassMonitor history preserved at `4a5b091`; work proceeds on `engineering/production-foundation`. No mission release is approved for publishing.
- Baseline .NET 9 solution built with zero errors/warnings. Previous in-app updater tests: 26 checks against framework-dependent and self-contained workers plus package validation; see prior changelog. These do not validate the newly requested school authorization system.
- Capture appeared locally after Windows unlock. Black-screen report on affected student devices remains unconfirmed; diagnostic prerelease exists. No new visible test fixture is permitted unless explicitly requested.
- Tools: Claude Code absent; Codex CLI installed but not logged in; no REA MCP; REA 6.8.0 setup blocked by Node 24.15.0. Native agent collaboration is available. No signing credentials or installer compiler detected.
- Official product and Microsoft documentation reviewed. ADRs recorded before implementation.

## Milestones
1. Repository audit/specification/threat model: in progress; documentation created, audit evidence and traceability being expanded.
2. Enrollment, settings/dashboard, device status: pending implementation and integration.
3. Mutual authentication, permissions, session-scoped LAN streaming: security remediation prioritized.
4. Remote assistance/presentation: existing implementation requires authorization and visible-state integration.
5. Messaging/files/focus: existing implementation requires limits/scope/cleanup verification.
6. Policies/recording: existing implementation requires ownership, consent and retention work.
7. Localization/accessibility/resilience/performance: assigned work planned; no load numbers claimed.
8. QA/review/installer/local packaging: pending. No physical device deployment or public publishing authorized.

## SecurityChecks crash fix, 2026-11-01
- Symptom: `SecurityChecks.exe` ended with unhandled .NET exception `0xe0434352` and started Windows Error Reporting. Reproduced on the unpatched build: `CryptographicException: Access denied` importing temporary keys at `Program.cs:264` called from line 14 (low-integrity token, matching the sandbox failure in `SECURITY_REVIEW.md` and the empty artifact directory of the first run; the original run left no event-log entry) and `IOException` creating the working-directory-relative artifact path at line 12.
- Root cause: fixture setup ran outside the `try`, and the first failing check aborted the rest. `AeDebug` registers `vsjitdebugger.exe` on this machine, so an uncaught exception could stop at interactive crash UI.
- Fix, with no check removed or weakened: setup failure exits 2, each check failure is named with its stack trace and later checks still run (exit 1), escaped or background exceptions exit 3, WER UI is disabled for the process and its children, and audit files moved to per-run `%TEMP%` directories.
- Evidence with SDK 10.0.403: SecurityChecks **43 PASS** (40 existing plus 3 harness regression checks), exit 0, plus 10 consecutive clean runs; ConsoleChecks 26 PASS; UpdateChecks 26 PASS; LoadProbe `--seconds 5` exit 0. An unguarded crash started 0 `WerFault` processes after the fix versus 1 before. Removing the active-lesson guard in a scratch copy produced 4 named failures and exit 1.
- Limitations: ScreenStreaming was not executed (`--visual` was not requested; it exits 2 without opening a window). ConsoleChecks, UpdateChecks, LoadProbe and ScreenStreaming still run code before their `try` and do not disable WER UI, and test projects are outside `ClassMonitor.sln`, so a solution build does not rebuild them.

Only replace these statuses with executed evidence. Record exact test commands/results and remaining release gates after integration.
