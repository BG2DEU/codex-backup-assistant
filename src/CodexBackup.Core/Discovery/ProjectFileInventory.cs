namespace CodexBackup.Core.Discovery;

public sealed record ProjectFileInventory(
    long FileCount,
    long TotalBytes,
    int LargeFileCount,
    long LargestFileBytes,
    int PotentialSecretFileCount,
    int SkippedReparsePointCount,
    int UnreadableItemCount)
{
    public bool IsComplete => UnreadableItemCount == 0;

    public bool HasRiskIndicators => LargeFileCount > 0 || PotentialSecretFileCount > 0;
}
