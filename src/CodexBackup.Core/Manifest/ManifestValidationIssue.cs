namespace CodexBackup.Core.Manifest;

public sealed record ManifestValidationIssue(
    string Code,
    string Message,
    string? RelativePath = null);
