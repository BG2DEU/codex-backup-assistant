using System.Text.Json;
using CodexBackup.App.Presentation;
using CodexBackup.Core.Backup;
using CodexBackup.Core.Export;
using CodexBackup.Core.Manifest;
using CodexBackup.Infrastructure.Windows.Codex;
using CodexBackup.Infrastructure.Windows.Discovery;
using CodexBackup.Infrastructure.Windows.Export;
using CodexBackup.Infrastructure.Windows.Restore;

namespace CodexBackup.App.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public async Task VerifyBackupCommand_VerifiesSelectedPackage()
    {
        var fixture = CreateFixture();
        try
        {
            var package = await CreateMigrationPackageAsync(fixture);
            var interaction = new ScriptedExportInteraction(
                backupPackage: package,
                projectRestoreRoot: Path.Combine(fixture, "new-computer", "Projects"));
            var viewModel = CreateViewModel(fixture, interaction);

            await viewModel.VerifyBackupCommand.ExecuteAsync();

            Assert.Contains("备份校验通过", viewModel.StatusText);
            Assert.Contains(package, viewModel.StatusText);
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }

    [Fact]
    public async Task RestoreCommand_RestoresPackageThroughDesktopFlow()
    {
        var fixture = CreateFixture();
        try
        {
            var package = await CreateMigrationPackageAsync(fixture);
            var projectRestoreRoot = Directory.CreateDirectory(
                Path.Combine(fixture, "new-computer", "Projects")).FullName;
            var existingProject = Directory.CreateDirectory(
                Path.Combine(projectRestoreRoot, "Demo")).FullName;
            await File.WriteAllTextAsync(
                Path.Combine(existingProject, "new-computer.txt"),
                "preserve");
            var interaction = new ScriptedExportInteraction(
                backupPackage: package,
                projectRestoreRoot: projectRestoreRoot);
            var operationLog = new RecordingOperationLog(
                Path.Combine(fixture, "logs"));
            var viewModel = CreateViewModel(fixture, interaction, operationLog);

            await viewModel.RestoreCommand.ExecuteAsync();

            Assert.Contains("恢复完成", viewModel.StatusText);
            Assert.Contains("恢复摘要：项目 1 个", viewModel.RestoreSummaryText);
            Assert.Contains("同名项目另存 1 个", viewModel.RestoreSummaryText);
            Assert.NotNull(viewModel.LastRestoreReportPath);
            Assert.True(File.Exists(viewModel.LastRestoreReportPath));
            Assert.Contains(viewModel.LastRestoreReportPath, viewModel.RestoreReportPathText);
            Assert.True(viewModel.OpenRestoreReportCommand.CanExecute(null));
            var restoredProjects = Directory.GetDirectories(
                projectRestoreRoot,
                "Demo-从备份恢复-*");
            Assert.Single(restoredProjects);
            Assert.Equal(
                "old project",
                await File.ReadAllTextAsync(Path.Combine(
                    restoredProjects[0],
                    "README.md")));
            Assert.Equal(
                "preserve",
                await File.ReadAllTextAsync(Path.Combine(
                    existingProject,
                    "new-computer.txt")));
            Assert.True(Directory.Exists(Path.Combine(
                projectRestoreRoot,
                "Codex换机恢复资料",
                "Codex通用对话")));
            Assert.Contains(operationLog.Messages, message =>
                message.Contains("RESTORE_START", StringComparison.Ordinal));
            Assert.Contains(operationLog.Messages, message =>
                message.Contains("RESTORE_RESULT status=Success", StringComparison.Ordinal));
            Assert.True(viewModel.OpenLogFolderCommand.CanExecute(null));
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }

    [Fact]
    public async Task ShowNewComputerGuideCommand_UsesInstalledAndSignedInCodexPremise()
    {
        var fixture = CreateFixture();
        try
        {
            var interaction = new ScriptedExportInteraction(
                backupPackage: Path.Combine(fixture, "package"),
                projectRestoreRoot: Path.Combine(fixture, "Projects"));
            var viewModel = CreateViewModel(fixture, interaction);

            await viewModel.ShowNewComputerGuideCommand.ExecuteAsync();

            Assert.True(interaction.WasNewComputerGuideShown);
            Assert.Contains("已经安装并登录过 Codex", viewModel.StatusText);
            Assert.DoesNotContain("先安装 Codex", viewModel.StatusText);
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }


    private static MainWindowViewModel CreateViewModel(
        string fixture,
        IExportInteraction interaction,
        IOperationLog? operationLog = null)
    {
        var packageVerifier = new BackupPackageVerifier();
        return new MainWindowViewModel(
            WindowsProjectDiscovery.CreateService(),
            packageVerifier: packageVerifier,
            restoreEngine: new BackupRestoreEngine(packageVerifier),
            exportInteraction: interaction,
            operationLog: operationLog,
            codexRoot: Path.Combine(fixture, "new-computer", ".codex"));
    }

    private static async Task<string> CreateMigrationPackageAsync(string fixture)
    {
        var oldComputer = Directory.CreateDirectory(
            Path.Combine(fixture, "old-computer")).FullName;
        var project = Directory.CreateDirectory(Path.Combine(oldComputer, "Demo")).FullName;
        var codexRoot = Directory.CreateDirectory(Path.Combine(oldComputer, ".codex")).FullName;
        var sessions = Directory.CreateDirectory(Path.Combine(codexRoot, "sessions")).FullName;
        await File.WriteAllTextAsync(Path.Combine(project, "README.md"), "old project");
        await File.WriteAllTextAsync(
            Path.Combine(codexRoot, "config.toml"),
            "old_setting = true");
        await File.WriteAllLinesAsync(Path.Combine(sessions, "session.jsonl"),
        [
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-06-23T01:00:00Z",
                type = "session_meta",
                payload = new { id = "session-desktop-restore", cwd = project, cli_version = "1.0" },
            }),
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-06-23T01:01:00Z",
                type = "event_msg",
                payload = new { type = "user_message", message = "恢复这段桌面流程对话" },
            }),
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-06-23T01:02:00Z",
                type = "event_msg",
                payload = new { type = "agent_message", message = "可以恢复。" },
            }),
        ]);

        var plan = new BackupPlanBuilder().Build(
        [
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
                Directory.EnumerateFiles(sessions).Sum(path => new FileInfo(path).Length),
                true),
        ]);
        var destination = Directory.CreateDirectory(
            Path.Combine(fixture, "external-drive")).FullName;
        var export = await new BackupExportEngine(
            new FixedVolumeInfoProvider(),
            contributors: [new CodexConversationBackupContributor()],
            codexAdapterVersion: CodexStorageAdapter.CurrentAdapterVersion)
            .ExportAsync(plan, destination, "app-test-1.0");
        Assert.True(
            export.IsCompleted,
            string.Join(" | ", export.Issues.Select(issue =>
                $"{issue.Code}:{issue.Message}")));
        return export.CompletedPackagePath!;
    }

    private static string CreateFixture()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "codex-app-viewmodel-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FixedVolumeInfoProvider : IDestinationVolumeInfoProvider
    {
        public DestinationVolumeInfo GetInfo(string destinationPath) => new(long.MaxValue, "NTFS");
    }

    private sealed class ScriptedExportInteraction : IExportInteraction
    {
        private readonly string _backupPackage;
        private readonly string _projectRestoreRoot;

        public ScriptedExportInteraction(
            string backupPackage,
            string projectRestoreRoot)
        {
            _backupPackage = backupPackage;
            _projectRestoreRoot = projectRestoreRoot;
        }

        public bool WasNewComputerGuideShown { get; private set; }

        public string? SelectDestination() => null;

        public bool ConfirmExport(
            int projectCount,
            int codexItemCount,
            long estimatedBytes,
            int secretRiskItemCount) => false;

        public string? SelectBackupPackage() => _backupPackage;

        public string? SelectProjectRestoreRoot() => _projectRestoreRoot;

        public bool ConfirmNativeCodexRestore() => false;

        public bool ConfirmRestore(
            string packagePath,
            string selectedProjectRestoreRoot,
            bool restoreNativeCodexState) =>
            string.Equals(packagePath, _backupPackage, StringComparison.Ordinal) &&
            string.Equals(selectedProjectRestoreRoot, _projectRestoreRoot, StringComparison.Ordinal) &&
            !restoreNativeCodexState;

        public void ShowNewComputerGuide()
        {
            WasNewComputerGuideShown = true;
        }
    }

    private sealed class RecordingOperationLog(string logDirectory) : IOperationLog
    {
        public List<string> Messages { get; } = [];

        public string LogDirectory { get; } =
            Directory.CreateDirectory(logDirectory).FullName;

        public string CurrentLogPath => Path.Combine(LogDirectory, "test.log");

        public void Info(string message) => Messages.Add(message);

        public void Error(string message, Exception? exception = null) =>
            Messages.Add(exception is null
                ? message
                : $"{message}:{exception.Message}");
    }
}
