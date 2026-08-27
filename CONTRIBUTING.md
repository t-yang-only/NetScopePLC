# 贡献指南

感谢关注 **NetScope PLC**。本仓库是刻意保持精简的两文件单体（WPF UI + C 扫描核心），贡献前请先阅读本节。

## 开始之前

- 仅在你有权管理的网络上测试扫描与改址功能。
- 需要管理员权限才能改 IP；临时改址结束后必须恢复原静态地址或 DHCP。
- 本地构建依赖：Visual Studio 18 Enterprise（含 C++ / `vcvars64`）与 .NET 10 SDK。
- 构建：运行仓库根目录的 `build.bat`。`NetScopePLC.exe` 与 `NetScopeNative.exe` 必须同目录。

## 开发约定

| 区域 | 约定 |
|------|------|
| C# | PascalCase；UI 编排集中在 `MainWindow.xaml(.cs)` |
| 原生 C | snake_case（如 `scan_worker`、`output_arp_hosts`） |
| UI 模型 | sealed record：`Adapter`、`Device` |
| 面向用户文案 | 中文 |
| CLI / 协议字段 | 英文（如 `--scan`、`HOST` / `ARP` / `DONE`） |
| 原生 stdout | UTF-8，制表符分隔：`HOST\tip\trtt`、`ARP\tip\tmac`、`DONE\tscanned\treplied` |

请勿无必要引入新框架或额外抽象层。

## 协议指纹端口

- Siemens S7 / ISO-TSAP：`102`
- Modbus TCP：`502`
- EtherNet/IP：`44818`
- OPC UA：`4840`

## 提交流程

1. Fork 本仓库并创建功能分支。
2. 本地用 `build.bat` 验证构建；涉及扫描时手动确认 HOST/ARP 行与改址恢复。
3. 按 [Issue 模板](.github/ISSUE_TEMPLATE/) 或 [PR 模板](.github/pull_request_template.md) 说明动机与验证步骤。
4. 保持改动聚焦：一次 PR 只解决一个问题。

## 行为准则

参与本项目即表示你同意遵守 [Code of Conduct](CODE_OF_CONDUCT.md)。

## 安全问题

请勿在公开 Issue 中报告安全漏洞。参见 [SECURITY.md](SECURITY.md)。

## 问题与讨论

- Bug / 功能请求：用仓库 Issue 模板提交。
- 不确定是否该改：先开 Issue 讨论范围。
