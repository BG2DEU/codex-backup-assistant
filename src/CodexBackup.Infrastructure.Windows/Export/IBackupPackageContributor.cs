using CodexBackup.Core.Backup;
using CodexBackup.Core.Export;
using CodexBackup.Core.Manifest;

namespace CodexBackup.Infrastructure.Windows.Export;

public interface IBackupPackageContributor
{
    long EstimateAdditionalBytes(BackupPlan plan);

    Task<BackupContributionResult> ContributeAsync(
        BackupContributionContext context,
        CancellationToken cancellationToken);
}

public sealed record BackupContributionContext(
    BackupPlan Plan,
    string PackageRoot,
    string BackupId,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<BackupPackageItem> CopiedItems);

public sealed record BackupContributionResult(
    IReadOnlyList<GeneratedPackageFile> Files,
    IReadOnlyList<BackupPackageItem> Items,
    IReadOnlyList<BackupExportIssue> Issues)
{
    public static BackupContributionResult Empty { get; } = new([], [], []);
}

public sealed record GeneratedPackageFile(
    string RelativePath,
    BackupDataKind Kind,
    RestoreLevel RestoreLevel);
