# Security engineering review — management primitives

Date: 2026-11-01. Author: native security/network Codex sub-agent in isolated `work/security`; **not** external Claude Code or authenticated external Codex CLI. This is an implementation self-review with executed tests, not a substitute for an independent integrated-application security assessment.

## Scope

New `src/ClassMonitor.Shared/Management/` models, offline enrollment signatures, certificate validation, Windows identity-storage helper, bounded lesson authorization, connection admission/rate controls and content-free audit. Existing AgentServer/AgentConnection/AppConfig/MainForm are owned/integrated by the coordinator and were not modified in this worktree.

## Findings addressed in this component

| Risk | Component control | Executed evidence |
|---|---|---|
| Default shared credential or automatic discovery enrollment | Strong random pair material; no default credential accepted; explicit expected pins required | Default/zero material rejected; generated materials differ |
| TLS endpoint substitution | Exact SHA-256 certificate pin, validity and both TLS EKUs; mutual client certificate required by integration | Real loopback mTLS succeeds for pinned peers and rejects rogue server/client/missing client |
| Enrollment forgery/ambiguity | RSA-PSS SHA-256 over exact raw payload, strict schema, duplicate/unknown-property rejection | Tampering, wrong pins, malformed encoding/signature, case-ambiguous/duplicate/unknown fields rejected |
| School/classroom or role escalation | Authoritative local grant, separate role/capability validation, bound lesson context | Cross-school/classroom denied; Teacher cannot grant Admin; zero/unknown/ungranted permission denied |
| Indefinite lesson or clock rollback | Eight-hour maximum, grant/enrollment limits, wall-clock and monotonic deadline | Expired/excessive requests denied; active extension denied; wall-clock rollback cannot restore expired access |
| Session replay | Per-connection history plus bounded process-wide cache across reconnects | Same ID rejected after end and on a new authorization instance |
| Resource-exhaustion admission | Concurrent connection bound, monotonic per-peer attempt window, bounded peer table and saturating count | Concurrent/attempt/table limits and expiry recovery verified |
| Sensitive logs and unbounded retention | Enumerated content-free fields, hashed IDs, bounded rotation/retention, per-path writer synchronization | No clear identifiers, bounded files, expiry removal and valid concurrent JSON lines verified |

## Mandatory integration requirements

1. Remove every trust-all TLS callback and require a client certificate. `CertificateTrust.Matches` includes time/purpose/pin checks; it is **not** enrollment lookup by itself.
2. Read authoritative agent trust only through the coordinator's administrator-protected enrollment store, validate its ACL/ownership and bind its device pin to the current interactive identity. Never fall back to legacy user-writable config.
3. Apply `LessonAuthorization.Demand` before every screen frame and disruptive/data-bearing command; do not just check UI permissions. Required capability mapping must be tested end to end.
4. Bound handshake time and frame/transfer/request sizes. Dispose admission leases in every exit path. Do not log authentication material.
5. Expiry/disconnect/revocation must stop visible remote assistance/recording and restore only restrictions owned by the ending session. Revocation must reload/terminate existing sessions.
6. The trusted teacher pin must be displayed and explicitly approved by the enrolling administrator through an out-of-band/physical workflow. A valid self-signature is not organizational authorization.
7. Check classroom/certificate/enrollment expiry at connection and lesson time. Restrict file paths independently of authorization. Keep local UI signals visible and truthful.

## Open production gates

- Independent review and integrated endpoint/UI tests, including malformed protocol, operational permission mapping, disconnect/expiry cleanup and revocation.
- CurrentUser CNG/store persistence and non-exportability verified on clean Windows accounts; machine-wide/multi-user/RDP broker design and enrollment migration.
- Real administrator ACL import and reparse-point/path-race defenses. No privileged policy/registry/host-file action was exercised here.
- Persistent organizational authority/revocation, durable cross-restart replay semantics if required, audit tamper evidence and storage access controls.
- Hardware/GPU/multi-monitor/capture regression tests on the reported affected student devices; these security tests do not claim to fix the black screen.
- Signing, installation/uninstallation, device-fleet and physical 30-device testing. Nothing was deployed or published.

## Test result

Command: `dotnet run --project tests/SecurityChecks -c Release` in the isolated security worktree, .NET SDK 9.0.203 baseline. **40 PASS**, exit 0. Build emitted zero compiler warnings/errors. Initial sandbox key access failed before tests; rerun with scoped escalation allowed temporary Schannel key access and all checks passed. No trust-store enrollment or visible fixtures were opened. Tests and generated logs stay in ignored `tests/SecurityChecks/artifacts/`.
