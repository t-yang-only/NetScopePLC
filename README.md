# NetScopePLC

工业网段扫描工具：WPF 图形界面 + 原生 ICMP/ARP 扫描核心，用于发现并识别现场 PLC / 工控设备。

## 功能

- 枚举本机网卡（含未配置 IPv4 的接口）
- 三种扫描模式：本网段、常见内网段、手工网段
- 未知网段扫描时临时改址并在结束后恢复静态地址或 DHCP
- 同时采集 ICMP 与所选接口的 ARP 邻居（不回应 Ping 但回应 ARP 的设备也能进结果）
- 命中后识别：反向 DNS、S7/ISO(102)、Modbus TCP(502)、EtherNet/IP(44818)、OPC UA(4840)
- 支持暂停 / 停止、配置同网段静态 IPv4、恢复 DHCP

## 运行

1. 以管理员运行 `NetScopePLC.exe`（清单会触发 UAC）
2. `NetScopePLC.exe` 与 `NetScopeNative.exe` **必须在同一目录**

仓库根目录已包含构建所需的 `NetScopeNative.exe`。完整自包含发布包请本地执行 `build.bat`，输出在 `publish/`。

## 构建

```bat
build.bat
```

需要：

- Visual Studio 18 Enterprise（含 C++ 工具链，`vcvars64`）
- .NET 10 SDK

## 注意

网络配置会短暂中断。仅禁用 ICMP 且不回应 ARP 的设备可能扫不到，需再加对应厂商协议探测。
