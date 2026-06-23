namespace CodexBackup.Core.Restore;

public enum RestoreStatus
{
    Success,
    PartialSuccess,
    Failed,
    RolledBack,
    Cancelled,
}

public sealed record RestoreResult(
    RestoreStatus Status,
    string BackupId,
    string? RollbackPath,
    long RestoredFileCount,
    long RestoredBytes,
    IReadOnlyList<RestorePlanItem> Items,
    IReadOnlyList<RestoreIssue> Issues,
    string? ReportJsonPath = null,
    string? ReportHtmlPath = null);
