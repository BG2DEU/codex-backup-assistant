using CodexBackup.Core.Backup;
using CodexBackup.Core.Discovery;

namespace CodexBackup.Core.Manifest;

public sealed record BackupPackageIndex(
    string FormatVersion,
    string BackupId,
    string PlanId,
    DateTimeOffset CreatedAtUtc,
    string ProducerVersion,
    IReadOnlyList<BackupPackageItem> Items,
    string? CodexAdapterVersion = null,
    string? SourceCodexVersion = null)
{
    public const string CurrentFormatVersion = "1.0";
}

public sealed record BackupPackageItem(
    string Id,
    string DisplayName,
    string OriginalSourcePath,
    string RelativeRoot,
    BackupDataKind Kind,
    RestoreLevel RestoreLevel,
    ProjectDiscoverySource DiscoverySources,
    bool SourceWasDirectory = true);
