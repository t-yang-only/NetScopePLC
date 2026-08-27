# Project Instructions

## Tech Stack
- UI: C# / WPF on `net10.0-windows`, self-contained single-file publish
- Scan core: C (`netscope_native.c`) → `NetScopeNative.exe` (iphlpapi + winsock)
- Build: `build.bat` (VS 18 Enterprise `vcvars64` + `dotnet publish`)
- Runtime: both EXEs must sit in the same directory; admin required for netsh IP changes

## Code Style
- C# files: PascalCase (`MainWindow.xaml.cs`, `App.xaml.cs`)
- Native: snake_case (`netscope_native.c`, `scan_worker`, `output_arp_hosts`)
- UI models as sealed records: `Adapter`, `Device`
- Prefer async/await for process I/O; keep UI state flags (`_scanActive`, `_paused`, `_stopRequested`) in the window
- Chinese user-facing strings in UI/status; English CLI args (`--scan`, `--adapters`)

## Testing
- No test project or runner configured
- Manual check: run as admin, scan a known subnet, confirm HOST/ARP rows and restore DHCP/static after unknown-segment mode

## Build & Run
- Build: `build.bat`
- Run: `NetScopePLC.exe` (UAC elevates if needed)
- Publish output also under `publish/`

## Project Structure
- `App.xaml(.cs)` — admin gate + theme resources
- `MainWindow.xaml(.cs)` — all UI orchestration (adapters, scan modes, identify, netsh)
- `netscope_native.c` — bound-source ICMP flood + ARP neighbor dump
- `NetScopePLC.csproj` — copies `NetScopeNative.exe` beside the WPF app
- `README.txt` — operator notes (r6)

## Conventions
- Git history unavailable (no commits on `main` yet)
- Native stdout protocol (UTF-8, tab-separated): `HOST\tip\trtt`, `ARP\tip\tmac`, `DONE\tscanned\treplied`
- Temporary IP changes must restore original static or DHCP in `finally`
- Protocol fingerprint ports: 102 (S7), 502 (Modbus), 44818 (EtherNet/IP), 4840 (OPC UA)
- Do not add layers/frameworks without need — this is intentionally a two-file monolith
