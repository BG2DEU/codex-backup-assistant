using CodexBackup.Core.Backup;

namespace CodexBackup.Core.Manifest;

public sealed record BackupManifestEntry(
    string RelativePath,
    long Length,
    string Sha256,
    BackupDataKind Kind,
    RestoreLevel RestoreLevel,
    DateTimeOffset LastWriteTimeUtc);
