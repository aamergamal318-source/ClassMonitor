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

The checks cover public-only enrollment, exact reviewed teacher/device pins,
RSA-PSS payload integrity, strict schema/property parsing, default/invalid pair
material, certificate purpose/time validity, classroom/role/capability/lesson
enforcement, expiry under clock rollback, replay across reconnects, bounded
connection admission, content-free audit rotation/retention/concurrency, and real
mutual TLS rejection cases. Generated audit files stay in ignored `artifacts/`.

These checks do not establish that the application has integrated every guard,
that persistent OS identities or administrator enrollment ACLs work on a clean
machine, or that an affected GPU capture issue is fixed. See the security review.
