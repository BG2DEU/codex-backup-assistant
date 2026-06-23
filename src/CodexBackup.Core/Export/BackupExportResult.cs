namespace CodexBackup.Core.Export;

public sealed record BackupExportResult(
    string BackupId,
    BackupExportStatus Status,
    string? CompletedPackagePath,
    string? IncompletePackagePath,
    long CopiedFileCount,
    long CopiedBytes,
    IReadOnlyList<BackupExportIssue> Issues)
{
    public bool IsSuccess => Status is BackupExportStatus.Success;

    public bool IsCompleted => Status is BackupExportStatus.Success or BackupExportStatus.PartialSuccess;
}
