# ADR-003: Bounded LAN transport before service rewrite
Status: accepted for this increment, 2026-11-01.

Evaluate ASP.NET Core/SignalR for central school coordination, native Windows capture APIs and streaming choices. SignalR supplies connection/group abstractions but does not itself solve device enrollment, permissions or video congestion. Retain bounded TCP/TLS messages for existing LAN workflows; separate policy/control from capture so an ASP.NET Core school service can be introduced later without replacing every classroom feature.

Keep JPEG snapshots as a measured baseline, clamp FPS/width/quality, use latest-frame backpressure and collect measured throughput/latency. Native Windows Graphics Capture/Desktop Duplication are candidates if evidence shows GDI capture fails on affected devices. Do not claim a new capture backend fixes the reported black screen without an affected-device regression test. Synthetic frames measure transport, not GPU capture.

Sources: https://learn.microsoft.com/en-us/aspnet/core/signalr/introduction?view=aspnetcore-10.0 , https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/screen-capture . No proprietary protocol is used.
