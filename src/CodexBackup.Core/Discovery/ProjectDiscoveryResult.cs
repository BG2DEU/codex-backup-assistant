namespace CodexBackup.Core.Discovery;

public sealed record ProjectDiscoveryResult(
    IReadOnlyList<DiscoveredProject> Projects,
    IReadOnlyList<DiscoveryWarning> Warnings,
    int SessionFileCount,
    int SessionPathRecordCount,
    int UniqueSessionPathCount);
