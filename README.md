# Codex 换机助手

[![Release](https://img.shields.io/github/v/release/BG2DEU/codex-backup-assistant?include_prereleases&label=release)](https://github.com/BG2DEU/codex-backup-assistant/releases)
![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D4)
![Architecture](https://img.shields.io/badge/architecture-x64-555555)
![Status](https://img.shields.io/badge/status-preview-orange)
![Tests](https://img.shields.io/badge/automated%20tests-64%20passed-brightgreen)

一个面向 Windows 用户的 Codex 本地备份与换机迁移工具。它尝试自动发现 Codex 数据和相关项目，将项目、Git 历史、可读对话、规则、Skills、插件及安全配置导出到移动硬盘，并在新电脑上完成校验、恢复和结果报告。

> [!WARNING]
> 当前版本是公开预览测试版，尚未完成真实双电脑换机验证。首次测试请使用非关键数据，并保留独立备份。

**[下载当前测试版](https://github.com/BG2DEU/codex-backup-assistant/releases/tag/v1.0.0-preview.1)** ·
**[提交测试反馈](https://github.com/BG2DEU/codex-backup-assistant/issues/new?template=test-report.yml)** ·
**[查看操作说明](./docs/用户操作脚本.md)**

## 为什么制作它

Codex 使用时间变长后，项目可能分布在不同磁盘和目录中；对话、规则、Skills、插件及配置也不一定都和项目放在一起。只复制某一个文件夹，容易遗漏没有推送到 GitHub 的源码、未提交修改、数据库、模型文件或 Codex 本地数据。

本项目希望让不熟悉目录结构、Git 和数据库的普通用户，也能通过桌面界面完成：

1. 扫描旧电脑。
2. 确认需要迁移的项目和 Codex 数据。
3. 导出到移动硬盘或指定文件夹。
4. 在新电脑校验备份包。
5. 恢复项目和可安全迁移的 Codex 内容。
6. 通过报告、日志和故障代码判断结果。

```mermaid
flowchart LR
    A["旧电脑扫描"] --> B["生成备份计划"]
    B --> C["导出到移动硬盘"]
    C --> D["SHA-256 完整校验"]
    D --> E["新电脑选择备份包"]
    E --> F["安全恢复与回滚保护"]
    F --> G["恢复报告与故障日志"]
```

## 当前能力

| 能力 | 当前状态 |
| --- | --- |
| 自动定位 Codex 数据目录 | 已实现 |
| 从 Codex 会话路径发现相关项目 | 已实现 |
| 轻量扫描项目标志进行补漏 | 已实现 |
| 备份完整项目工作树和 `.git` 历史 | 已实现 |
| 备份可读对话、规则、Skills 和自定义插件 | 已实现 |
| 生成原生快照与通用对话导出 | 已实现 |
| 强制排除登录令牌、机器标识和临时缓存 | 已实现 |
| 导出后从目标介质重新读取并校验 SHA-256 | 已实现 |
| 新电脑恢复前完整校验备份包 | 已实现 |
| 同名项目默认另存，不直接覆盖 | 已实现 |
| 覆盖类操作前创建回滚副本 | 已实现 |
| JSON/HTML 报告、日志和故障代码 | 已实现 |
| 真实双电脑完整换机测试 | **等待志愿测试** |
| 不同 Codex 版本兼容性验证 | **仍需扩充** |

## 会备份什么

- 项目源码、文档、资源、数据库和未跟踪文件。
- Git 仓库、分支、标签、本地提交和未提交修改。
- Codex 会话与已归档会话的原始快照。
- 脱敏后的通用 JSON、Markdown 对话和项目关联索引。
- `AGENTS.md`、Rules、Skills、个人配置和自定义插件。
- 导出清单、文件哈希、风险提示和恢复工具。

实际包含内容以软件扫描结果和最终备份计划为准。导出前应由用户检查并确认。

## 不会迁移什么

- Codex 登录令牌、浏览器 Cookie 和登录会话。
- 机器标识、系统凭据和 sandbox secrets。
- 可以重新生成的缓存、临时文件和运行时锁文件。
- 第三方服务的授权状态；MCP、Connector 或插件可能需要重新登录和授权。
- Windows 系统、已安装软件本体及完整开发环境。

这个工具不是整机镜像软件，也不能替代独立硬盘备份或云端 Git 仓库。

## 快速开始

### 旧电脑导出

1. 从 [Releases](https://github.com/BG2DEU/codex-backup-assistant/releases) 下载 ZIP。
2. 对照同一 Release 中的 `SHA256.txt` 校验 ZIP。
3. 解压后运行 `CodexBackupAssistant.exe`。
4. 扫描并检查项目、Codex 数据和风险提示。
5. 生成备份计划，选择移动硬盘或其他目标目录并导出。
6. 等待软件完成目标介质重读校验。

### 新电脑恢复

1. 安装 Codex，并至少启动、登录一次。
2. 完全退出 Codex。
3. 从备份包内启动 `tools/Codex换机助手.exe`。
4. 先选择“校验备份包”。
5. 校验通过后选择项目恢复目录并执行恢复。
6. 首次测试建议不要恢复 Codex 原生状态。
7. 完成后查看恢复报告和日志。

详细步骤见 [用户操作脚本](./docs/用户操作脚本.md)。

## 下载与运行说明

- 当前版本：`1.0.0-preview.1`
- 支持系统：Windows 10/11 x64
- 发布形式：自包含单文件程序，不需要单独安装 .NET
- 数字签名：当前预览版尚未进行代码签名

由于程序尚未签名，Windows SmartScreen 可能显示未知发布者警告。请只从本仓库的 [Releases](https://github.com/BG2DEU/codex-backup-assistant/releases) 下载，并使用 Release 中的 `SHA256.txt` 检查文件完整性。不要从第三方网盘或转载链接下载。

## 安全与隐私

- 扫描和导出不会修改源项目，也不会自动执行 Git 提交、清理或重置。
- 凭据、令牌、Cookie、机器标识和已知敏感运行数据默认排除。
- 备份包可能包含私人源码、对话、路径和配置，应只保存在可信介质中。
- 不要把真实备份包、完整日志或未脱敏截图上传到 GitHub。
- 提交问题前，请删除用户名、完整路径、项目名、仓库地址、令牌和密钥。

安全问题和隐私风险请参考 [安全反馈说明](./SECURITY.md)。

## 志愿测试反馈

目前最需要的是 Windows 10/11、不同 Codex 版本、移动硬盘/U 盘和双电脑环境下的实际测试。

没有第二台电脑也可以测试：

- 软件能否正常启动。
- 是否能发现预期项目和 Codex 数据。
- 是否能成功导出并生成报告。
- 备份包是否能通过独立校验。

有第二台电脑或虚拟机可以继续测试完整恢复流程。请使用 GitHub 的
[测试反馈表](https://github.com/BG2DEU/codex-backup-assistant/issues/new?template=test-report.yml)，并先阅读
[测试反馈说明](./docs/测试反馈说明.md)。

## 当前验证状态

- `dotnet format CodexBackup.slnx --verify-no-changes --no-restore`：通过
- `dotnet build CodexBackup.slnx -c Release`：通过
- `dotnet test CodexBackup.slnx -c Release --no-build`：64 项通过
- Windows x64 单文件发布：通过
- 本机桌面启动烟雾测试：通过
- 独立新电脑完整迁移：尚未完成

自动测试通过不等于真实换机已经验证成功，这也是发布预览版并征集测试者的原因。

## 从源码构建

环境要求：

- Windows 10/11
- .NET SDK 10

```powershell
dotnet restore .\CodexBackup.slnx
dotnet build .\CodexBackup.slnx --configuration Release
dotnet test .\CodexBackup.slnx --configuration Release
```

生成自包含桌面发布包：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File .\tools\Publish-DesktopRelease.ps1
```

## 项目结构

```text
src/
├─ CodexBackup.App/                    WPF 桌面界面
├─ CodexBackup.Core/                   备份、清单和恢复领域模型
└─ CodexBackup.Infrastructure.Windows/ Windows 扫描、导出和恢复实现

tests/                                 自动测试
tools/                                 诊断探针和发布脚本
docs/                                  需求、设计、操作与测试文档
```

## 路线图

- 完成真实双电脑和虚拟机换机验证。
- 扩充不同 Codex 版本和 Windows 环境兼容性测试。
- 改进大规模项目、超大文件和移动介质故障处理。
- 增加可选加密、增量快照和备份历史。
- 完善界面、代码签名和稳定版发布流程。

## 文档

- [项目需求手册](./项目需求手册.md)
- [技术方案](./docs/技术方案.md)
- [用户操作脚本](./docs/用户操作脚本.md)
- [测试反馈说明](./docs/测试反馈说明.md)
- [故障代码与日志说明](./docs/故障代码与日志说明.md)
- [发布说明](./docs/最终版发布说明.md)
- [阶段 A 验收记录](./docs/阶段A验收记录.md)
- [阶段 B 实施记录](./docs/阶段B实施记录.md)
- [阶段 C 实施记录](./docs/阶段C实施记录.md)

## 项目来源

本项目由作者提出实际换机需求、确认使用流程和测试目标，并在作者没有软件编程背景的情况下，通过 Codex 协助完成需求整理、技术设计、代码实现、自动测试和发布。

作者目前没有第二台电脑完成完整换机实测，因此公开源代码和测试版，希望获得志愿测试者的验证与反馈。项目与 OpenAI 没有官方隶属或背书关系。
