namespace CodexBackup.Core.Discovery;

public sealed record ProjectDiscoveryRequest(
    IReadOnlyList<string> SessionRoots,
    IReadOnlyList<string> SupplementalRoots,
    IReadOnlyList<string> ManuallyAddedPaths,
    int MaximumSupplementalDepth = 6);
