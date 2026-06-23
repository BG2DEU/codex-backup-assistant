using CodexBackup.Core.Discovery;

namespace CodexBackup.Core.Backup;

public sealed record BackupCandidate(
    string Id,
    string DisplayName,
    string SourcePath,
    BackupDataKind Kind,
    BackupPolicy Policy,
    RestoreLevel RestoreLevel,
    long EstimatedBytes,
    bool IsSelected,
    bool IsReviewApproved = false,
    ProjectDiscoverySource DiscoverySources = ProjectDiscoverySource.None);
