namespace CodexBackup.Core.Restore;

public sealed record RestorePlan(
    string BackupId,
    string PackagePath,
    string RollbackRoot,
    IReadOnlyList<RestorePlanItem> Items,
    IReadOnlyList<RestoreIssue> Issues)
{
    public bool CanRestore =>
        Items.Any(item => item.State is RestoreItemState.Ready) &&
        Issues.All(issue => !issue.IsBlocking);
}
