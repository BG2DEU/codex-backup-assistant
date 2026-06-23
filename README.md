# Codex 一键备份与换机迁移工具

本项目用于将 Codex 本地状态、项目工作树、Git 历史、可读对话和开发环境清单导出到外部介质，并在新电脑上完成可校验、可回滚的恢复。

## 项目说明

这是一个 Codex 换机迁移工具的测试版，用于验证“旧电脑一键导出、新电脑校验恢复”的实际可行性。

本项目由作者提出需求、确认流程和测试目标，并在作者没有软件编程背景的情况下，通过 Codex 协助完成需求整理、技术设计、代码实现、测试和打包。作者目前没有第二台新电脑用于完整换机实测，因此将项目上传到 GitHub，希望获得愿意帮忙的测试者反馈。

如果你愿意协助测试，请优先阅读 [测试反馈说明](./docs/测试反馈说明.md)。请不要公开上传真实备份包、完整日志或包含私人路径、项目名、账号信息的截图。

## 当前阶段

当前版本：`1.0.0-preview.1`。

阶段 A（发现与风险验证）、阶段 B（可验证导出 MVP）和阶段 C（安全恢复 MVP）已完成。项目发现使用 Codex 会话路径作为主要来源，再使用只读项目标志扫描补漏，不读取源码内容；桌面应用已具备导出、校验、恢复、回滚、恢复报告和新电脑操作说明入口。

## 文档

- [项目需求手册](./项目需求手册.md)
- [技术方案](./docs/技术方案.md)
- [阶段 A 验收记录](./docs/阶段A验收记录.md)
- [阶段 B 实施记录](./docs/阶段B实施记录.md)
- [阶段 C 实施记录](./docs/阶段C实施记录.md)
- [用户操作脚本](./docs/用户操作脚本.md)
- [测试反馈说明](./docs/测试反馈说明.md)
- [故障代码与日志说明](./docs/故障代码与日志说明.md)
- [最终版发布说明](./docs/最终版发布说明.md)

## 正式解决方案

```powershell
dotnet build .\CodexBackup.slnx --configuration Release
dotnet test .\CodexBackup.slnx --configuration Release
```

解决方案当前包含 WPF 桌面应用、核心领域模型、Windows 基础设施库、只读诊断入口和三个自动测试项目。桌面首页已支持项目发现、Git/体量风险、Codex 顶层数据分类、凭据与临时数据强制排除、Codex/SQLite 使用状态阻止、统一备份计划、可验证导出、备份包校验和安全恢复。导出采用临时目录、流式复制、目标介质重读 SHA-256、未完成标记和成功后提交；Codex 会话会同时保存原始 JSONL、脱敏通用 JSON、Markdown 和项目关联索引。恢复前会完整校验备份包，恢复后会校验目标文件，并在覆盖类操作前建立回滚副本。新电脑恢复的前提是 Codex 已安装并登录过，恢复前需要完全退出 Codex。恢复完成后，桌面界面会显示恢复摘要、HTML 报告路径，并提供“打开恢复报告”和“打开日志目录”按钮。当前自动测试包含移动硬盘结构端到端演练、桌面校验/恢复命令流程和新电脑说明入口，共 64 项通过。

## 只读发现探针

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Invoke-DiscoveryProbe.ps1 `
  -ProjectRoots "$HOME\Documents", "$HOME\Desktop" `
  -OutputPath "$env:TEMP\codex-discovery.json"
```

报告可能包含本机路径和项目名称，不应直接上传或公开分享。

## 烟雾测试

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\DiscoveryProbe.Smoke.ps1
```
