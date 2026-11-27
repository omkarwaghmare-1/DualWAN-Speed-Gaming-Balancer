# Changelog

All notable changes to **DualWAN Speed & Gaming Balancer** are documented in this file.

## [2.0.0] - 2026-11-27
### Added
- Native Windows `.NET Framework 4.x` GUI application (`DualWanBalancer.exe`) built via `DualWanApp/build.ps1`.
- Floating bottom-right live **Speed & Ping Bar** (`SpeedBarForm`) with real-time download, upload, per-adapter stats, ping indicator, drag support, and click-to-open panel.
- System Tray (`NotifyIcon`) integration with dynamic mode icons, single-instance wakeup (`Mutex` + `EventWaitHandle`), `--background` startup flag, and Windows Startup (`HKCU\...\Run`) prompt & toggle.
- Pure C# async `DualWanProxyEngine` (HTTP/HTTPS `CONNECT` + SOCKS5) with automatic fallback ports (`8080..8090` / `1080..1090`), **Sticky Host Affinity**, **Lowest Ping**, **Least-Busy**, and **Weighted Round-Robin** strategies.
- Dedicated **`Game Split (Game->ETH | DL->WIFI)`** mode separating direct game sockets (Ethernet Metric `10`) from browser/download proxy traffic (Wi-Fi).
- `LatencyMonitor` using Win32 source-bound `IcmpSendEcho2Ex` with `TCP:443` fallback for live Ping, Jitter, and Packet Loss tracking per link.
- `GameDetector` supporting 55+ built-in PC games, custom `.exe` lists, Fullscreen + UDP heuristic detection (`GetExtendedUdpTable`), and optional `AboveNormal` priority boost.
- `NetworkOptimizer` for reversible Administrator Windows latency tweaks (`TcpAckFrequency`, `TCPNoDelay`, `NetworkThrottlingIndex`, `SystemResponsiveness`).
- Live `Direct / Game OS Route` indicator (`GetBestInterface`).
- Complete GitHub documentation, CI workflow, issue templates, and MIT License attributed to **Omkar Waghmare**.

## [1.0.0] - 2026-11-27
### Added
- Initial PowerShell Dual-WAN manager (`dual-wan-manager.ps1`) with `Status`, `Proxy`, `EqualMetrics`, and `Restore` modes.
- Zero-dependency Node.js HTTP/HTTPS `CONNECT` + SOCKS5 Dual-WAN load-balancing proxy (`dual-wan-proxy.js`).
