using CodexBackup.Core.Codex;

namespace CodexBackup.Core.Backup;

public static class CodexSnapshotPlanGuard
{
    public static BackupPlan Apply(
        BackupPlan plan,
        IEnumerable<CodexDataItem> codexItems,
        CodexUsageStatus usageStatus)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(codexItems);
        ArgumentNullException.ThrowIfNull(usageStatus);

        var snapshotItemIds = codexItems
            .Where(item => item.RequiresCodexStopped)
            .Select(item => item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var includesNativeSnapshot = plan.Items.Any(item =>
            item.State is BackupPlanItemState.Included && snapshotItemIds.Contains(item.Id));
        if (!includesNativeSnapshot || usageStatus.CanCreateNativeSnapshot)
        {
            return plan;
        }

        if (plan.Issues.Any(issue => issue.Code == "CODEX_MUST_BE_STOPPED"))
        {
            return plan;
        }

        return plan with
        {
            Issues = plan.Issues
                .Append(new BackupPlanIssue(
                    "CODEX_MUST_BE_STOPPED",
                    BackupPlanIssueSeverity.Blocking,
                    "Codex or its state databases are still in use. Stop Codex and scan again."))
                .ToArray(),
        };
    }
}
