# Explicit device enrollment and lesson trust

## Adding a computer with its password (the normal way)

This path is the project owner's decision of 2026-11-02, for simpler classroom use than file exchange.

1. Whoever installs the student agent chooses a computer password of at least 8 characters. Setup stores only a salted PBKDF2-SHA256 hash (600,000 iterations) in `%LOCALAPPDATA%\ClassMonitor\pairing-password.json`. There is no default password, and the pairing port (TCP 5902) stays closed until one is set.
2. The teacher console starts empty and shows only computers added to it. A beacon cannot add a computer; it can only help find one already added whose address changed, and its pin is still checked.
3. **Add computer** takes an address, the password and a classroom. Both sides connect with their own certificates, then prove knowledge of the hash with an HMAC over both certificate fingerprints and fresh nonces. The password never crosses the network, and a machine in the middle presenting its own certificate fails. Only after the computer has proved itself does the teacher sign the enrollment and send it. The computer stores it in `enrollment.json`, and normal pinned mutual TLS takes over on TCP 5900.
4. Five wrong passwords lock pairing for 15 minutes, until the agent restarts.

What this gives up compared with the file path below:
- **No administrator reviews the teacher.** Knowing the password is the approval, so its strength matters. Do not tell it to students.
- **Offline guessing is possible.** Someone impersonating a computer on the network can capture a teacher's proof and try passwords offline, slowed by the hash. A short or common password would not survive that. A password-authenticated key exchange would remove this and is a candidate improvement.
- **The record is in the student's profile,** so a student can replace it. That affects only their own computer, and they could already exit the program. It does not let them reach another computer.
- **A password grants a classroom teacher only:** view, messages, presenting, files, focus, assistance and recording. Power and program launch still need the administrator file path, and the computer refuses the administrator role through this port.

Lessons, capability checks, visible state and recording notices apply exactly as they do for file enrollment.

## Trust model for file enrollment

Discovery is not enrollment. No beacon, hostname or console preference may authorize a device. Every normal monitoring connection must use mutually authenticated TLS and an exact SHA-256 leaf-certificate pin on each endpoint. The certificate must be currently valid and have **both** TLS client/server EKUs. A valid classroom grant and bounded active lesson are additionally required before any capture or command.

This increment uses **local administrator approval of an explicitly displayed teacher fingerprint**, not a centrally operated school CA. The enrollment signature proves possession of the selected teacher's private key; it does not independently establish that the teacher is employed by the school. The administrator must verify the displayed pin through a trusted physical/out-of-band channel. A self-issued certificate alone is never authorization.

## Artifact flow

1. In the intended interactive student Windows account, the agent obtains its persistent Windows CNG/My-store identity and exports `DeviceEnrollmentRequest`. The request contains a public DER certificate, its SHA-256 fingerprint, a deterministic device ID and a display label; it contains **no private key**.
2. The school administrator reviews that request and its fingerprint, selects the school/classroom and precise capabilities, and uses the teacher identity to sign an `EnrollmentPackage`. `EnrollmentManager.CreatePackage` generates a unique cryptographic 256-bit pairing secret. A Teacher grant cannot include the Admin capability; SchoolAdmin still needs an explicitly assigned classroom.
3. On the student computer, an explicit administrator import verifies the exact student and teacher fingerprints and package signature, then writes the authoritative protected enrollment manifest. The coordinator's managed enrollment store owns the Windows ACL check/import integration. Never copy this policy into user-writable legacy `agent.json` and treat it as authoritative.
4. The teacher retains the matching enrolled device record/pin and uses its own OS-protected certificate as the TLS client identity. The agent requires a trusted client certificate; the teacher pins the agent certificate. Unique pairing material may support the existing HMAC exchange only **after** mTLS; the historical `classmonitor` default never grants access.
5. The teacher starts a classroom lesson with a fresh random GUID (`Guid.NewGuid().ToString("N")`), school/classroom scope and explicit expiry. The agent checks its authoritative grant. All operations call `LessonAuthorization.Demand` with the required capability. Ending/expiry/disconnect must stop capture/control/recording and restore owned restrictions.

