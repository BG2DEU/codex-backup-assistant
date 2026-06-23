namespace CodexBackup.Core.Codex;

public sealed record CodexUsageStatus(
    int RunningProcessCount,
    int LockedDatabaseCount,
    int DatabaseSidecarCount,
    IReadOnlyList<string> WarningCodes)
{
    public bool CanCreateNativeSnapshot => RunningProcessCount == 0 && LockedDatabaseCount == 0;
}
