namespace CodexBackup.Core.Discovery;

public sealed record ProjectGitStatus(
    string? BranchName,
    bool IsDetachedHead,
    bool HasRemote,
    int ChangedTrackedFileCount,
    int UntrackedFileCount,
    string? AheadBehindSummary = null,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    public bool IsAvailable => ErrorCode is null;

    public bool HasLocalChanges => ChangedTrackedFileCount > 0 || UntrackedFileCount > 0;

    public bool IsClean => IsAvailable && !HasLocalChanges;
}
