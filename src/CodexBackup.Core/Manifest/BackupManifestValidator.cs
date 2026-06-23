namespace CodexBackup.Core.Manifest;

public static class BackupManifestValidator
{
    public static IReadOnlyList<ManifestValidationIssue> Validate(BackupManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var issues = new List<ManifestValidationIssue>();
        if (!string.Equals(
                manifest.FormatVersion,
                BackupManifest.CurrentFormatVersion,
                StringComparison.Ordinal))
        {
            issues.Add(new ManifestValidationIssue(
                "MANIFEST_UNSUPPORTED_VERSION",
                $"Unsupported manifest version: {manifest.FormatVersion}"));
        }

        if (string.IsNullOrWhiteSpace(manifest.BackupId))
        {
            issues.Add(new ManifestValidationIssue("MANIFEST_MISSING_BACKUP_ID", "Backup ID is required."));
        }

        if (string.IsNullOrWhiteSpace(manifest.ProducerVersion))
        {
            issues.Add(new ManifestValidationIssue("MANIFEST_MISSING_PRODUCER_VERSION", "Producer version is required."));
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Entries)
        {
            ValidateEntry(entry, paths, issues);
        }

        return issues;
    }

    private static void ValidateEntry(
        BackupManifestEntry entry,
        HashSet<string> paths,
        List<ManifestValidationIssue> issues)
    {
        if (!BackupPathRules.IsSafeRelativePath(entry.RelativePath))
        {
            issues.Add(new ManifestValidationIssue(
                "MANIFEST_UNSAFE_PATH",
                "Entry path must be relative and cannot contain parent traversal.",
                entry.RelativePath));
        }

        var normalizedPath = entry.RelativePath.Replace('\\', '/');
        if (!paths.Add(normalizedPath))
        {
            issues.Add(new ManifestValidationIssue(
                "MANIFEST_DUPLICATE_PATH",
                "Manifest contains a duplicate path.",
                entry.RelativePath));
        }

        if (entry.Length < 0)
        {
            issues.Add(new ManifestValidationIssue(
                "MANIFEST_INVALID_LENGTH",
                "Entry length cannot be negative.",
                entry.RelativePath));
        }

        if (!IsSha256(entry.Sha256))
        {
            issues.Add(new ManifestValidationIssue(
                "MANIFEST_INVALID_SHA256",
                "Entry SHA-256 must contain exactly 64 hexadecimal characters.",
                entry.RelativePath));
        }

        if (entry.LastWriteTimeUtc == default)
        {
            issues.Add(new ManifestValidationIssue(
                "MANIFEST_INVALID_MODIFIED_TIME",
                "Entry modified time is required.",
                entry.RelativePath));
        }
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);
}
