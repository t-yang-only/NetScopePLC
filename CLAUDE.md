# Project Instructions

## Tech Stack
- UI: C# / WPF on `net10.0-windows`, self-contained single-file publish
- Scan core: C (`netscope_native.c`) → `NetScopeNative.exe` (embedded resource + dev sidecar)
- Build: `build.bat` (VS 18 Enterprise `vcvars64` + `dotnet publish`)
- Runtime: admin required for netsh IP changes; release is a single `NetScopePLC.exe`

## Code Style
- C# files: PascalCase (`MainWindow.xaml.cs`, `CliRunner.cs`)
- Native: snake_case (`netscope_native.c`, `scan_worker`, `output_arp_hosts`)
- UI models as sealed records: `Adapter`, `Device`
- Prefer async/await for process I/O; keep UI state flags (`_scanActive`, `_paused`, `_stopRequested`) in the window
- Chinese user-facing strings in UI/status; English CLI args (`--scan`, `--adapters`)

## Testing
- No test project or runner configured
- Manual check: run as admin, scan a known subnet, confirm HOST/ARP rows and restore DHCP/static after unknown-segment mode

## Build & Run
- Dev (auto): `watch-dev.ps1` — saves to `.cs`/`.xaml`/`.c` rebuild + admin launch via `dev-run.bat`
- Manual dev: `dev-run.bat` (Debug build + admin run)
- Release: `build.bat` → `publish/NetScopePLC.exe`; optional `build.bat run` to launch
- F5 debug attaches without UAC; netsh/IP changes need `dev-run.bat` or published exe

## Project Structure
- `App.xaml(.cs)` — theme resources
- `MainWindow.xaml(.cs)` — UI orchestration (adapters, scan modes, identify, netsh)
- `Program.cs` / `CliRunner.cs` — entry, admin elevation, CLI
- `NativeToolHost.cs` — extract embedded `NetScopeNative.exe` for scan subprocess
- `PlcFingerprint.cs` / `DeviceFingerprint.cs` / `SocketProbe.cs` — protocol identify
- `netscope_native.c` — bound-source ICMP flood + ARP neighbor dump
- `tools/` — `make-ico.ps1`, `capture-window.ps1`
- `docs/` — `social-preview.png`
- `README.md` — GitHub docs; `README.txt` — operator quick reference

## Conventions
- Native stdout protocol (UTF-8, tab-separated): `HOST\tip\trtt`, `ARP\tip\tmac`, `DONE\tscanned\treplied`
- Temporary IP changes must restore original static or DHCP in `finally`
- Protocol fingerprint ports: 102 (S7), 502 (Modbus), 44818 (EtherNet/IP), 4840 (OPC UA)
- Do not add layers/frameworks without need — intentionally a small monolith