In the applications: the tray item **Export enrollment request** (or `ClassMonitorAgent.exe --enrollment-request <file>`) performs step 1, **Enroll device** in the teacher console's settings performs steps 2 and 4, and **Import enrollment file** in `ClassMonitorSetup.exe` performs step 3 under the Windows administrator prompt. The student's own program never elevates, because students can change its folder; the administrator runs a trusted copy of setup. **Start lesson** performs step 5 for one classroom.

## Formats and validation

`EnrollmentPackage` contains Base64 signed payload bytes, RSA-PSS/SHA-256 signature and the teacher public certificate. The signature covers the **exact payload bytes**, eliminating reserialization/canonicalization ambiguity. After verification, schema 1 is strictly deserialized; duplicate properties (including case ambiguity), unknown fields, invalid IDs, mismatched pins/scope, malformed material and expiry are rejected. Enrollment cannot outlive either leaf certificate. Only public DER certificates are accepted; PFX/private-key blobs are rejected.

The payload is integrity protected, **not encrypted**: it includes pairing material. Treat exported enrollment packages and teacher paired-device records as sensitive configuration, transfer them through an administrator-approved channel and remove export copies according to school policy. They must never be committed or placed in public release assets. No private key is ever exported by `WindowsIdentityStore`.

## Windows identity and revocation limitations

- `WindowsIdentityStore.GetOrCreate("Agent"/"Teacher")` uses CurrentUser Windows CNG named non-exportable RSA keys and the CurrentUser My certificate store. Keys are protected by the Windows user/profile boundary, not a hardware-attestation guarantee.
- Enrollment is therefore bound to the intended **interactive Windows user/profile**. Running an enrollment request under a different administrator account produces a different identity. An administrator import must target the previously reviewed student request, and the subsequently running agent must match that fingerprint.
- Each Windows account has its own identity and its own policy file, `enrollment-<fingerprint>.json`, so a shared computer enrolls each account once and the teacher keeps a record per account. Only one signed-in account can hold the agent port at a time; a second waits.
- A machine-wide service broker, concurrent Windows users/RDP sessions, central school CA/revocation and signed organizational assignments remain separate production gates. Local administrator compromise is outside this local trust boundary.
- The initial shared model has one authoritative trusted teacher grant per enrollment. Replacing it requires an explicit protected administrative import. Application integration must terminate/reload existing sessions when revoking/replacing enrollment; a disk write alone does not revoke an already loaded session.
- Replay memory spans connections within a running process and is bounded; it is not a persistent school revocation ledger across restarts. Fresh TLS handshakes, unique pairing challenges, short lessons and authenticated senders remain mandatory.

## Auditing

`AuditWriter` accepts enumerated event/reason/capability metadata only. Actor, device, school and classroom identifiers are hashed before writing, with timestamp and allow/deny outcome. It has no parameter for screen, password, chat, file or arbitrary command content. Files rotate within a configured byte bound and owned log files expire by configured retention. Storage ACLs and off-device tamper-evident collection remain deployment/review gates; the local log is not cryptographically immutable.

## Revocation
An administrator removes the device's policy file from `%ProgramData%\ClassMonitor.Managed`, or imports a replacement. For a computer added with its password, delete `enrollment.json` from that account's `%LOCALAPPDATA%\ClassMonitor`, and use **Remove computer** on the teacher console. A running session notices within about two seconds and ends. Local accounts can read policy files, which include pairing material; the teacher's private key remains the barrier, and that material adds nothing against a local user.

## Executed tests

`dotnet run --project tests/SecurityChecks -c Release` executed successfully with .NET 9 baseline: **40 checks**. These include actual loopback mTLS acceptance/rejection, signature/strict payload validation, explicit pins, permission/school/classroom/expiry/replay guards, monotonic expiry/window behavior, bounded connection admission and content-free/rotating/concurrent audit.

No visible desktop window, real student deployment, OS trust-store enrollment or `WindowsIdentityStore.GetOrCreate` call occurred. Temporary test certificates are imported with temporary user key handles solely for Windows Schannel; sandbox restrictions required an escalated test invocation. Current-user persistent-key behavior, installer ACLs and actual school devices must be validated separately before production approval.
