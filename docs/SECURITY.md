# Security and release gates

Trust boundaries: unauthenticated LAN/discovery; enrolled teacher identity; administrator-controlled device policy; student user/session; OS-protected keys; local recordings/files; trusted release repository/updater. Threats: rogue consoles, certificate substitution, replay, classroom privilege escalation, malformed frames, resource exhaustion, path escape, stale restrictions, private-data logging and update tampering.

Required controls: exact mutual certificate pins, strong unique pairing material, valid school/classroom grants and lesson expiry, capability checks before every command/capture, bounded handshake/frame/cadence/file sizes, rate limits, monotonic/replay-aware lesson state, administrator-controlled enrollment, visible student state, safe paths, reversible owned restrictions and content-free audit. No default password or automatic enrollment from beacons.

Baseline findings needing remediation: trust-all teacher certificate callback; optional client TLS authentication; global default `classmonitor`; unbounded connection handshake; commands without classroom roles; hidden-capable tray preference; restrictions without per-session ownership; errors swallowed around capture and UI; recording retention not implemented. See AUDIT for verified results and matrix for current status.

Production gates: independent security review; real enrolled Windows device interoperability; tested revocation/reconnect/session cleanup; enrollment ACL validation; signing or clearly documented unsigned internal build; installer/uninstaller verification; no secrets or student content in source/artifacts. Security claims are not complete until corresponding tests execute.
