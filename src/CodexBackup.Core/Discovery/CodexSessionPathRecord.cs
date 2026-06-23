namespace CodexBackup.Core.Discovery;

public sealed record CodexSessionPathRecord(
    string SessionFilePath,
    string WorkingDirectory,
    bool DirectoryExists);
