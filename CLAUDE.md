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
- `OfflineDb.cs` — offline identify library (IEEE OUI vendor table + host/vendor rules), loaded lazily from embedded resources
- `data/oui.tsv`, `data/identify.tsv` — offline identify data, embedded into the exe; regenerate with `tools/make-oui.ps1`
- `netscope_native.c` — bound-source ICMP flood + ARP neighbor dump
- `tools/` — `make-ico.ps1`, `make-oui.ps1`, `capture-window.ps1`
- `docs/` — `social-preview.png`
- `README.md` — GitHub docs; `README.txt` — operator quick reference

## Conventions
- Native stdout protocol (UTF-8, tab-separated): `HOST\tip\trtt`, `ARP\tip\tmac`, `DONE\tscanned\treplied`
- Temporary IP changes must restore original static or DHCP in `finally`
- Protocol fingerprint ports: 102 (S7), 502 (Modbus), 44818 (EtherNet/IP), 4840 (OPC UA)
- Identify priority: protocol fingerprint → industrial OUI (`PlcFingerprint`) → offline vendor DB (`OfflineDb`); an open port alone never proves a model
- `data/identify.tsv` vendor rules match on word boundaries — substring matching makes "Chengdu Quanjing **Intel**ligent" hit Intel
- Reverse-DNS hostnames resolve in the background (routers here answer PTR in ~17.5 s) — never put a DNS lookup on the scan path
- Keep the native thread pool below `MAXIMUM_WAIT_OBJECTS` (64); this project uses 60
- Version lives in `<Version>` in the csproj; the window title in `MainWindow.xaml` must match it
- Do not add layers/frameworks without need — intentionally a small monolith
