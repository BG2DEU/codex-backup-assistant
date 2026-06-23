namespace CodexBackup.Core.Backup;

public sealed class BackupPlanBuilder(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public BackupPlan Build(IEnumerable<BackupCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var items = new List<BackupPlanItem>();
        var issues = new List<BackupPlanIssue>();
        var sourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            ValidateCandidate(candidate);

            var normalizedPath = Path.GetFullPath(candidate.SourcePath);
            if (!sourcePaths.Add(normalizedPath))
            {
                throw new ArgumentException($"Duplicate source path: {normalizedPath}", nameof(candidates));
            }

            var state = ResolveState(candidate);
            items.Add(new BackupPlanItem(
                candidate.Id,
                candidate.DisplayName,
                normalizedPath,
                candidate.Kind,
                candidate.Policy,
                candidate.RestoreLevel,
                candidate.EstimatedBytes,
                state,
                candidate.DiscoverySources));
        }

        if (items.Count == 0 || items.All(item => item.State is not BackupPlanItemState.Included))
        {
            issues.Add(new BackupPlanIssue(
                "PLAN_NO_INCLUDED_ITEMS",
                BackupPlanIssueSeverity.Blocking,
                "没有可导出的数据。"));
        }

        var reviewRequired = items.Count(item => item.State is BackupPlanItemState.ReviewRequired);
        if (reviewRequired > 0)
        {
            issues.Add(new BackupPlanIssue(
                "PLAN_REVIEW_REQUIRED",
                BackupPlanIssueSeverity.Blocking,
                $"有 {reviewRequired} 项未知数据需要确认。"));
        }

        var overlappingPairs = FindOverlappingIncludedPaths(items);
        if (overlappingPairs.Count > 0)
        {
            var examples = string.Join("；", overlappingPairs
                .Take(3)
                .Select(pair => $"“{pair.Parent.DisplayName}”包含“{pair.Child.DisplayName}”"));
            var remaining = overlappingPairs.Count > 3
                ? $"；另有 {overlappingPairs.Count - 3} 组"
                : string.Empty;
            issues.Add(new BackupPlanIssue(
                "PLAN_OVERLAPPING_INCLUDED_PATHS",
                BackupPlanIssueSeverity.Blocking,
                $"已选项目路径重叠：{examples}{remaining}。请只保留需要的项目。"));
        }

        return new BackupPlan(
            Guid.NewGuid().ToString("N"),
            _timeProvider.GetUtcNow(),
            items,
            issues);
    }

    private static IReadOnlyList<(BackupPlanItem Parent, BackupPlanItem Child)>
        FindOverlappingIncludedPaths(IReadOnlyList<BackupPlanItem> items)
    {
        var includedItems = items
            .Where(item => item.State is BackupPlanItemState.Included)
            .ToArray();
        var pairs = new List<(BackupPlanItem Parent, BackupPlanItem Child)>();

        for (var outer = 0; outer < includedItems.Length; outer++)
        {
            for (var inner = outer + 1; inner < includedItems.Length; inner++)
            {
                if (IsDescendant(includedItems[outer].SourcePath, includedItems[inner].SourcePath))
                {
                    pairs.Add((includedItems[outer], includedItems[inner]));
                }
                else if (IsDescendant(includedItems[inner].SourcePath, includedItems[outer].SourcePath))
                {
                    pairs.Add((includedItems[inner], includedItems[outer]));
                }
            }
        }

        return pairs;
    }

    private static bool IsDescendant(string parentPath, string candidatePath)
    {
        var relativePath = Path.GetRelativePath(parentPath, candidatePath);
        return relativePath != "." &&
               !Path.IsPathFullyQualified(relativePath) &&
               relativePath != ".." &&
               !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static void ValidateCandidate(BackupCandidate candidate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.DisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.SourcePath);

        if (!Path.IsPathFullyQualified(candidate.SourcePath))
        {
            throw new ArgumentException("Source path must be absolute.", nameof(candidate));
        }

        if (candidate.EstimatedBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(candidate), "Estimated bytes cannot be negative.");
        }
    }

    private static BackupPlanItemState ResolveState(BackupCandidate candidate) => candidate.Policy switch
    {
        BackupPolicy.ExcludeCredential => BackupPlanItemState.ExcludedCredential,
        BackupPolicy.ExcludeVolatile => BackupPlanItemState.ExcludedVolatile,
        BackupPolicy.InventoryOnly => BackupPlanItemState.InventoryOnly,
        BackupPolicy.UnknownReviewRequired when !candidate.IsSelected => BackupPlanItemState.ExcludedByUser,
        BackupPolicy.UnknownReviewRequired when !candidate.IsReviewApproved => BackupPlanItemState.ReviewRequired,
        _ when candidate.IsSelected => BackupPlanItemState.Included,
        _ => BackupPlanItemState.ExcludedByUser,
    };
}
