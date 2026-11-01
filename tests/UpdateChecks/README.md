# Update checks

```powershell
dotnet build ClassMonitor.sln -c Release
dotnet run --project tests/UpdateChecks -c Release
```

These checks open no visual fixture and use no live GitHub requests. They cover
release selection and rejection, offline/rate-limit behavior, integrity, component
extraction, version mismatch, tampering, installation, settings preservation,
backup and rollback. Process-level checks run the actual updater, verify that it
waits for the parent process, and launch a harmless replacement probe. Scratch
files stay under the ignored `artifacts` directory.
