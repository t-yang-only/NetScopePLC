NetScope PLC 0.10
=================

工业设备网络发现与地址配置 · Windows 本机工具

下载
----
GitHub Releases: https://github.com/t-yang-only/NetScopePLC/releases/latest
下载 NetScopePLC.exe，以管理员身份运行。

运行
----
  发布包: publish\NetScopePLC.exe（build.bat 生成）
  开发调试: dev-run.bat

单文件自包含，内嵌 C 扫描核心，无需安装 .NET 运行时。

命令行
------
  NetScopePLC.exe --help
  NetScopePLC.exe --adapters
  NetScopePLC.exe --scan 192.168.1.250 24

功能摘要
--------
  · 枚举网卡（含未配置 IPv4 的接口）
  · 扫描：本网段 / 常见内网段 / 手工 CIDR
  · 未知网段临时改址，结束后自动恢复
  · ICMP + ARP 双通道发现
  · 识别 S7(102) / Modbus(502) / EtherNet/IP(44818) / OPC UA(4840)
  · 扫描目标：PLC·HMI 或 其他设备
  · 选中设备后配置同网段静态 IP 或恢复 DHCP
  · 暂停 / 停止扫描

构建
----
  build.bat          发布到 publish\
  build.bat run      构建后管理员启动

需要 VS 18 Enterprise（C++）与 .NET 10 SDK。

注意
----
  改 IP 需管理员权限。仅在你有权管理的网络上使用。
  临时改址会短暂中断连接；扫描结束会自动恢复原配置。
