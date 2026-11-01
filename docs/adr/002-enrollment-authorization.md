# ADR-002: Explicit trust and classroom-scoped lessons
Status: accepted before implementation, 2026-11-01.

Current HMAC passwords over trust-all TLS do not bind authentication to the TLS endpoint. Replace trust-all acceptance with exact enrolled certificate pins on both peers and require a client certificate. No default credential authorizes a session. Preserve old JSON properties for migration, not as a security bypass.

Use administrator-controlled per-device enrollment, public certificate request/export/import and strong unique pairing material. Agent-side teacher grants contain school/classroom assignment, role, permissions and expiration; a teacher cannot expand those by changing console preferences. Audit allow/deny and lesson lifecycle without content. Require a bounded active lesson before capture/commands. Unpaired agents may advertise availability for enrollment only. OS key storage protects identities; do not export private keys into enrollment files.

Local administrative compromise, multi-user Windows identity scope, school PKI/central revocation and installer ACLs must be documented and tested before production approval. Enrollment file import is an explicit administrator workflow. Secure errors must guide users without falling back to legacy trust-all TLS.
