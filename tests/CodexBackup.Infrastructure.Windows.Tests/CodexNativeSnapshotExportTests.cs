using CodexBackup.Core.Backup;
using CodexBackup.Core.Codex;
using CodexBackup.Core.Manifest;
using CodexBackup.Infrastructure.Windows.Codex;
using CodexBackup.Infrastructure.Windows.Export;

namespace CodexBackup.Infrastructure.Windows.Tests;

public sealed class CodexNativeSnapshotExportTests
{
    [Fact]
    public async Task Export_DefaultCodexSnapshotIncludesPortableDataAndOmitsCredentialsAndCache()
    {
        var fixture = CreateFixture();
        try
        {
            var codexRoot = Directory.CreateDirectory(Path.Combine(fixture, ".codex")).FullName;
            var destination = Directory.CreateDirectory(Path.Combine(fixture, "destination")).FullName;
            Directory.CreateDirectory(Path.Combine(codexRoot, "sessions"));
            Directory.CreateDirectory(Path.Combine(codexRoot, "rules"));
            Directory.CreateDirectory(Path.Combine(codexRoot, "cache"));
            File.WriteAllText(Path.Combine(codexRoot, "sessions", "one.jsonl"), "session fixture");
            File.WriteAllText(Path.Combine(codexRoot, "rules", "default.rules"), "rule fixture");
            File.WriteAllText(Path.Combine(codexRoot, "config.toml"), "model = 'fixture'");
            File.WriteAllText(Path.Combine(codexRoot, "auth.json"), "credential must not export");
            File.WriteAllText(Path.Combine(codexRoot, "cache", "cache.bin"), "cache must not export");

            var inventory = new CodexStorageAdapter().Inspect(codexRoot);
            var candidates = inventory.Items.Select(item => CodexBackupCandidateFactory.Create(
                item,
                item.Policy is BackupPolicy.Include or BackupPolicy.IncludePortableAndNative));
            var plan = new BackupPlanBuilder().Build(candidates);
            plan = CodexSnapshotPlanGuard.Apply(
                plan,
                inventory.Items,
                new CodexUsageStatus(0, 0, 0, []));
            var engine = new BackupExportEngine(new FixedVolumeInfoProvider());

            var result = await engine.ExportAsync(plan, destination, "test-1.0");

            Assert.True(result.IsSuccess);
            var manifest = BackupManifestJson.Deserialize(
                await File.ReadAllTextAsync(Path.Combine(result.CompletedPackagePath!, "manifest.json")));
            Assert.Contains(manifest.Entries, entry => entry.RelativePath.EndsWith("/one.jsonl"));
            Assert.Contains(manifest.Entries, entry => entry.RelativePath.EndsWith("/default.rules"));
            Assert.Contains(manifest.Entries, entry => entry.RelativePath.EndsWith("/config.toml"));
            Assert.DoesNotContain(manifest.Entries, entry => entry.RelativePath.Contains("auth.json"));
            Assert.DoesNotContain(manifest.Entries, entry => entry.RelativePath.Contains("cache.bin"));
            Assert.True((await new BackupPackageVerifier().VerifyAsync(result.CompletedPackagePath!)).IsValid);
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }

    private static string CreateFixture()
    {
        var path = Path.Combine(Path.GetTempPath(), "codex-native-export-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FixedVolumeInfoProvider : IDestinationVolumeInfoProvider
    {
        public DestinationVolumeInfo GetInfo(string destinationPath) => new(long.MaxValue, "NTFS");
    }
}
