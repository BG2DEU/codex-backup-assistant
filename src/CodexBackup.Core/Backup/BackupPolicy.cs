namespace CodexBackup.Core.Backup;

public enum BackupPolicy
{
    Include,
    IncludePortableAndNative,
    InventoryOnly,
    ExcludeCredential,
    ExcludeVolatile,
    UnknownReviewRequired,
}
