namespace CodexBackup.Core.Backup;

public sealed record BackupPlan(
    string PlanId,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<BackupPlanItem> Items,
    IReadOnlyList<BackupPlanIssue> Issues)
{
    public long IncludedBytes => Items
        .Where(item => item.State is BackupPlanItemState.Included)
        .Sum(item => item.EstimatedBytes);

    public int ReviewRequiredCount => Items.Count(item => item.State is BackupPlanItemState.ReviewRequired);

    public bool CanExport =>
        Items.Any(item => item.State is BackupPlanItemState.Included) &&
        Issues.All(issue => issue.Severity is not BackupPlanIssueSeverity.Blocking);
}
