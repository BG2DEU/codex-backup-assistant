using CodexBackup.Core.Backup;

namespace CodexBackup.Core.Restore;

public sealed record RestorePlanItem(
    string PackageItemId,
    string DisplayName,
    string SourceRelativeRoot,
    string TargetPath,
    BackupDataKind Kind,
    RestoreLevel RestoreLevel,
    RestoreConflictPolicy ConflictPolicy,
    RestoreItemState State,
    string Reason,
    bool SourceWasDirectory);
