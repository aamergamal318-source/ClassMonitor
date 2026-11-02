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

## Integration, 2026-11-02
Branch `work/integration` combines the unfinished coordinator, console and endpoint work (copied, not moved, from their worktrees) with a new teacher connection, enrollment workflow, lesson control, visible student state, single-file setup and independent-review fixes. Version 1.2.0 is prepared, not published.

Executed with SDK 10.0.403 on one Windows 11 development computer:
- SecurityChecks 43, IntegrationChecks 40, ConsoleChecks 26, UpdateChecks 29 (also against the published updater), InstallerChecks 17: all pass. `Publish-Release.ps1` without `-Publish` runs these and builds the installer.
- IntegrationChecks `--windows-identities`: real non-exportable Windows keys complete mutual TLS and a lesson; created identities are removed afterwards.
- Mutation proof on a scratch copy: the unmodified code passed, then all 17 guards removed one at a time were each detected.
- Real installer on this computer: Teacher console installed through the actual setup window, with Start menu and desktop shortcuts, Apps entry and version 1.2.0, using 85.4 MB. Uninstall through the Apps command waited for the prompt: No kept everything, Yes removed files, Apps entry and shortcuts. The student role also appears correctly installed on this computer, but that install did not come from a scripted test step, so it was left in place and its uninstall was not tested.
- Real GitHub update path against public v1.0.0: metadata, download and SHA-256 passed, then the package was correctly refused because v1.0.0 has no updater. Installations of v1.0.0 therefore need setup once.
- LoadProbe 10/20/30 simulated peers on loopback: see BENCHMARK_RESULTS.

Independent review: a Claude sub-agent that did not write this code read it without running anything. It found no route to capture or commands around enrollment, mutual TLS and a lesson, and raised 35 findings. See SECURITY_REVIEW for what changed and what remains. It is the same vendor's model, so it is not a cross-vendor review.

Not executed, and therefore still release gates:
- Administrator enrollment import with a real elevation prompt and real ACLs. This session has no elevated rights, so no agent has run end to end with a stored enrollment.
- Real firewall rule, website and internet blocks, and the leftover-restriction cleanup with administrator rights. Only the parsing and arguments are tested.
- A real update from one installed version to a newer stable release, which needs a published stable release.
- Real screen capture through the new session on affected student computers, the ScreenStreaming visual fixtures (they require `--visual`), multi-monitor, RDP, fast user switching, and physical 30-device use. The black-screen report remains unconfirmed.
- Human review of Arabic and Hebrew, accessibility on real hardware, and code signing.

## Adding computers with a password, 2026-11-02
Owner's request: a password when installing a student computer, and a teacher console that shows only computers added with that password. Implemented on TCP 5902 with a salted hash and proofs bound to both certificates. See ENROLLMENT for the security trade-offs. Executed: IntegrationChecks 48 (also 6 clean runs in a row), and the mutation proof on a scratch copy: the unmodified code passed, then 22 guards removed one at a time were each detected. Also `IntegrationChecks --live-agent`: the real agent program on this computer was added with a generated password, a lesson started, real screen frames decoded at 960x270 in memory, and frames stopped when the lesson ended. That ran over loopback, not between two computers. This work found and fixed a student's end-of-session notice that could be lost.

Only replace these statuses with executed evidence. Record exact test commands/results and remaining release gates after integration.
