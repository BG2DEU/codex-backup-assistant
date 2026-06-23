namespace CodexBackup.Core.Backup;

public enum BackupPlanItemState
{
    Included,
    InventoryOnly,
    ReviewRequired,
    ExcludedCredential,
    ExcludedVolatile,
    ExcludedByUser,
}
