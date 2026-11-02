# Contributing

ClassMonitor is an MIT-licensed tool for visible classroom management on school-administered Windows computers. Read `AGENTS.md`, `docs/SECURITY.md` and `docs/PRIVACY.md` before changing behavior.

## What will not be accepted

Hidden or covert operation, keylogging, an arbitrary remote shell, arbitrary remote file access, remote silent installation, operating-system protection bypass, default shared credentials, or any path to capture or commands that skips enrollment, mutual TLS and an authorized lesson.

## Build and test

The SDK version is pinned in `global.json` (.NET 10). If it is not installed machine-wide, put a local copy in `.tools/dotnet` and prepend it to `PATH`. Test projects are not part of `ClassMonitor.sln`, so a solution build does not rebuild them.

```powershell
dotnet build ClassMonitor.sln -c Release
dotnet run --project tests/SecurityChecks -c Release
dotnet run --project tests/IntegrationChecks -c Release
dotnet run --project tests/ConsoleChecks -c Release
dotnet run --project tests/UpdateChecks -c Release
dotnet build installer/ClassMonitor.Installer -c Release
dotnet run --project installer/InstallerChecks -c Release
dotnet run --project tests/LoadProbe -c Release -- --seconds 5
```

`LoadProbe` rewrites `docs/BENCHMARK_RESULTS.md`. Commit that only when you mean to record a new measurement.

Tests use temporary directories and loopback peers and mock disruptive system operations. A test must not open a window unless it requires `--visual`. Headless checks link `tests/Shared/CrashFreeReporting.cs`, so a failure is written to stderr with an exit code rather than ending in a crash dialog.

## Changing security behavior

- The agent is the authority. A check in the teacher console is a convenience; add the matching refusal to `AgentServer` and test it with `RawClient` in `tests/IntegrationChecks`, which has no local guards.
- A new command needs a capability in `CommandPolicy`, an agent test showing refusal without it, and a visible student state if it records, controls or shares the screen.
- Nothing in the student's program folder or `agent.json` may be trusted or run elevated. Students can change both.
- Never log class secrets, frame content, chat or file contents, or private keys.

## Pull requests

Use an isolated branch. Record what you ran and what you could not test in `docs/PROGRESS.md`, and keep `TASKS.md` and `docs/FEATURE_MATRIX.md` accurate. Passing compilation or a simulated benchmark does not establish production readiness.

Releases, tags and real-device deployment need explicit approval from the project owner.
