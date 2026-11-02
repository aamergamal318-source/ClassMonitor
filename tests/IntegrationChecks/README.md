# Integration checks

```powershell
dotnet run --project tests/IntegrationChecks -c Release
```

These checks run the real `AgentServer` and `AgentConnection` over loopback mutual TLS. Capture, input, power, program launch and restrictions are recorded fakes, so no window opens and no system setting changes. Audit files and test folders go to a per-run temporary directory that is removed afterwards.

`RawClient` has none of the teacher console's local guards, which shows the agent enforces policy by itself. A separate hostile-agent case holds real credentials and sends unsolicited data to the teacher console.

They cover enrollment gating, certificate and pairing rejection, lesson start and expiry, replay, classroom scope, capability refusal, visible assistance, recording and sharing state, cleanup on every way a session ends, revocation, health timeouts, malformed traffic, file limits, audit content, retention, and multi-account records.

`--live-agent <ClassMonitorAgent.exe>` runs the real agent program on this computer, adds it with a generated password, and decodes frames of the real screen during a lesson. Frames stay in memory. It refuses to run if this account already has a computer password, and it removes what it created. `--windows-identities` exercises real non-exportable Windows keys.

They do not establish that real screen capture, the Windows ACLs on an administrator import, firewall or hosts changes, or physical devices work. See `docs/TEST_PLAN.md`.
