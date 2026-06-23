using System.Security.Cryptography;
using CodexBackup.Core.Backup;
using CodexBackup.Core.Export;
using CodexBackup.Core.Manifest;
using CodexBackup.Infrastructure.Windows.Export;

namespace CodexBackup.Infrastructure.Windows.Tests;

public sealed class BackupExportEngineTests
{
    [Fact]
    public async Task ExportAsync_CopiesVerifiesAndCommitsCompletePackage()
    {
        var fixture = CreateFixture();
        try
        {
            var source = CreateSourceProject(fixture);
            var destination = Directory.CreateDirectory(Path.Combine(fixture, "destination")).FullName;
            var sourceHashes = HashTree(source);
            var engine = CreateEngine();

            var result = await engine.ExportAsync(CreatePlan(source), destination, "test-1.0");

            Assert.True(result.IsSuccess);
            Assert.NotNull(result.CompletedPackagePath);
            Assert.Null(result.IncompletePackagePath);
            Assert.False(File.Exists(Path.Combine(result.CompletedPackagePath!, "INCOMPLETE.json")));
            Assert.True(File.Exists(Path.Combine(result.CompletedPackagePath!, "manifest.json")));
            Assert.True(File.Exists(Path.Combine(result.CompletedPackagePath!, "package-index.json")));
            Assert.True(File.Exists(Path.Combine(result.CompletedPackagePath!, "export-report.json")));
            Assert.True(File.Exists(Path.Combine(result.CompletedPackagePath!, "export-report.html")));

            var manifest = BackupManifestJson.Deserialize(
                await File.ReadAllTextAsync(Path.Combine(result.CompletedPackagePath!, "manifest.json")));
            var index = BackupPackageIndexJson.Deserialize(
                await File.ReadAllTextAsync(Path.Combine(result.CompletedPackagePath!, "package-index.json")));
            Assert.Equal(3, manifest.Entries.Count);
            Assert.Equal(result.BackupId, manifest.BackupId);
            Assert.Equal(source, Assert.Single(index.Items).OriginalSourcePath);
            Assert.True(Directory.Exists(Path.Combine(
                result.CompletedPackagePath!,
                index.Items[0].RelativeRoot.Replace('/', Path.DirectorySeparatorChar),
                "empty")));

            var verification = await new BackupPackageVerifier().VerifyAsync(result.CompletedPackagePath!);
            Assert.True(verification.IsValid);
            Assert.Equal(3, verification.VerifiedFileCount);
            Assert.Equal(sourceHashes, HashTree(source));
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }

    [Fact]
    public async Task VerifyAsync_DetectsPackageFileTampering()
    {
        var fixture = CreateFixture();
        try
        {
            var source = CreateSourceProject(fixture);
            var destination = Directory.CreateDirectory(Path.Combine(fixture, "destination")).FullName;
            var export = await CreateEngine().ExportAsync(CreatePlan(source), destination, "test-1.0");
            var manifest = BackupManifestJson.Deserialize(
                await File.ReadAllTextAsync(Path.Combine(export.CompletedPackagePath!, "manifest.json")));
            var entry = manifest.Entries.First(item => item.Length > 0);
            var targetPath = Path.Combine(
                export.CompletedPackagePath!,
                entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            var bytes = await File.ReadAllBytesAsync(targetPath);
            bytes[0] ^= 0xff;
            await File.WriteAllBytesAsync(targetPath, bytes);

            var verification = await new BackupPackageVerifier().VerifyAsync(export.CompletedPackagePath!);

            Assert.False(verification.IsValid);
            Assert.Contains(verification.Issues, issue => issue.Code == "TARGET_HASH_MISMATCH");
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }

    [Fact]
    public async Task ExportAsync_BlocksWhenDestinationSpaceIsInsufficient()
    {
        var fixture = CreateFixture();
        try
        {
            var source = CreateSourceProject(fixture);
            var destination = Directory.CreateDirectory(Path.Combine(fixture, "destination")).FullName;
            var engine = new BackupExportEngine(new FixedVolumeInfoProvider(0, "NTFS"));

            var result = await engine.ExportAsync(CreatePlan(source), destination, "test-1.0");

            Assert.Equal(BackupExportStatus.Failed, result.Status);
            Assert.Null(result.IncompletePackagePath);
            Assert.Contains(result.Issues, issue => issue.Code == "DESTINATION_SPACE_INSUFFICIENT");
            Assert.Empty(Directory.GetFileSystemEntries(destination));
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }

    [Fact]
    public async Task ExportAsync_CancellationLeavesClearlyIncompletePackage()
    {
        var fixture = CreateFixture();
        try
        {
            var source = CreateSourceProject(fixture);
            var destination = Directory.CreateDirectory(Path.Combine(fixture, "destination")).FullName;
            using var cancellation = new CancellationTokenSource();
            var progress = new InlineProgress<BackupExportProgress>(value =>
            {
                if (value.Stage is BackupExportStage.Copying && value.CompletedFiles >= 1)
                {
                    cancellation.Cancel();
                }
            });

            var result = await CreateEngine().ExportAsync(
                CreatePlan(source),
                destination,
                "test-1.0",
                progress,
                cancellation.Token);

            Assert.Equal(BackupExportStatus.Cancelled, result.Status);
            Assert.Null(result.CompletedPackagePath);
            Assert.NotNull(result.IncompletePackagePath);
            Assert.True(Directory.Exists(result.IncompletePackagePath));
            Assert.True(File.Exists(Path.Combine(result.IncompletePackagePath!, "INCOMPLETE.json")));
            Assert.True(File.Exists(Path.Combine(result.IncompletePackagePath!, "export-report.json")));

            var verification = await new BackupPackageVerifier().VerifyAsync(result.IncompletePackagePath!);
            Assert.False(verification.IsValid);
            Assert.Contains(verification.Issues, issue => issue.Code == "PACKAGE_INCOMPLETE");
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }

    [Fact]
    public async Task ExportAsync_BlocksDestinationInsideSource()
    {
        var fixture = CreateFixture();
        try
        {
            var source = CreateSourceProject(fixture);
            var destination = Path.Combine(source, "backup-output");

            var result = await CreateEngine().ExportAsync(CreatePlan(source), destination, "test-1.0");

            Assert.Equal(BackupExportStatus.Failed, result.Status);
            Assert.Contains(result.Issues, issue => issue.Code == "DESTINATION_OVERLAPS_SOURCE");
            Assert.False(Directory.Exists(destination));
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }

    [Fact]
    public async Task ExportAsync_SupportsSingleFileBackupItems()
    {
        var fixture = CreateFixture();
        try
        {
            var sourceFile = Path.Combine(fixture, "config.toml");
            await File.WriteAllTextAsync(sourceFile, "model = 'fixture'");
            var destination = Directory.CreateDirectory(Path.Combine(fixture, "destination")).FullName;
            var candidate = new BackupCandidate(
                "codex-config",
                "config.toml",
                sourceFile,
                BackupDataKind.Configuration,
                BackupPolicy.Include,
                RestoreLevel.VerifiedExact,
                new FileInfo(sourceFile).Length,
                true);
            var plan = new BackupPlanBuilder().Build([candidate]);

            var result = await CreateEngine().ExportAsync(plan, destination, "test-1.0");

            Assert.True(result.IsSuccess);
            var manifest = BackupManifestJson.Deserialize(
                await File.ReadAllTextAsync(Path.Combine(result.CompletedPackagePath!, "manifest.json")));
            var entry = Assert.Single(manifest.Entries);
            Assert.EndsWith("/config.toml", entry.RelativePath, StringComparison.Ordinal);
            Assert.True((await new BackupPackageVerifier().VerifyAsync(result.CompletedPackagePath!)).IsValid);
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }

    private static BackupExportEngine CreateEngine() =>
        new(new FixedVolumeInfoProvider(long.MaxValue, "NTFS"));

    private static BackupPlan CreatePlan(string source)
    {
        var candidate = new BackupCandidate(
            "project-test",
            "演示项目",
            source,
            BackupDataKind.Project,
            BackupPolicy.Include,
            RestoreLevel.VerifiedExact,
            Directory.GetFiles(source, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length),
            true);
        return new BackupPlanBuilder().Build([candidate]);
    }

    private static string CreateSourceProject(string fixture)
    {
        var source = Directory.CreateDirectory(Path.Combine(fixture, "源 项目")).FullName;
        Directory.CreateDirectory(Path.Combine(source, ".git", "objects"));
        Directory.CreateDirectory(Path.Combine(source, "子目录"));
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        File.WriteAllText(Path.Combine(source, "README.md"), "hello backup");
        File.WriteAllBytes(Path.Combine(source, "子目录", "数据.bin"), Enumerable.Range(0, 4096)
            .Select(index => (byte)(index % 251))
            .ToArray());
        File.WriteAllText(Path.Combine(source, ".git", "HEAD"), "ref: refs/heads/main\n");
        return source;
    }

    private static Dictionary<string, string> HashTree(string root) => Directory
        .GetFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(
            path => Path.GetRelativePath(root, path),
            path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
            StringComparer.OrdinalIgnoreCase);

    private static string CreateFixture()
    {
        var path = Path.Combine(Path.GetTempPath(), "codex-backup-export-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FixedVolumeInfoProvider(long availableBytes, string fileSystem)
        : IDestinationVolumeInfoProvider
    {
        public DestinationVolumeInfo GetInfo(string destinationPath) => new(availableBytes, fileSystem);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
