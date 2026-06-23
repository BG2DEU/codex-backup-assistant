namespace CodexBackup.Core.Export;

public sealed record BackupExportIssue(
    string Stage,
    string Code,
    string Message,
    string? RelativePath = null,
    bool IsRetryable = false);
