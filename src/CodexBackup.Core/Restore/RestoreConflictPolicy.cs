namespace CodexBackup.Core.Restore;

public enum RestoreConflictPolicy
{
    KeepBoth,
    SkipExisting,
    MergePreserveExisting,
    ReplaceWithRollback,
}
