# Privacy

ClassMonitor is for enrolled school-managed devices during authorized lessons. Students see the agent and active lesson/control/recording status. Screen recording requires an explicit authorized action and visible indication. No covert persistence, keyboard logging, credential harvesting, OS bypass or microphone/camera collection is implemented in this scope.

Screen frames, messages and lesson files stay between authorized LAN participants. Update checks contact the public GitHub release API and reveal ordinary request metadata; no student content is sent to GitHub. Audit records should contain actor/device identifiers, operation, classroom, time and outcome, not screenshots, keystrokes, passwords, chat text or file contents.

Schools must configure retention, access, approved directories and deletion policies. Existing historical recordings are not retroactively erased automatically. Privacy functionality, retention enforcement and storage protection require tests; open gates are recorded in FEATURE_MATRIX and PROGRESS. Never include desktop diagnostic screenshots in public repository or releases.
