# Measured loopback transport benchmark

Executed UTC: 2026-11-02T07:48:58.9262146+00:00. Runtime: 10.0.14; logical processors visible to process: 20.

Each row uses unique client certificates, exact SHA-256 pins on both sides, TLS 1.2/1.3, production `Frame` framing, 32,768-byte synthetic payloads, target 10 frames/s/client and 10 seconds of steady-state measurement. Keys and connections are prepared before the timer starts. No screen capture, visible windows, device installation, school permissions or OS policy changes occur.

| Simulated peers | Frames echoed | Duration s | CPU % normalized | Working set before/after MiB | Process-lifetime peak MiB | RTT p50/p95 ms | Application Mbps, both directions |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 10 | 1000 | 10.018 | 0.61 | 52.33 / 66.79 | 68.54 | 0.960 / 1.758 | 52.343 |
| 20 | 2006 | 10.087 | 2.20 | 70.72 / 72.70 | 75.80 | 0.923 / 1.613 | 104.283 |
| 30 | 3002 | 10.085 | 2.28 | 78.81 / 75.48 | 80.74 | 0.975 / 3.692 | 156.081 |

CPU is this process's TotalProcessorTime delta divided by elapsed wall time and logical processors, covering both TLS endpoints. Throughput counts payload plus the 5-byte application frame header in both directions; it excludes TCP/TLS overhead. RTT uses same-process monotonic timestamps and includes scheduling/framing/TLS/echo. Peak working set is process-lifetime, not isolated per row. Host contention and loopback results cannot establish physical LAN/Wi-Fi, GPU capture, 30 separate machines, UI painting, authorization or end-to-end classroom performance.

Executed preflight checks: oversized/invalid/truncated framing rejected, unknown client certificate rejected, incorrect server pin rejected, and same enrolled test certificate reconnects after a clean disconnect. These checks validate this harness and existing framing; they do not replace production enrollment/permission integration tests.

Reproduce: `dotnet run --project tests/LoadProbe -c Release -- --seconds 5`. Raw results: ignored `tests/LoadProbe/artifacts/latest.json`. No performance acceptance threshold is claimed yet; physical-device release gate remains open.
