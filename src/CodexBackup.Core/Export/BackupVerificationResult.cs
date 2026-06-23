namespace CodexBackup.Core.Export;

public sealed record BackupVerificationResult(
    bool IsValid,
    long VerifiedFileCount,
    long VerifiedBytes,
    IReadOnlyList<BackupExportIssue> Issues);
