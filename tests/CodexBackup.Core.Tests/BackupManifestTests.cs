using CodexBackup.Core.Backup;
using CodexBackup.Core.Manifest;

namespace CodexBackup.Core.Tests;

public sealed class BackupManifestTests
{
    private const string ValidHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void Validate_AcceptsValidManifestAndJsonRoundTrip()
    {
        var manifest = CreateManifest([
            new BackupManifestEntry(
                "projects/demo/readme.md",
                42,
                ValidHash,
                BackupDataKind.Project,
                RestoreLevel.VerifiedExact,
                DateTimeOffset.UtcNow),
        ]);

        var issues = BackupManifestValidator.Validate(manifest);
        var restored = BackupManifestJson.Deserialize(BackupManifestJson.Serialize(manifest));

        Assert.Empty(issues);
        Assert.Equal(manifest.FormatVersion, restored.FormatVersion);
        Assert.Equal(manifest.BackupId, restored.BackupId);
        Assert.Equal(manifest.ProducerVersion, restored.ProducerVersion);
        Assert.Equal(manifest.Entries, restored.Entries);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("projects/../../outside.txt")]
    [InlineData("C:\\outside.txt")]
    [InlineData("payload/file.txt:stream")]
    [InlineData("payload/CON.txt")]
    [InlineData("payload/a//b.txt")]
    public void Validate_RejectsUnsafePaths(string relativePath)
    {
        var manifest = CreateManifest([
            new BackupManifestEntry(
                relativePath,
                1,
                ValidHash,
                BackupDataKind.Project,
                RestoreLevel.VerifiedExact,
                DateTimeOffset.UtcNow),
        ]);

        var issues = BackupManifestValidator.Validate(manifest);

        Assert.Contains(issues, issue => issue.Code == "MANIFEST_UNSAFE_PATH");
    }

    [Fact]
    public void Validate_RejectsDuplicatePathsAndInvalidHash()
    {
        var manifest = CreateManifest([
            new BackupManifestEntry("projects/a.txt", 1, "invalid", BackupDataKind.Project, RestoreLevel.VerifiedExact, DateTimeOffset.UtcNow),
            new BackupManifestEntry("PROJECTS/A.TXT", 1, ValidHash, BackupDataKind.Project, RestoreLevel.VerifiedExact, DateTimeOffset.UtcNow),
        ]);

        var issues = BackupManifestValidator.Validate(manifest);

        Assert.Contains(issues, issue => issue.Code == "MANIFEST_INVALID_SHA256");
        Assert.Contains(issues, issue => issue.Code == "MANIFEST_DUPLICATE_PATH");
    }

    [Fact]
    public void Validate_AllowsEmptyManifestForEmptySelectedDirectories()
    {
        var issues = BackupManifestValidator.Validate(CreateManifest([]));

        Assert.Empty(issues);
    }

    private static BackupManifest CreateManifest(IReadOnlyList<BackupManifestEntry> entries) => new(
        BackupManifest.CurrentFormatVersion,
        "backup-1",
        DateTimeOffset.UtcNow,
        "0.1.0",
        entries);
}
