using CodexBackup.Core.Backup;
using CodexBackup.Infrastructure.Windows.Codex;

namespace CodexBackup.Infrastructure.Windows.Tests;

public sealed class CodexStorageAdapterTests
{
    [Fact]
    public void Inspect_ClassifiesPortableStateCredentialsVolatileAndUnknownItems()
    {
        var fixture = CreateFixture();
        try
        {
            Directory.CreateDirectory(Path.Combine(fixture, "sessions"));
            Directory.CreateDirectory(Path.Combine(fixture, "skills"));
            Directory.CreateDirectory(Path.Combine(fixture, "cache"));
            Directory.CreateDirectory(Path.Combine(fixture, "mystery-feature"));
            File.WriteAllText(Path.Combine(fixture, "sessions", "one.jsonl"), "fixture");
            File.WriteAllText(Path.Combine(fixture, "skills", "SKILL.md"), "fixture");
            File.WriteAllText(Path.Combine(fixture, "auth.json"), "secret fixture");
            File.WriteAllText(Path.Combine(fixture, "config.toml"), "model = 'fixture'");
            File.WriteAllText(Path.Combine(fixture, "models_cache.json"), "{}");

            var result = new CodexStorageAdapter().Inspect(fixture);

            Assert.Equal(7, result.Items.Count);
            Assert.Equal(
                BackupPolicy.IncludePortableAndNative,
                result.Items.Single(item => item.Name == "sessions").Policy);
            Assert.Equal(BackupPolicy.Include, result.Items.Single(item => item.Name == "skills").Policy);
            Assert.Equal(
                BackupPolicy.ExcludeCredential,
                result.Items.Single(item => item.Name == "auth.json").Policy);
            Assert.Equal(
                BackupPolicy.ExcludeVolatile,
                result.Items.Single(item => item.Name == "cache").Policy);
            Assert.Equal(
                BackupPolicy.InventoryOnly,
                result.Items.Single(item => item.Name == "models_cache.json").Policy);
            Assert.Equal(
                BackupPolicy.UnknownReviewRequired,
                result.Items.Single(item => item.Name == "mystery-feature").Policy);
            Assert.True(result.Items.Single(item => item.Name == "config.toml").ContainsPotentialSecrets);
            Assert.Empty(result.Warnings);
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }

    [Fact]
    public void BackupPlan_AlwaysExcludesClassifiedCodexCredentials()
    {
        var fixture = CreateFixture();
        try
        {
            File.WriteAllText(Path.Combine(fixture, "auth.json"), "secret fixture");
            File.WriteAllText(Path.Combine(fixture, "AGENTS.md"), "fixture");
            var items = new CodexStorageAdapter().Inspect(fixture).Items;
            var candidates = items.Select(item => CodexBackupCandidateFactory.Create(
                item,
                isSelected: true,
                isReviewApproved: true));

            var plan = new BackupPlanBuilder().Build(candidates);

            Assert.True(plan.CanExport);
            Assert.Equal(
                BackupPlanItemState.ExcludedCredential,
                plan.Items.Single(item => item.DisplayName == "auth.json").State);
            Assert.Equal(
                BackupPlanItemState.Included,
                plan.Items.Single(item => item.DisplayName == "AGENTS.md").State);
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }

    private static string CreateFixture()
    {
        var path = Path.Combine(Path.GetTempPath(), "codex-storage-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
