# 安全策略

## 支持的版本

| 版本 | 支持状态 |
|------|----------|
| `main` 分支最新代码 | ✅ 接受安全报告 |
| 历史发布 / 旧提交 | ❌ 仅酌情修复 |

## 报告漏洞

请**不要**通过公开 Issue、讨论区或 Pull Request 披露安全问题。

推荐方式（任选其一）：

1. 使用 GitHub 私密报告：  
   [Open a draft security advisory](https://github.com/t-yang-only/NetScopePLC/security/advisories/new)
2. 通过仓库维护者私信联系：[@t-yang-only](https://github.com/t-yang-only)

报告中请尽量包含：

- 影响范围（UI、原生扫描核心、临时改址 / netsh 等）
- 复现步骤与环境（Windows 版本、是否管理员、网卡/网段）
- 预期影响（权限提升、网络中断、意外改址无法恢复等）
- 若有，附上最小复现或日志（勿包含真实生产网段敏感信息）

我们会尽快确认并给出处理计划。修复发布前，请勿公开细节。

## 使用注意

本工具会枚举网卡、发送 ICMP、读取 ARP，并在未知网段模式下**临时修改本机 IP**。请仅在你有权管理的网络上使用，并确保扫描结束后配置已正确恢复。
