using System.Text;
using CodexBackup.Core.Backup;
using CodexBackup.Core.Discovery;
using CodexBackup.Core.Export;
using CodexBackup.Core.Manifest;

namespace CodexBackup.Infrastructure.Windows.Export;

public sealed class RestoreToolBackupContributor(string executablePath)
    : IBackupPackageContributor
{
    private const string ToolRoot = "tools";
    private const string ToolRelativePath = "tools/Codex换机助手.exe";
    private const string InstructionsRelativePath = "tools/新电脑恢复说明.txt";
    private readonly string _executablePath = Path.GetFullPath(executablePath);

    public long EstimateAdditionalBytes(BackupPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return File.Exists(_executablePath)
            ? new FileInfo(_executablePath).Length + 4096
            : 0;
    }

    public async Task<BackupContributionResult> ContributeAsync(
        BackupContributionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!File.Exists(_executablePath))
        {
            return new BackupContributionResult(
                [],
                [],
                [
                    new BackupExportIssue(
                        "transforming",
                        "RESTORE_TOOL_SOURCE_MISSING",
                        "The running restore tool executable could not be found."),
                ]);
        }

        var toolDirectory = GetSafePackagePath(context.PackageRoot, ToolRoot);
        Directory.CreateDirectory(toolDirectory);
        var targetExecutable = GetSafePackagePath(
            context.PackageRoot,
            ToolRelativePath);
        await using (var source = new FileStream(
                         _executablePath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read | FileShare.Delete,
                         1024 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var target = new FileStream(
                         targetExecutable,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         1024 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await source.CopyToAsync(target, cancellationToken);
            await target.FlushAsync(cancellationToken);
            target.Flush(flushToDisk: true);
        }

        var instructions = """
                           Codex 换机恢复说明

                           新电脑前提：
                           - 新电脑已经安装 Codex。
                           - 已经至少启动并登录过一次 Codex，让新电脑生成自己的本机环境。
                           - 恢复前请完全退出 Codex，避免数据库仍在使用。

                           推荐操作：
                           1. 插入移动硬盘。
                           2. 在本备份包的 tools 目录中打开“Codex换机助手.exe”。
                           3. 点击“查看新电脑操作说明”，确认当前电脑满足前提。
                           4. 点击“校验备份包”，选择本备份包根目录，也就是包含 manifest.json 的目录。
                           5. 校验通过后，点击“选择备份并恢复”。
                           6. 再次选择本备份包根目录，并选择项目恢复位置。
                           7. 默认选择不恢复 Codex 原生状态；项目、通用对话和安全配置仍会恢复。
                           8. 恢复完成后查看底部摘要，并点击“打开恢复报告”。

                           安全边界：
                           - 项目默认另存恢复，不覆盖新电脑已有项目。
                           - 登录令牌、浏览器会话、机器标识和 sandbox secrets 不会从旧电脑恢复。
                           - 如需尝试 Codex 原生状态恢复，必须确认 Codex 已安装、已初始化、已登录并完全退出，且工具判定版本兼容。
                           """;
        await File.WriteAllTextAsync(
            GetSafePackagePath(context.PackageRoot, InstructionsRelativePath),
            instructions,
            new UTF8Encoding(false),
            cancellationToken);

        return new BackupContributionResult(
            [
                new GeneratedPackageFile(
                    ToolRelativePath,
                    BackupDataKind.GeneratedAsset,
                    RestoreLevel.Rebuildable),
                new GeneratedPackageFile(
                    InstructionsRelativePath,
                    BackupDataKind.GeneratedAsset,
                    RestoreLevel.Rebuildable),
            ],
            [
                new BackupPackageItem(
                    "restore-tool-v1",
                    "Codex 换机恢复程序",
                    _executablePath,
                    ToolRoot,
                    BackupDataKind.GeneratedAsset,
                    RestoreLevel.Rebuildable,
                    ProjectDiscoverySource.None,
                    SourceWasDirectory: true),
            ],
            []);
    }

    private static string GetSafePackagePath(string root, string relativePath)
    {
        if (!BackupPathRules.IsSafeRelativePath(relativePath))
        {
            throw new IOException("Restore tool package path is unsafe.");
        }

        var normalizedRoot = Path.GetFullPath(root);
        var targetPath = Path.GetFullPath(Path.Combine(
            normalizedRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(normalizedRoot, targetPath);
        if (relative == ".." ||
            Path.IsPathFullyQualified(relative) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new IOException("Restore tool package path escaped the package.");
        }

        return targetPath;
    }
}
