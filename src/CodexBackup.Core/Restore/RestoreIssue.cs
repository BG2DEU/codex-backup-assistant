namespace CodexBackup.Core.Restore;

public sealed record RestoreIssue(
    string Code,
    string Message,
    bool IsBlocking,
    string? ItemId = null);
