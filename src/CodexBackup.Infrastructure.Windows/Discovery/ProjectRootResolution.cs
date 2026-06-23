using CodexBackup.Core.Discovery;

namespace CodexBackup.Infrastructure.Windows.Discovery;

public sealed record ProjectRootResolution(
    string RootPath,
    ProjectDiscoverySource Sources,
    IReadOnlyList<string> Markers);
