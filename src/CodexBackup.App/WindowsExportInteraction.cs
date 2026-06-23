using System.Windows;
using System.IO;
using CodexBackup.App.Presentation;
using Microsoft.Win32;

namespace CodexBackup.App;

public sealed class WindowsExportInteraction(Window owner) : IExportInteraction
{
    public string? SelectDestination()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择备份包保存位置",
            Multiselect = false,
        };
        return dialog.ShowDialog(owner) == true ? dialog.FolderName : null;
    }

    public bool ConfirmExport(
        int projectCount,
        int codexItemCount,
        long estimatedBytes,
        int secretRiskItemCount)
    {
        var riskText = secretRiskItemCount > 0
            ? $"\n其中 {secretRiskItemCount} 个已选项可能含私人信息，请确认目标介质可信。"
            : string.Empty;
        var message = $"即将导出 {projectCount} 个项目和 {codexItemCount} 个 Codex 数据项，" +
                      $"预计 {ProjectListItem.FormatBytes(estimatedBytes)}。" +
                      riskText +
                      "\n\n会话将同时保存原始快照、通用 JSON 和 Markdown；登录凭据已强制排除。是否继续？";
        return MessageBox.Show(
                   owner,
                   message,
                   "确认备份导出",
                   MessageBoxButton.YesNo,
                   MessageBoxImage.Warning,
                   MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    public string? SelectBackupPackage()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择备份包根目录（包含 manifest.json）",
            Multiselect = false,
            InitialDirectory = FindContainingBackupPackage(),
        };
        return dialog.ShowDialog(owner) == true ? dialog.FolderName : null;
    }

    public string? SelectProjectRestoreRoot()
    {
        var suggested = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Codex恢复项目");
        var dialog = new OpenFolderDialog
        {
            Title = "选择项目恢复到哪个文件夹",
            Multiselect = false,
            InitialDirectory = Directory.Exists(suggested)
                ? suggested
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        return dialog.ShowDialog(owner) == true ? dialog.FolderName : null;
    }

    public bool ConfirmNativeCodexRestore() =>
        MessageBox.Show(
            owner,
            "是否尝试恢复 Codex 原生会话和状态？\n\n" +
            "推荐选择“否”：项目、规则、技能、插件和通用对话仍会恢复，风险更低。\n\n" +
            "只有新电脑已经安装、启动并登录 Codex，随后完全退出，而且适配器版本一致时才选择“是”。",
            "Codex 原生状态恢复",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;

    public bool ConfirmRestore(
        string packagePath,
        string projectRestoreRoot,
        bool restoreNativeCodexState)
    {
        var nativeText = restoreNativeCodexState
            ? "将尝试恢复兼容的 Codex 原生状态，并在修改前建立回滚副本。"
            : "不会恢复 Codex 原生状态；仍会恢复安全配置和通用对话。";
        return MessageBox.Show(
                   owner,
                   $"备份包：{packagePath}\n\n" +
                   $"项目恢复位置：{projectRestoreRoot}\n\n" +
                   "已有项目默认另存，不会覆盖。\n" +
                   "已有 Codex 配置默认保留，只补充缺失文件。\n" +
                   $"{nativeText}\n\n是否开始？",
                   "确认恢复",
                   MessageBoxButton.YesNo,
                   MessageBoxImage.Question,
                   MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    public void ShowNewComputerGuide()
    {
        const string message =
            "新电脑恢复前提：\n\n" +
            "1. 新电脑已经安装 Codex。\n" +
            "2. 已经至少启动并登录过一次 Codex，让新电脑生成自己的本机环境。\n" +
            "3. 恢复前请完全退出 Codex，避免数据库仍在使用。\n" +
            "4. 插入移动硬盘，打开备份包里的“Codex换机助手.exe”，或打开桌面预览版。\n" +
            "5. 先点“校验备份包”，确认备份完整。\n" +
            "6. 再点“选择备份并恢复”，选择备份包和项目恢复目录。\n" +
            "7. 默认不要恢复 Codex 原生状态；项目、通用对话、安全配置仍会恢复。\n" +
            "8. 恢复完成后查看底部摘要，并点“打开恢复报告”。\n\n" +
            "本工具不会迁移旧电脑登录令牌、浏览器会话、机器标识或 sandbox secrets。";
        MessageBox.Show(
            owner,
            message,
            "新电脑操作说明",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static string? FindContainingBackupPackage()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var level = 0; current is not null && level < 3; level++, current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "manifest.json")) &&
                File.Exists(Path.Combine(current.FullName, "package-index.json")))
            {
                return current.FullName;
            }
        }

        return null;
    }
}
