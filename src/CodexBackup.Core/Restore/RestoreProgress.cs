namespace CodexBackup.Core.Restore;

public enum RestoreStage
{
    VerifyingPackage,
    Planning,
    CreatingRollback,
    Restoring,
    VerifyingRestoredData,
    Completed,
    Failed,
    RolledBack,
}

public sealed record RestoreProgress(
    RestoreStage Stage,
    string? CurrentItem,
    long CompletedFiles,
    long TotalFiles,
    long ProcessedBytes,
    long TotalBytes);
