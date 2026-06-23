using CodexBackup.Core.Backup;
using CodexBackup.Core.Discovery;
using CodexBackup.Core.Manifest;
using CodexBackup.Core.Restore;
using CodexBackup.Infrastructure.Windows.Codex;
using CodexBackup.Infrastructure.Windows.Restore;

namespace CodexBackup.Infrastructure.Windows.Tests;

public sealed class RestorePlanBuilderTests
{
    [Fact]
    public void Build_DefaultsProjectConflictToKeepBoth()
    {
        var fixture = CreateFixture();
        try
        {
            var projectRoot = Directory.CreateDirectory(Path.Combine(fixture, "projects")).FullName;
            Directory.CreateDirectory(Path.Combine(projectRoot, "Demo"));
            var index = CreateIndex([
                new BackupPackageItem(
                    "project-demo",
                    "Demo",
                    "C:\\OldComputer\\Demo",
                    "payload/projects/demo",
                    BackupDataKind.Project,
                    RestoreLevel.VerifiedExact,
                    ProjectDiscoverySource.None),
            ]);

            var plan = new RestorePlanBuilder().Build(
                CreateRequest(fixture, projectRoot),
                index,
                CodexStorageAdapter.CurrentAdapterVersion);

            var item = Assert.Single(plan.Items);
            Assert.Equal(RestoreItemState.Ready, item.State);
            Assert.NotEqual(Path.Combine(projectRoot, "Demo"), item.TargetPath);
            Assert.Contains("从备份恢复", item.TargetPath);
            Assert.True(plan.CanRestore);
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }

    [Fact]
    public void Build_SkipsNativeStateWhenAdapterVersionDoesNotMatch()
    {
        var fixture = CreateFixture();
        try
        {
            var index = CreateIndex([
                new BackupPackageItem(
                    "codex-sessions",
                    "sessions",
                    "C:\\Users\\Old\\.codex\\sessions",
                    "payload/codex-sessions/sessions",
                    BackupDataKind.CodexSession,
                    RestoreLevel.NativeBestEffort,
                    ProjectDiscoverySource.None),
            ]) with
            {
                CodexAdapterVersion = "different-adapter",
            };
            var request = CreateRequest(
                fixture,
                Directory.CreateDirectory(Path.Combine(fixture, "projects")).FullName) with
            {
                RestoreNativeCodexState = true,
            };

            var plan = new RestorePlanBuilder().Build(
                request,
                index,
                CodexStorageAdapter.CurrentAdapterVersion);

            Assert.Equal(
                RestoreItemState.SkippedIncompatible,
                Assert.Single(plan.Items).State);
            Assert.False(plan.CanRestore);
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }

    [Fact]
    public void Build_NeverMapsCredentialNamesIntoCodexTarget()
    {
        var fixture = CreateFixture();
        try
        {
            var projectRoot = Directory.CreateDirectory(Path.Combine(fixture, "projects")).FullName;
            var index = CreateIndex([
                new BackupPackageItem(
                    "malicious-auth",
                    "auth.json",
                    "C:\\Users\\Old\\.codex\\auth.json",
                    "payload/configuration/auth",
                    BackupDataKind.Configuration,
                    RestoreLevel.VerifiedExact,
                    ProjectDiscoverySource.None,
                    SourceWasDirectory: false),
                new BackupPackageItem(
                    "project-demo",
                    "Demo",
                    "C:\\OldComputer\\Demo",
                    "payload/projects/demo",
                    BackupDataKind.Project,
                    RestoreLevel.VerifiedExact,
                    ProjectDiscoverySource.None),
            ]);

            var plan = new RestorePlanBuilder().Build(
                CreateRequest(fixture, projectRoot),
                index,
                CodexStorageAdapter.CurrentAdapterVersion);

            Assert.Equal(
                RestoreItemState.SkippedIncompatible,
                plan.Items.Single(item => item.PackageItemId == "malicious-auth").State);
            Assert.DoesNotContain(plan.Items, item =>
                item.TargetPath.EndsWith("auth.json", StringComparison.OrdinalIgnoreCase));
            Assert.True(plan.CanRestore);
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }

    private static RestoreRequest CreateRequest(string fixture, string projectRoot) => new(
        Path.Combine(fixture, "package"),
        projectRoot,
        Path.Combine(fixture, ".codex"),
        Path.Combine(fixture, "restored-data"));

    private static BackupPackageIndex CreateIndex(IReadOnlyList<BackupPackageItem> items) => new(
        BackupPackageIndex.CurrentFormatVersion,
        "backup-1",
        "plan-1",
        DateTimeOffset.UtcNow,
        "test",
        items,
        CodexStorageAdapter.CurrentAdapterVersion);

    private static string CreateFixture()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "codex-restore-plan-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
