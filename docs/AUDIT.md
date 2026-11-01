# Existing repository audit — 2026-11-01

## Inspected baseline
Existing MIT repo, baseline `4a5b091`; no history rewrite. Read README, FEATURE-GAP, DIAGNOSTICS, CHANGELOG, license, solution, source, headless updater checks and package scripts. Baseline commands executed before migration: `dotnet build ClassMonitor.sln -c Release` (0 errors, 0 warnings); `dotnet run --project tests/UpdateChecks -c Release` (26 passed). The 26 checks cover updates, not school enrollment or classroom authorization.

## Verified findings
| ID | Evidence | Finding | Priority |
|---|---|---|---|
| SEC-01 | AgentConnection TLS callback `(_,_,_,_) => true`; AgentServer clientCertificateRequired=false | Endpoint identities not mutually bound to TLS | Critical |
| SEC-02 | AppConfig.Password default `classmonitor` | Shared known credential; no explicit per-device enrollment | Critical |
| SEC-03 | AgentServer.Session dispatches operations without classroom/role/lesson guard | Authenticated password holder gains all commands | Critical |
| FILE-01 | FileTransfer.ReadForCollect accepts arbitrary full path; base64 expands bytes beyond existing rough limit | Collection not constrained to approved directories; frame size mismatch | High |
| UI-01 | AppConfig.AgentVisible may disable tray; no visible assistance/recording handshake | Student session transparency incomplete | High |
| STATE-01 | AppConfig.Load catches corrupt JSON and silently returns defaults; Save directly overwrites | Unsafe recovery/default behavior and non-atomic persistence | High |
| POL-01 | Restrictions shared across sessions; Dispose always edits hosts/firewall | Session ownership and non-disruptive test seam missing | High |
| PRIV-01 | Activity title/process transmitted unconditionally after authentication | Lesson/policy scope and retention not enforced | High |
| REC-01 | Recorder saves AVI; no storage/retention enforcement or student recording acknowledgement | Recording lifecycle incomplete | High |
| CAP-01 | CopyFromScreen returns gray on exception; local fixture succeeded only after Windows unlock | Affected-machine black screen not reproduced; no proven fix | Open hardware gate |
| UX-01 | Single crowded toolbar, prompt-only password settings; groups use machine name | Settings, search, enrollment/health and localization need integration | Medium |
| DEP-01 | .NET9 SDK9.0.203 installed; no compiler for MSI detected | Migrate to locally installed .NET10 LTS; bootstrapper is not enterprise MSI | Release gate |

## Capability validation
`codex --version`: 0.164.0-alpha.11.1; `codex login status`: Not logged in. `Get-Command claude/rea/wix/iscc` found none; common Claude executable paths absent. Active tool list has no REA MCP. Earlier actual REA 6.8.0 setup dry-run returned needs_human/no appliedActions because Node24.15.0 does not satisfy its engine requirement. Native collaborators are available and isolated in git worktrees; they are not a substitute claimed as external Claude/Codex execution.

## SDK installation
Official dotnet-install script executed with channel10.0 into ignored `.tools/dotnet` only. Installed SDK10.0.403; downloaded/archive sizes both300745255 bytes. No machine-wide runtime or Node change. New migration build/tests are recorded separately in PROGRESS; baseline success is not reused as migration success.

## Audit limits
No fleet deployment, registry installation, real-device pairing or privileged restriction execution is authorized/executed. Desktop capture was previously verified on this host, not the affected students. No signing credentials supplied. Public reference screenshots were reviewed as workflow evidence; no proprietary assets/code were imported.
