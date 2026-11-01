# Measured loopback transport benchmark

Executed UTC: 2026-11-01T11:12:15.1254160+00:00. Runtime: 9.0.4; logical processors visible to process: 20.

Each row uses unique client certificates, exact SHA-256 pins on both sides, TLS 1.2/1.3, production `Frame` framing, 32,768-byte synthetic payloads, target 10 frames/s/client and 5 seconds of steady-state measurement. Keys and connections are prepared before the timer starts. No screen capture, visible windows, device installation, school permissions or OS policy changes occur.

| Simulated peers | Frames echoed | Duration s | CPU % normalized | Working set before/after MiB | Process-lifetime peak MiB | RTT p50/p95 ms | Application Mbps, both directions |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 10 | 500 | 5.011 | 0.37 | 48.50 / 68.61 | 68.61 | 0.374 / 2.101 | 52.326 |
| 20 | 1000 | 5.014 | 1.32 | 69.88 / 78.41 | 79.09 | 0.654 / 2.586 | 104.587 |
| 30 | 1500 | 5.018 | 1.09 | 75.85 / 76.05 | 79.59 | 0.365 / 1.271 | 156.749 |

CPU is this process's TotalProcessorTime delta divided by elapsed wall time and logical processors, covering both TLS endpoints. Throughput counts payload plus the 5-byte application frame header in both directions; it excludes TCP/TLS overhead. RTT uses same-process monotonic timestamps and includes scheduling/framing/TLS/echo. Peak working set is process-lifetime, not isolated per row. Host contention and loopback results cannot establish physical LAN/Wi-Fi, GPU capture, 30 separate machines, UI painting, authorization or end-to-end classroom performance.

Executed preflight checks: oversized/invalid/truncated framing rejected, unknown client certificate rejected, incorrect server pin rejected, and same enrolled test certificate reconnects after a clean disconnect. These checks validate this harness and existing framing; they do not replace production enrollment/permission integration tests.

Reproduce: `dotnet run --project tests/LoadProbe -c Release -- --seconds 5`. Raw results: ignored `tests/LoadProbe/artifacts/latest.json`. No performance acceptance threshold is claimed yet; physical-device release gate remains open.
