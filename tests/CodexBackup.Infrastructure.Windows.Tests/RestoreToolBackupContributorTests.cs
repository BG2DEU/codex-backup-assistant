using CodexBackup.Core.Backup;
using CodexBackup.Core.Manifest;
using CodexBackup.Infrastructure.Windows.Export;

namespace CodexBackup.Infrastructure.Windows.Tests;

public sealed class RestoreToolBackupContributorTests
{
    [Fact]
    public async Task Export_IncludesRestoreExecutableAndInstructions()
    {
        var fixture = CreateFixture();
        try
        {
            var source = Directory.CreateDirectory(Path.Combine(fixture, "project")).FullName;
            File.WriteAllText(Path.Combine(source, "README.md"), "fixture");
            var fakeExecutable = Path.Combine(fixture, "Codex换机助手.exe");
            File.WriteAllBytes(fakeExecutable, Enumerable.Range(0, 4096)
                .Select(index => (byte)(index % 251))
                .ToArray());
            var destination = Directory.CreateDirectory(Path.Combine(fixture, "destination")).FullName;
            var candidate = new BackupCandidate(
                "project",
                "project",
                source,
                BackupDataKind.Project,
                BackupPolicy.Include,
                RestoreLevel.VerifiedExact,
                10,
                true);
            var plan = new BackupPlanBuilder().Build([candidate]);
            var engine = new BackupExportEngine(
                new FixedVolumeInfoProvider(),
                contributors: [new RestoreToolBackupContributor(fakeExecutable)]);

            var result = await engine.ExportAsync(plan, destination, "test");

            Assert.True(result.IsCompleted);
            var toolPath = Path.Combine(
                result.CompletedPackagePath!,
                "tools",
                "Codex换机助手.exe");
            Assert.True(File.Exists(toolPath));
            Assert.Equal(
                File.ReadAllBytes(fakeExecutable),
                File.ReadAllBytes(toolPath));
            Assert.True(File.Exists(Path.Combine(
                result.CompletedPackagePath!,
                "tools",
                "新电脑恢复说明.txt")));
            var instructions = await File.ReadAllTextAsync(Path.Combine(
                result.CompletedPackagePath!,
                "tools",
                "新电脑恢复说明.txt"));
            Assert.Contains("新电脑已经安装 Codex", instructions);
            Assert.Contains("已经至少启动并登录过一次 Codex", instructions);
            Assert.Contains("恢复前请完全退出 Codex", instructions);
            Assert.DoesNotContain("必须先安装、启动并登录 Codex", instructions);
            var index = BackupPackageIndexJson.Deserialize(
                await File.ReadAllTextAsync(Path.Combine(
                    result.CompletedPackagePath!,
                    "package-index.json")));
            Assert.Contains(index.Items, item => item.Id == "restore-tool-v1");
            Assert.True((await new BackupPackageVerifier().VerifyAsync(
                result.CompletedPackagePath!)).IsValid);
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }

    private static string CreateFixture()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "codex-restore-tool-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FixedVolumeInfoProvider : IDestinationVolumeInfoProvider
    {
        public DestinationVolumeInfo GetInfo(string destinationPath) => new(long.MaxValue, "NTFS");
    }
}
