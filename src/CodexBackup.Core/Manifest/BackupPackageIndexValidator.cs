namespace CodexBackup.Core.Manifest;

public static class BackupPackageIndexValidator
{
    public static IReadOnlyList<ManifestValidationIssue> Validate(BackupPackageIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);

        var issues = new List<ManifestValidationIssue>();
        if (!string.Equals(
                index.FormatVersion,
                BackupPackageIndex.CurrentFormatVersion,
                StringComparison.Ordinal))
        {
            issues.Add(new ManifestValidationIssue(
                "INDEX_UNSUPPORTED_VERSION",
                $"Unsupported package index version: {index.FormatVersion}"));
        }

        if (string.IsNullOrWhiteSpace(index.BackupId))
        {
            issues.Add(new ManifestValidationIssue("INDEX_MISSING_BACKUP_ID", "Backup ID is required."));
        }

        if (string.IsNullOrWhiteSpace(index.PlanId))
        {
            issues.Add(new ManifestValidationIssue("INDEX_MISSING_PLAN_ID", "Plan ID is required."));
        }

        if (string.IsNullOrWhiteSpace(index.ProducerVersion))
        {
            issues.Add(new ManifestValidationIssue("INDEX_MISSING_PRODUCER_VERSION", "Producer version is required."));
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in index.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Id) || !ids.Add(item.Id))
            {
                issues.Add(new ManifestValidationIssue(
                    "INDEX_INVALID_ITEM_ID",
                    "Package item IDs must be non-empty and unique.",
                    item.Id));
            }

            if (string.IsNullOrWhiteSpace(item.DisplayName))
            {
                issues.Add(new ManifestValidationIssue(
                    "INDEX_MISSING_DISPLAY_NAME",
                    "Package item display name is required.",
                    item.Id));
            }

            if (!Path.IsPathFullyQualified(item.OriginalSourcePath))
            {
                issues.Add(new ManifestValidationIssue(
                    "INDEX_INVALID_SOURCE_PATH",
                    "Original source path must be absolute metadata.",
                    item.Id));
            }

            var normalizedRoot = item.RelativeRoot.Replace('\\', '/');
            if (!BackupPathRules.IsSafeRelativePath(normalizedRoot) || !roots.Add(normalizedRoot))
            {
                issues.Add(new ManifestValidationIssue(
                    "INDEX_INVALID_RELATIVE_ROOT",
                    "Package item roots must be safe and unique relative paths.",
                    item.RelativeRoot));
            }
        }

        return issues;
    }
}
