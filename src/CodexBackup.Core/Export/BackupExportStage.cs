namespace CodexBackup.Core.Export;

public enum BackupExportStage
{
    Planning,
    Copying,
    Transforming,
    Verifying,
    Committing,
    Completed,
    Failed,
    Cancelled,
}
