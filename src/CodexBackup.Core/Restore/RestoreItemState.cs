namespace CodexBackup.Core.Restore;

public enum RestoreItemState
{
    Ready,
    SkippedByUser,
    SkippedIncompatible,
    SkippedExisting,
    Restored,
    Failed,
    RolledBack,
}
