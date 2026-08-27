NetScope PLC

运行 NetScopePLC.exe。程序通过清单请求管理员权限。

组件：NetScopePLC.exe 是自包含 WPF 图形界面；NetScopeNative.exe 是 C 语言原生网络核心。两个 EXE 必须放在同一目录。

功能：枚举所有网卡；没有 IPv4 的接口显示“未配置 IPv4”，不会被过滤。界面提供三种模式：扫描本网段 IPv4 范围、扫描常见内网段、扫描手工网段。未知网段扫描会保存原配置，逐段设置临时地址，等待地址在 Windows 中进入可用状态，再绑定该地址扫描，最后恢复原静态地址或 DHCP。C 核心同时采集 ICMP 和所选接口的 ARP 邻居，因此不回应 Ping 但回应 ARP 的 PLC 也能进入结果。命中设备后界面继续做反向 DNS、S7/ISO(102)、Modbus TCP(502)、EtherNet/IP(44818)、OPC UA(4840) 识别。支持暂停、停止、选择设备后配置同网段静态 IPv4，以及恢复 DHCP。

构建：运行 build.bat。需要 Visual Studio 18 Enterprise 的 C++ 工具链及 .NET 10 SDK。

网络配置会短暂中断连接。仅响应 ICMP 的设备会出现在结果中；禁用 ICMP 的 PLC 需要增加对应厂商协议探测。
