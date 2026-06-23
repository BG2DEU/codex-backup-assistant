namespace CodexBackup.Core.Export;

public sealed record BackupExportProgress(
    BackupExportStage Stage,
    string? CurrentItem,
    long CompletedFiles,
    long TotalFiles,
    long ProcessedBytes,
    long TotalBytes);
