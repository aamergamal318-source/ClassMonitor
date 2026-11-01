# Verification plan and executed evidence

## Test boundaries
Automated tests use loopback, mock disruptive operations and isolated artifact
directories. No actual device enrollment/deployment, OS restrictions, registry
installation, firewall modification or visible screen fixture is authorized by
these tests. Existing screenshot tests must require explicit `--visual`.

## Executed by QA/release worktree on 2026-11-01

| Command | Observed result | What it establishes |
|---|---|---|
| `dotnet build ClassMonitor.sln -c Release` | Passed, zero warnings/errors on .NET SDK9.0.203 baseline | Existing baseline compiles |
| `dotnet run --project tests/LoadProbe -c Release -- --seconds 5` | Passed preflights and 10/20/30 peers, 500/1000/1500 echoed frames | Production framing over mutually pinned loopback TLS; see BENCHMARK_RESULTS for measurements |
| `dotnet build installer/ClassMonitor.Installer -c Release` | Passed, zero warnings/errors | Bootstrapper and safe core compile |
| `dotnet run --project installer/InstallerChecks -c Release` | 13 checks passed | Isolated install/upgrade/uninstall, mocked registry rollback, lock rejection, exact entry extraction and real PowerShell worker constrained to temporary paths |
| `dotnet publish installer/ClassMonitor.Installer ... -p:PayloadPath=<existing 1.1.0 ZIP>` then `ClassMonitorSetup.exe --verify-payload <report>` | Self-contained bootstrapper built; headless report: payload present, 120,471,819 bytes, installer version1.1.0.0 | Payload was embedded and could be opened without setup UI; does not certify product integration or real installation |

SChannel TLS tests could not acquire temporary certificate-key credentials inside
the sandbox (`0x8009030E`); rerun outside sandbox passed. No certificate trust-store
enrollment was performed. Test certificates were temporary generated identities.
These results precede root integration/runtime migration; rerun after merging.
The baseline bootstrapper artifact is 233,446,870 bytes and remains local under
ignored `installer/artifacts`. Rebuild against the integrated product payload;
do not distribute that baseline artifact as the new school-managed product.

## Required integrated coverage

- Enrollment: malformed/replayed packages, wrong signatures/certificate pins,
  expired teacher grants, school/classroom mismatch, revocation, rate limits and
  missing authorization must fail closed.
- Lessons: no screen/control/recording before an authorized session; explicit
  start/end/expiry; reconnect cannot resurrect stale permissions; visible status.
- Screens: frame bounds and latest-frame backpressure; max FPS/quality/size;
  timeout/drop recovery; real GPU/multi-monitor/DPI/lock/RDP tests on enrolled
  Windows endpoints. The reported black-screen case needs affected-device proof.
- Workflow: selected/classroom scope for messages, files and presentation;
  approved file paths and size limits; policy cleanup on disconnect; opt-in
  recording, retention/deletion and audit without content.
- Settings/UX: every active setting validates, persists and affects behavior;
  Arabic/Hebrew RTL and English LTR; accessibility names, tab navigation, high DPI,
  dark/light contrast, search/filtering and saved layout. Human and automation
  results must be labeled separately.
- Updates: metadata rejection, checksum, exact entries/version, parent wait,
  immediate startup failure rollback and settings preservation; rerun existing
  UpdateChecks against current framework/self-contained workers.
- Packaging: embedded production payload, correct file versions, both component
  installs in a clean Windows profile with real Apps registration, uninstall
  restoration/failure behavior, signed artifacts when credentials are available.

## Release gates still requiring actual infrastructure

Physical 30-device LAN/Wi-Fi workload, real capture/UI/permissions integration,
administrator enrollment ACLs, multi-user/session operation, clean-machine real
registry install/uninstall and signer verification are not established by the
isolated checks. Loopback throughput is not a claim of classroom capacity.
No public release or real-device deployment may be performed without approval.
