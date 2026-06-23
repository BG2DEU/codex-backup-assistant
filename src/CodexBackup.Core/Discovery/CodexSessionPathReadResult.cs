namespace CodexBackup.Core.Discovery;

public sealed record CodexSessionPathReadResult(
    IReadOnlyList<CodexSessionPathRecord> Records,
    IReadOnlyList<DiscoveryWarning> Warnings,
    int ScannedFileCount)
{
    public IReadOnlyList<string> UniqueWorkingDirectories => Records
        .Select(record => record.WorkingDirectory)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}
