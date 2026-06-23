using CodexBackup.Core.Discovery;

namespace CodexBackup.Infrastructure.Windows.Discovery;

public sealed record SupplementalProjectScanResult(
    IReadOnlyList<ProjectRootResolution> Projects,
    IReadOnlyList<DiscoveryWarning> Warnings);
