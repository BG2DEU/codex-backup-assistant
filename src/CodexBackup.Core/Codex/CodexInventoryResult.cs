using CodexBackup.Core.Discovery;

namespace CodexBackup.Core.Codex;

public sealed record CodexInventoryResult(
    string RootPath,
    string AdapterVersion,
    IReadOnlyList<CodexDataItem> Items,
    IReadOnlyList<DiscoveryWarning> Warnings)
{
    public long TotalBytes => Items.Sum(item => item.EstimatedBytes);
}
