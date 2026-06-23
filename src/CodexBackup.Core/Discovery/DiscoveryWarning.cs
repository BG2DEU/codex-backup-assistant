namespace CodexBackup.Core.Discovery;

public sealed record DiscoveryWarning(
    string Code,
    string Path,
    string Message);
