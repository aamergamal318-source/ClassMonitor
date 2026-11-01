# Requirements and traceability

Status: B=baseline source exists, I=in integration, P=planned, G=production gate. A baseline check does not certify a feature. Detailed settings: SETTINGS_MATRIX.md (console workstream). Official references: https://classroomspy.com/ , https://classroomspy.com/help/ , https://classroomspy.com/screenshots.htm . The existing MIT implementation and branding remain original.

| ID | Feature/workflow | Source/UI/settings | Permission | Acceptance and tests | Status |
|---|---|---|---|---|---|
| F01 | Modern teacher dashboard | Teacher/MainForm, Presentation; classroom/sidebar/grid, layout/theme | assigned teacher | accessible resizing/search/settings persistence; ConsoleChecks | I |
| F02 | Enrolled visible student | Agent/Program, AgentUi; management enrollment import/export | local administrator | no commands before trust, active lesson/control/record notices | I/G hardware |
| F03 | LAN discovery and explicit pairing | Discovery, AgentServer, Shared/Management | admin trust + mutual certs | spoof/wrong pin/default credentials denied; SecurityChecks/loopback | I |
| F04 | Groups/device health/presence | GroupsStore, MainForm, health timestamps | classroom scope | unassigned devices denied, stale/reconnect states | B/I |
| F05 | Adaptive live thumbnails | ScreenCapture, Session.SendLoop; width/FPS/JPEG/bandwidth | View + active lesson | clamps/backpressure; measured LoadProbe, actual-device capture gate | B/I/G |
| F06 | Enlargement/assistance | LiveViewForm, RemoteInput; selection/view/control | Assist + active lesson | visible state; permission denial; monitor coordinate tests | B/I |
| F07 | Teacher/student presentation | MainForm broadcast, TeacherCapture, Overlays | Present + active lesson | bounded frames, stop/cleanup on end/drop | B/I |
| F08 | Messages/questions/links | ChatForm, AgentUi, RunControl | Message; program launch Admin | targeted/group receive, bounded text and safe URLs | B/I questions |
| F09 | Lesson distribution/collection | FileTransfer, MainForm; approved roots/caps | Files | traversal/absolute escape/oversize denied; temp file tests | I |
| F10 | Focus/lock/unlock | Restrictions, Overlays; max focus duration | Focus | owned restoration on stop/drop/expiry; OS calls mocked | B/I |
| F11 | App/site allow/block | Restrictions; policy settings | Focus/Admin | allowed scope/notices/owned restoration; browser allow-list remains gate | B/I/G |
| F12 | School/roles/classrooms/audit | Shared/Management, enrollment administration | agent-authoritative grants | cross-school/classroom/role denial, expiry/replay, content-free audit | I |
| F13 | Arabic/Hebrew/English | Presentation/UiText; General.Language | local preference | RTL/LTR/resources/translation coverage; ConsoleChecks, human review gate | I/G |
| F14 | Accessibility/theme/filter/layout | Presentation, AgentTile, MainForm | local preference | readable contrast/names/tab order/high-DPI; hidden model checks + hardware/UI review | I/G |
| F15 | Opt-in recording/retention | Recorder, MainForm, visible agent state | Record + explicit action | grant/preference/ack, owned file retention/quota, stop on lesson end | B/I |
| F16 | Upgrade/uninstall/packages | Shared/Updates, Updater, installer, scripts | local write/admin where needed | 26 baseline updater checks; installer temp-root/registry-mock tests | B/I/G signing |

Gates cannot be marked complete from a simulated benchmark or successful compilation. Preserve and extend this table after executed integration evidence.
