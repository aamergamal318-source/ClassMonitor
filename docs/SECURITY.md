# Security and release gates

Trust boundaries: unauthenticated LAN/discovery; enrolled teacher identity; administrator-controlled device policy; student user/session; OS-protected keys; local recordings/files; trusted release repository/updater. Threats: rogue consoles, certificate substitution, replay, classroom privilege escalation, malformed frames, resource exhaustion, path escape, stale restrictions, private-data logging and update tampering.

Required controls: exact mutual certificate pins, strong unique pairing material, valid school/classroom grants and lesson expiry, capability checks before every command/capture, bounded handshake/frame/cadence/file sizes, rate limits, monotonic/replay-aware lesson state, administrator-controlled enrollment, visible student state, safe paths, reversible owned restrictions and content-free audit. No default password or automatic enrollment from beacons.

Baseline findings in AUDIT are addressed on the integrated branch: pinned mutual TLS replaces the trust-all callback, the shared password is unused, handshakes are bounded, the agent checks capability and lesson for every operation, the tray cannot be hidden, restrictions are session-owned and swept after interruption, and recording has opt-in, notice, retention and quota. See SECURITY_REVIEW for open findings.

Adding computers: the owner chose password-based adding over administrator review for normal classroom use. See ENROLLMENT for what that gives up. The password must be strong and kept from students.

Local trust boundary: the student's program folder and `agent.json` are student-writable, so neither is trusted and neither runs elevated. Enrollment import and restriction cleanup run from an administrator's trusted copy of setup. Website and internet blocks need an agent running as administrator; do that only where students cannot change the program folder.

Production gates: independent security review; real enrolled Windows device interoperability; tested revocation/reconnect/session cleanup; enrollment ACL validation; signing or clearly documented unsigned internal build; installer/uninstaller verification; no secrets or student content in source/artifacts. Security claims are not complete until corresponding tests execute.
