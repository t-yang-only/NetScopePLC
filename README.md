<p align="center">
  <img src="docs/social-preview.png" width="100%" alt="NetScope PLC — 工业设备网络发现与地址配置">
</p>

<p align="center">
  <strong>工业设备网络发现与地址配置</strong><br>
  Windows 本机工具：WPF 图形界面 + C 语言 ICMP/ARP 扫描核心，快速发现现场 PLC / HMI / 工控设备并一键改址。
</p>

<p align="center">
  <img alt="Platform" src="https://img.shields.io/badge/platform-Windows-0078D4?logo=windows&logoColor=white">
  <img alt=".NET" src="https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white">
  <img alt="UI" src="https://img.shields.io/badge/UI-WPF-188657">
  <img alt="Native" src="https://img.shields.io/badge/scan_core-C%20%2B%20WinSock-555555">
  <img alt="Release" src="https://img.shields.io/github/v/release/t-yang-only/NetScopePLC?label=release">
  <img alt="License" src="https://img.shields.io/badge/license-MIT-green">
</p>

<p align="center">
  <a href="https://github.com/t-yang-only/NetScopePLC/releases/latest"><strong>下载最新版</strong></a> ·
  <a href="CONTRIBUTING.md">贡献指南</a> ·
  <a href="SECURITY.md">安全策略</a> ·
  <a href="LICENSE">MIT License</a>
</p>

---

## 下载与运行

| 方式 | 说明 |
|------|------|
| **发布包（推荐）** | 在 [Releases](https://github.com/t-yang-only/NetScopePLC/releases/latest) 下载 `NetScopePLC.exe`，**以管理员身份运行** |
| **本地构建** | 克隆仓库后执行 `build.bat`，产物在 `publish\NetScopePLC.exe` |
| **开发调试** | `dev-run.bat`（Debug 构建 + 管理员启动） |

发布包为 **单文件自包含**（`win-x64`），内嵌 C 扫描核心，无需额外安装 .NET 运行时。程序清单会触发 UAC，改 IP / 扫未知网段需要管理员权限。

## 能做什么

| 能力 | 说明 |
|------|------|
| 网卡枚举 | 列出本机接口；未配置 IPv4 的网卡也会显示 |
| 三种扫描 | 本网段 · 常见内网段 · 手工 CIDR |
| 临时改址 | 扫未知网段时临时设 IP，结束后自动恢复静态地址或 DHCP |
| 双通道发现 | ICMP 应答 + 所选接口 ARP 邻居；不 Ping 但回 ARP 的设备也能进结果 |
| 离线识别库 | 内置 IEEE 注册的 5 万余条 MAC 前缀（MA-L / MA-M / MA-S）与主机名/厂商规则表，**不联网也能识别厂商与设备类型**；随机/隐私 MAC 会明确标注 |
| 协议识别 | S7 / Modbus / EtherNet/IP / OPC UA 端口指纹，以及反向 DNS 主机名（后台补全，不拖慢扫描） |
| 设备分类 | 扫描目标可选 **PLC / HMI** 或 **其他设备**，识别逻辑不同 |
| 现场改址 | 选中设备后一键把本机配到同网段，或恢复 DHCP |
| 扫描控制 | 暂停 / 继续 / 停止 |
| 命令行 | 无界面批处理：`--adapters`、`--scan`（见下文） |

## 协议指纹

| 协议 | 端口 |
|------|------|
| Siemens S7 / ISO-TSAP | `102` |
| Modbus TCP | `502` |
| EtherNet/IP | `44818` |
| OPC UA | `4840` |

## 命令行

```bat
NetScopePLC.exe --help
NetScopePLC.exe --adapters
NetScopePLC.exe --scan 192.168.1.250 24
```

输出协议与原生核心一致（UTF-8，制表符分隔）：

```text
HOST    <ip>    <rtt>
ARP     <ip>    <mac>
DONE    <scanned>    <replied>
```

## 架构

```
NetScopePLC.exe          WPF 界面 · 网卡 / 扫描编排 · netsh 改址 · 协议识别 · CLI
        │
        ▼  内嵌资源提取 / 子进程调用
NetScopeNative.exe       C 核心 · 绑定源地址 ICMP · ARP 邻居导出
```

源码结构（保持精简，无额外框架）：

| 文件 | 职责 |
|------|------|
| `MainWindow.xaml(.cs)` | UI 编排、扫描、识别、改址 |
| `Program.cs` / `CliRunner.cs` | 入口、管理员提权、CLI |
| `NativeToolHost.cs` | 内嵌/旁路调用原生扫描核心 |
| `PlcFingerprint.cs` / `DeviceFingerprint.cs` | PLC/HMI 与其他设备识别 |
| `OfflineDb.cs` | 离线识别库：OUI 厂商表 + 主机名/厂商规则 |
| `data/oui.tsv`、`data/identify.tsv` | 离线识别数据（内嵌进 exe，由 `tools\make-oui.ps1` 生成） |
| `netscope_native.c` | ICMP 洪泛 + ARP 导出 |

## 构建

```bat
build.bat        :: 发布到 publish\
build.bat run    :: 构建后以管理员启动
```

依赖：

- Visual Studio 18 Enterprise（C++ 工具链，`vcvars64`）
- .NET 10 SDK

`tools\make-ico.ps1` 在构建时从 `app-icon.png` 生成圆角 `app.ico`。
`tools\make-oui.ps1` 从 IEEE 注册表重新生成 `data\oui.tsv`（产物已入库，日常构建不需要跑）。

## 更新记录

### 0.11

- **内置离线识别库**：把 IEEE 注册的 MAC 前缀表（MA-L / MA-M / MA-S，53 964 条）与主机名/厂商规则表编译进 exe，识别设备不再依赖联网查询。
- 设备型号优先取「协议指纹 → 工业 OUI → 离线厂商库」，认不出来的才落到「网络设备」；随机/隐私 MAC（本地管理位置 1）会标注「随机 MAC（隐私地址）」，不再静默留空。
- 反向 DNS 主机名改为**后台补全**：本机路由器 PTR 反查实测要 17.5 秒，之前 1.2 秒的通用探针超时让它永远超时、主机名形同废设；现在设备先出行，名字回来后再回填型号。
- PLC/HMI 模式不再把开了 80 端口的路由器/交换机一律叫「工业 HMI / 触摸屏」，先查离线厂商库。

### 0.10

- WPF 界面改版，新增 CLI（`--adapters` / `--scan`），C 扫描核心改为内嵌资源。

## 注意事项

- 临时改址期间网络会短暂中断；未知网段扫描结束后会在 `finally` 中恢复原配置。
- 仅禁用 ICMP 且不回应 ARP 的设备可能扫不到，需再加对应厂商协议探测。
- **仅在你有权管理的工业网络上使用。**

## 仓库说明

| 路径 | 用途 |
|------|------|
| `publish/` | 发布产物（`build.bat` 生成，不入库） |
| `bin/`、`obj/` | 临时构建输出 |
| `docs/` | 社交预览图 `social-preview.png` |
| `tools/` | 图标生成、窗口截图等构建辅助脚本 |
| `README.txt` | 现场操作速查（纯文本） |

## 参与贡献

欢迎 Issue / PR。请先阅读 [CONTRIBUTING.md](CONTRIBUTING.md) 与 [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)。
