namespace CodexBackup.Core.Manifest;

public sealed record BackupManifest(
    string FormatVersion,
    string BackupId,
    DateTimeOffset CreatedAtUtc,
    string ProducerVersion,
    IReadOnlyList<BackupManifestEntry> Entries)
{
    public const string CurrentFormatVersion = "1.0";
}
