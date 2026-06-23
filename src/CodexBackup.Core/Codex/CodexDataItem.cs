using CodexBackup.Core.Backup;

namespace CodexBackup.Core.Codex;

public sealed record CodexDataItem(
    string Id,
    string Name,
    string FullPath,
    bool IsDirectory,
    BackupDataKind Kind,
    BackupPolicy Policy,
    RestoreLevel RestoreLevel,
    long FileCount,
    long EstimatedBytes,
    string ClassificationReason,
    bool RequiresCodexStopped,
    bool ContainsPotentialSecrets);
