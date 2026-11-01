# Console and settings checks

```powershell
dotnet run --project tests/ConsoleChecks -c Release
```

Requires the .NET 10 Windows desktop runtime/SDK. Tests validate all setting groups,
atomic profile persistence/recovery, import safeguards, adaptive stream profiles,
actual byte-budget waits, approved paths, owned recording retention, localized
catalog/direction, search, theme contrast and original glyphs. Forms are only
constructed headlessly and are never shown. No screen fixture, enrollment, remote
command or machine policy is invoked. Temporary data stays under ignored
`tests/ConsoleChecks/artifacts`.

Actual school network integration, physical Windows DPI/Narrator behavior and
complete translation of legacy dialogs remain separate release gates.
