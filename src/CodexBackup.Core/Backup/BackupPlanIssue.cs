namespace CodexBackup.Core.Backup;

public enum BackupPlanIssueSeverity
{
    Information,
    Warning,
    Blocking,
}

public sealed record BackupPlanIssue(
    string Code,
    BackupPlanIssueSeverity Severity,
    string Message);
