# Requirements and traceability

Status: V=implemented and verified by automated checks on the integrated branch, B=baseline source exists, G=production gate still open. A check does not certify a feature on real devices; see PROGRESS for what has not been executed. Detailed settings: SETTINGS_MATRIX.md (console workstream). Official references: https://classroomspy.com/ , https://classroomspy.com/help/ , https://classroomspy.com/screenshots.htm . The existing MIT implementation and branding remain original.

| ID | Feature/workflow | Source/UI/settings | Permission | Acceptance and tests | Status |
|---|---|---|---|---|---|
| F01 | Modern teacher dashboard | Teacher/MainForm, Presentation; classroom/sidebar/grid, layout/theme | assigned teacher | accessible resizing/search/settings persistence; ConsoleChecks | V/G human UI review |
| F02 | Enrolled visible student | Agent/Program, AgentUi; management enrollment import/export | local administrator | no commands before trust, active lesson/control/record notices | V/G hardware, real import |
| F03 | LAN discovery and explicit pairing | Discovery, AgentServer, Shared/Management | admin trust + mutual certs | spoof/wrong pin/default credentials denied; SecurityChecks/loopback | V/G real import |
| F04 | Groups/device health/presence | GroupsStore, MainForm, health timestamps | classroom scope | unassigned devices denied, stale/reconnect states | V (health, presence, reconnect); B groups, not covered by automated checks |
| F05 | Adaptive live thumbnails | ScreenCapture, Session.SendLoop; width/FPS/JPEG/bandwidth | View + active lesson | clamps/backpressure; measured LoadProbe, actual-device capture gate | V/G real capture |
| F06 | Enlargement/assistance | LiveViewForm, RemoteInput; selection/view/control | Assist + active lesson | visible state; permission denial; monitor coordinate tests | V/G real input |
| F07 | Teacher/student presentation | MainForm broadcast, TeacherCapture, Overlays | Present + active lesson | bounded frames, stop/cleanup on end/drop | V protocol; G real display |
| F08 | Messages/questions/links | ChatForm, AgentUi, RunControl | Message; program launch Admin | targeted/group receive, bounded text and safe URLs | V |
| F09 | Lesson distribution/collection | FileTransfer, MainForm; approved roots/caps | Files | traversal/absolute escape/oversize denied; temp file tests | V |
| F10 | Focus/lock/unlock | Restrictions, Overlays; max focus duration | Focus | owned restoration on stop/drop/expiry; OS calls mocked | V with faked OS calls/G real |
| F11 | App/site allow/block | Restrictions; policy settings | Focus/Admin | allowed scope/notices/owned restoration; browser allow-list remains gate | V with faked OS calls/G real firewall, hosts, browser allow-list |
| F12 | School/roles/classrooms/audit | Shared/Management, enrollment administration | agent-authoritative grants | cross-school/classroom/role denial, expiry/replay, content-free audit | V |
| F13 | Arabic/Hebrew/English | Presentation/UiText; General.Language | local preference | RTL/LTR/resources/translation coverage; ConsoleChecks, human review gate | V catalog/G human review, legacy dialogs partly English |
| F14 | Accessibility/theme/filter/layout | Presentation, AgentTile, MainForm | local preference | readable contrast/names/tab order/high-DPI; hidden model checks + hardware/UI review | V model checks/G hardware review |
| F15 | Opt-in recording/retention | Recorder, MainForm, visible agent state | Record + explicit action | grant/preference/confirmation/notice, owned file and snapshot retention/quota, stop on lesson end | V |
| F16 | Upgrade/uninstall/packages | Shared/Updates, Updater, installer, scripts | local write/admin where needed | 26 baseline updater checks; installer temp-root/registry-mock tests | V Teacher real install/G signing, clean machine, real update |

Gates cannot be marked complete from a simulated benchmark or successful compilation. Preserve and extend this table after executed integration evidence.
