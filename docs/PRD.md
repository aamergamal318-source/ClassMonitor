# ClassMonitor product requirements

## Goal and users
Evolve the existing Windows teacher/agent/shared application into an original, maintainable classroom management product. School administrators enroll devices and assign classrooms; teachers conduct authorized lessons; students receive visible indications and assistance. Preserve MIT history and compatible configuration shape. User scope: Arabic, Hebrew and English; school-managed Windows PCs; LAN first.

## Required workflows
1. Administrator bootstraps teacher identity and explicitly imports device enrollment; both peers verify pinned certificates and classroom grants.
2. Teacher selects an assigned classroom, sees presence/health, starts a bounded lesson and receives thumbnails only for enrolled devices in that lesson.
3. Authorized teacher enlarges a screen, requests visible remote assistance, presents a desktop, sends messages/questions/links, distributes/collects approved lesson files and applies session-scoped focus rules.
4. Recording is opt-in under policy, visibly marked, stored locally with retention/deletion controls. Ending a lesson restores owned restrictions and stops capture/control/recording.
5. Users choose validated persistent preferences, language/direction, theme, quality/cadence, network timeouts and diagnostics. In-app verified updates preserve settings and support recovery.
6. Administrator can revoke enrollment/teacher assignment, audit security actions and install/upgrade/uninstall through documented packages.

## Acceptance principles
Every matrix row identifies implementation, UI, settings, permissions, acceptance and executed tests. Existing source is not evidence of functional completion. All 16 core requirements in the user's mission remain tracked in `FEATURE_MATRIX.md`. Thirty-device load measurements must be reported as simulations unless measured on physical enrolled endpoints.

## Boundaries
No covert monitoring, hidden persistence, keylogging, credential collection, unauthorized camera/microphone, arbitrary shell, OS bypass, protected protocol imitation, copied proprietary assets or activation mechanisms. No school deployment/public release without approval. Do not label the local package production-ready while hardware, security or signing gates remain open.

## Reference research (2026-11-01)
Public feature and workflow sources: https://classroomspy.com/ , https://classroomspy.com/help/ , https://classroomspy.com/screenshots.htm . The matrix uses their documented classroom screens, groups, assistance, presentation, messaging, restrictions, recording/reporting and administration workflows. No proprietary artifacts were supplied for analysis.
