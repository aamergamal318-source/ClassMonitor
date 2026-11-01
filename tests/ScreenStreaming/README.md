# Screen streaming regression checks

Run on Windows with the .NET 9 SDK. Every visible test requires explicit `--visual`;
without it the program exits without opening a test window. Run these only when
the operator has requested a visible desktop check.

```powershell
dotnet run --project tests/ScreenStreaming -c Release -- --visual --transport
dotnet run --project tests/ScreenStreaming -c Release -- --visual
dotnet run --project tests/ScreenStreaming -c Release -- --visual --end-to-end
dotnet run --project tests/ScreenStreaming -c Release -- --visual --published
dotnet run --project tests/ScreenStreaming -c Release -- --visual --diagnostics
```

The transport check starts three authenticated TLS loopback connections, sends
ten JPEG frames per connection, and asserts that the real teacher thumbnail and
live-view controls render the expected color. It does not validate screen capture.

The capture check opens a visible magenta test window and calls the real agent
capture method on a background thread. Three consecutive screenshots must include
the window. Keep the Windows session unlocked and the window unobscured while
running this check. A locked desktop cannot validate capture of the test window.
The latest diagnostic screenshot is stored in the ignored `artifacts` directory;
it may contain desktop content and must not be published.

These local checks do not establish that capture works on other physical computers,
graphics adapters, disconnected remote sessions, or secure desktops.

`--end-to-end` uses the real agent streaming session on a background thread after
TLS authentication. `--published` starts the self-contained executable under
`release/ClassMonitor/agent`, checks the complete teacher console, then checks
three simultaneous connections. It reads existing agent settings without
printing the password, and stops only the process it started. Leave the test
window visible; obscuring it can fail the visual assertion. Port 5900 (or the
configured agent port) must be free. `--diagnostics` also exercises the local
capture report used by the student diagnostic window.
