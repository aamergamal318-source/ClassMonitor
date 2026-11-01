# Headless enrollment and authorization checks

```powershell
dotnet run --project tests/SecurityChecks -c Release
```

Windows is required for the real Schannel TLS cases. No visible window or test
desktop fixture is created. No production Windows certificate identity, protected
enrollment or school device is modified. Test certificates/private-key handles are
temporary and never inserted in the OS trust stores. Sandbox restrictions on
temporary Windows key access may require running the same command outside the
sandbox; do not weaken certificate validation to work around that failure.

The SDK is workspace-local, so running `SecurityChecks.exe` directly needs
`DOTNET_ROOT` set to `.tools/dotnet`; otherwise the host exits with `0x80008096`
before any check runs.

| Exit code | Meaning |
| --- | --- |
| 0 | Every check passed. |
| 1 | One or more checks failed. Each is named by a `FAIL:` line with its stack trace on stderr, and later checks still ran. |
| 2 | `SETUP FAILED`: temporary directories or test identities could not be created, so no check ran. |
| 3 | `FATAL`: an exception escaped outside a check. |

A failing run must never stop at a dialog: the process disables the Windows Error
Reporting UI for itself and its children, so even a fault the harness cannot catch
ends with its exit code. `--harness-self-test=setup-failure|failing-check|background-exception`
exercises those failure paths and each normal run asserts them.
`--harness-self-test=unguarded-crash` deliberately exits `0xe0434352` without the
handlers, to confirm by hand that no dialog appears; it is excluded from normal
runs because it adds a crash entry to the Windows event log.

The checks cover public-only enrollment, exact reviewed teacher/device pins,
RSA-PSS payload integrity, strict schema/property parsing, default/invalid pair
material, certificate purpose/time validity, classroom/role/capability/lesson
enforcement, expiry under clock rollback, replay across reconnects, bounded
connection admission, content-free audit rotation/retention/concurrency, and real
mutual TLS rejection cases. Generated audit files go to a per-run directory under
`%TEMP%/ClassMonitor.SecurityChecks`, removed on success and kept for diagnosis on
failure.

These checks do not establish that the application has integrated every guard,
that persistent OS identities or administrator enrollment ACLs work on a clean
machine, or that an affected GPU capture issue is fixed. See the security review.
