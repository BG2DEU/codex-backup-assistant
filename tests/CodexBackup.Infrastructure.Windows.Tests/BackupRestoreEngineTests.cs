using System.Security.Cryptography;
using System.Text.Json;
using CodexBackup.Core.Backup;
using CodexBackup.Core.Export;
using CodexBackup.Core.Manifest;
using CodexBackup.Core.Restore;
using CodexBackup.Infrastructure.Windows.Codex;
using CodexBackup.Infrastructure.Windows.Export;
using CodexBackup.Infrastructure.Windows.Restore;

namespace CodexBackup.Infrastructure.Windows.Tests;

public sealed class BackupRestoreEngineTests
{
    [Fact]
    public async Task RestoreAsync_VerifiesAndRestoresProjectsPortableConversationsAndSafeConfig()
    {
        var fixture = CreateFixture();
        try
        {
            var package = await CreateMigrationPackageAsync(fixture);
            var projectDestination = Directory.CreateDirectory(
                Path.Combine(fixture, "new-computer", "Projects")).FullName;
            var codexDestination = Directory.CreateDirectory(
                Path.Combine(fixture, "new-computer", ".codex")).FullName;
            var portableDestination = Directory.CreateDirectory(
                Path.Combine(fixture, "new-computer", "Recovered")).FullName;
            var existingProject = Directory.CreateDirectory(
                Path.Combine(projectDestination, "Demo")).FullName;
            File.WriteAllText(Path.Combine(existingProject, "new-computer.txt"), "preserve");
            File.WriteAllText(
                Path.Combine(codexDestination, "config.toml"),
                "new_computer_setting = true");
            var request = new RestoreRequest(
                package,
                projectDestination,
                codexDestination,
                portableDestination,
                RestoreNativeCodexState: false);

            var result = await new BackupRestoreEngine().RestoreAsync(request);

            Assert.Equal(RestoreStatus.Success, result.Status);
            var restoredProject = result.Items.Single(item =>
                item.Kind is BackupDataKind.Project);
            Assert.Contains("从备份恢复", restoredProject.TargetPath);
            Assert.Equal(
                "old project",
                File.ReadAllText(Path.Combine(restoredProject.TargetPath, "README.md")));
            Assert.Equal(
                "preserve",
                File.ReadAllText(Path.Combine(existingProject, "new-computer.txt")));
            Assert.Equal(
                "new_computer_setting = true",
                File.ReadAllText(Path.Combine(codexDestination, "config.toml")));
            Assert.True(Directory.Exists(Path.Combine(
                portableDestination,
                "Codex通用对话")));
            Assert.Contains(result.Items, item =>
                item.Kind is BackupDataKind.CodexSession &&
                item.RestoreLevel is RestoreLevel.NativeBestEffort &&
                item.State is RestoreItemState.SkippedIncompatible);
            Assert.True(Directory.EnumerateFiles(
                portableDestination,
                "Codex恢复报告_*.json").Any());
            Assert.NotNull(result.ReportHtmlPath);
            Assert.NotNull(result.ReportJsonPath);
            Assert.True(File.Exists(result.ReportHtmlPath));
            Assert.True(File.Exists(result.ReportJsonPath));
            var htmlReport = await File.ReadAllTextAsync(result.ReportHtmlPath);
            Assert.Contains("备份包", htmlReport);
            Assert.Contains("恢复项", htmlReport);
            Assert.Contains("同名另存", htmlReport);
            Assert.Contains("通用对话", htmlReport);
            Assert.Contains("登录令牌", htmlReport);
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }

    [Fact]
    public async Task RestoreAsync_ReplaceWithRollbackPreservesPreviousProject()
    {
        var fixture = CreateFixture();
        try
        {
            var package = await CreateMigrationPackageAsync(fixture);
            var projectDestination = Directory.CreateDirectory(
                Path.Combine(fixture, "new-computer", "Projects")).FullName;
            var codexDestination = Directory.CreateDirectory(
                Path.Combine(fixture, "new-computer", ".codex")).FullName;
            var portableDestination = Directory.CreateDirectory(
                Path.Combine(fixture, "new-computer", "Recovered")).FullName;
            var existingProject = Directory.CreateDirectory(
                Path.Combine(projectDestination, "Demo")).FullName;
            File.WriteAllText(Path.Combine(existingProject, "README.md"), "new computer project");
            var request = new RestoreRequest(
                package,
                projectDestination,
                codexDestination,
                portableDestination,
                RestorePortableConversations: false,
                RestoreCodexConfiguration: false,
                ProjectConflictPolicy: RestoreConflictPolicy.ReplaceWithRollback);

            var result = await new BackupRestoreEngine().RestoreAsync(request);

            Assert.True(
                result.Status is RestoreStatus.Success,
                string.Join(" | ", result.Issues.Select(issue =>
                    $"{issue.Code}:{issue.Message}")));
            Assert.Equal(
                "old project",
                File.ReadAllText(Path.Combine(existingProject, "README.md")));
            Assert.NotNull(result.RollbackPath);
            Assert.Contains(
                Directory.EnumerateFiles(
                    result.RollbackPath!,
                    "README.md",
                    SearchOption.AllDirectories),
                path => File.ReadAllText(path) == "new computer project");
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }

    [Fact]
    public async Task RestoreAsync_RefusesTamperedPackageBeforeWritingTargets()
    {
        var fixture = CreateFixture();
        try
        {
            var package = await CreateMigrationPackageAsync(fixture);
            var manifest = BackupManifestJson.Deserialize(
                await File.ReadAllTextAsync(Path.Combine(package, "manifest.json")));
            var projectEntry = manifest.Entries.First(entry =>
                entry.Kind is BackupDataKind.Project);
            var tamperedPath = Path.Combine(
                package,
                projectEntry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            await File.AppendAllTextAsync(tamperedPath, "tampered");
            var projectDestination = Path.Combine(fixture, "new-computer", "Projects");
            var request = new RestoreRequest(
                package,
                projectDestination,
                Path.Combine(fixture, "new-computer", ".codex"),
                Path.Combine(fixture, "new-computer", "Recovered"));

            var result = await new BackupRestoreEngine().RestoreAsync(request);

            Assert.Equal(RestoreStatus.Failed, result.Status);
            Assert.Contains(result.Issues, issue =>
                issue.Code is "TARGET_LENGTH_MISMATCH" or "TARGET_HASH_MISMATCH");
            Assert.False(Directory.Exists(projectDestination));
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }

    [Fact]
    public async Task RestoreAsync_EndToEndMobileDrivePackageIncludesToolInstructionsAndReport()
    {
        var fixture = CreateFixture();
        try
        {
            var mobileDrive = Directory.CreateDirectory(
                Path.Combine(fixture, "移动硬盘", "CodexBackups")).FullName;
            var package = await CreateMigrationPackageAsync(
                fixture,
                mobileDrive,
                includeRestoreTool: true);
            Assert.StartsWith(
                Path.GetFullPath(mobileDrive),
                Path.GetFullPath(package),
                StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(
                package,
                "tools",
                "Codex换机助手.exe")));
            var instructions = await File.ReadAllTextAsync(Path.Combine(
                package,
                "tools",
                "新电脑恢复说明.txt"));
            Assert.Contains("新电脑已经安装 Codex", instructions);
            Assert.Contains("默认选择不恢复 Codex 原生状态", instructions);

            var verification = await new BackupPackageVerifier().VerifyAsync(package);
            Assert.True(
                verification.IsValid,
                string.Join(" | ", verification.Issues.Select(issue =>
                    $"{issue.Code}:{issue.Message}")));

            var newComputer = Directory.CreateDirectory(
                Path.Combine(fixture, "new-computer")).FullName;
            var request = new RestoreRequest(
                package,
                Path.Combine(newComputer, "RestoredProjects"),
                Directory.CreateDirectory(Path.Combine(newComputer, ".codex")).FullName,
                Path.Combine(newComputer, "Codex换机恢复资料"),
                RestoreNativeCodexState: false);

            var result = await new BackupRestoreEngine().RestoreAsync(request);

            Assert.Equal(RestoreStatus.Success, result.Status);
            Assert.Contains(result.Items, item =>
                item.PackageItemId == "restore-tool-v1" &&
                item.State is RestoreItemState.SkippedByUser);
            Assert.True(Directory.Exists(Path.Combine(
                request.PortableDataDestinationRoot,
                "Codex通用对话")));
            Assert.NotNull(result.ReportHtmlPath);
            var htmlReport = await File.ReadAllTextAsync(result.ReportHtmlPath);
            Assert.Contains("移动硬盘", htmlReport);
            Assert.Contains("Codex 换机恢复程序", htmlReport);
            Assert.Contains("通用对话", htmlReport);
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }

    private static async Task<string> CreateMigrationPackageAsync(
        string fixture,
        string? destinationRoot = null,
        bool includeRestoreTool = false)
    {
        var oldComputer = Directory.CreateDirectory(
            Path.Combine(fixture, "old-computer")).FullName;
        var project = Directory.CreateDirectory(Path.Combine(oldComputer, "Demo")).FullName;
        var codexRoot = Directory.CreateDirectory(Path.Combine(oldComputer, ".codex")).FullName;
        var sessions = Directory.CreateDirectory(Path.Combine(codexRoot, "sessions")).FullName;
        File.WriteAllText(Path.Combine(project, "README.md"), "old project");
        File.WriteAllText(Path.Combine(codexRoot, "config.toml"), "old_setting = true");
        File.WriteAllLines(Path.Combine(sessions, "session.jsonl"),
        [
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-06-22T01:00:00Z",
                type = "session_meta",
                payload = new { id = "session-restore", cwd = project, cli_version = "1.0" },
            }),
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-06-22T01:01:00Z",
                type = "event_msg",
                payload = new { type = "user_message", message = "恢复这段对话" },
            }),
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-06-22T01:02:00Z",
                type = "event_msg",
                payload = new { type = "agent_message", message = "可以恢复。" },
            }),
        ]);

        var candidates = new[]
        {
            new BackupCandidate(
                "project-demo",
                "Demo",
                project,
                BackupDataKind.Project,
                BackupPolicy.Include,
                RestoreLevel.VerifiedExact,
                new FileInfo(Path.Combine(project, "README.md")).Length,
                true),
            new BackupCandidate(
                "codex-config",
                "config.toml",
                Path.Combine(codexRoot, "config.toml"),
                BackupDataKind.Configuration,
                BackupPolicy.Include,
                RestoreLevel.VerifiedExact,
                new FileInfo(Path.Combine(codexRoot, "config.toml")).Length,
                true),
            new BackupCandidate(
                "codex-sessions",
                "sessions",
                sessions,
                BackupDataKind.CodexSession,
                BackupPolicy.IncludePortableAndNative,
                RestoreLevel.NativeBestEffort,
                new FileInfo(Path.Combine(sessions, "session.jsonl")).Length,
                true),
        };
        var plan = new BackupPlanBuilder().Build(candidates);
        var destination = Directory.CreateDirectory(
            destinationRoot ?? Path.Combine(fixture, "external-drive")).FullName;
        var contributors = new List<IBackupPackageContributor>
        {
            new CodexConversationBackupContributor(),
        };
        if (includeRestoreTool)
        {
            var fakeExecutable = Path.Combine(fixture, "published", "Codex换机助手.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(fakeExecutable)!);
            File.WriteAllBytes(fakeExecutable, Enumerable.Range(0, 4096)
                .Select(index => (byte)(index % 251))
                .ToArray());
            contributors.Add(new RestoreToolBackupContributor(fakeExecutable));
        }

        var export = await new BackupExportEngine(
            new FixedVolumeInfoProvider(),
            contributors: contributors,
            codexAdapterVersion: CodexStorageAdapter.CurrentAdapterVersion)
            .ExportAsync(plan, destination, "test-1.0");
        Assert.True(export.IsCompleted);
        return export.CompletedPackagePath!;
    }

    private static string CreateFixture()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "codex-restore-engine-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FixedVolumeInfoProvider : IDestinationVolumeInfoProvider
    {
        public DestinationVolumeInfo GetInfo(string destinationPath) => new(long.MaxValue, "NTFS");
    }
}
