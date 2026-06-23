using CodexBackup.Core.Discovery;

namespace CodexBackup.Core.Backup;

public sealed record BackupPlanItem(
    string Id,
    string DisplayName,
    string SourcePath,
    BackupDataKind Kind,
    BackupPolicy Policy,
    RestoreLevel RestoreLevel,
    long EstimatedBytes,
    BackupPlanItemState State,
    ProjectDiscoverySource DiscoverySources);
