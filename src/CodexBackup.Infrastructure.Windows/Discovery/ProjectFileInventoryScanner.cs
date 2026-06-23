using CodexBackup.Core.Discovery;

namespace CodexBackup.Infrastructure.Windows.Discovery;

public sealed class ProjectFileInventoryScanner(long largeFileThresholdBytes = 100L * 1024 * 1024)
{
    private static readonly HashSet<string> PotentialSecretFileNames = new(
        [
            ".env",
            ".npmrc",
            ".pypirc",
            ".netrc",
            "credentials.json",
            "token.json",
            "secrets.json",
            "id_rsa",
            "id_ed25519",
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> PotentialSecretExtensions = new(
        [".pem", ".key", ".pfx", ".p12", ".ppk"],
        StringComparer.OrdinalIgnoreCase);

    private readonly long _largeFileThresholdBytes = largeFileThresholdBytes > 0
        ? largeFileThresholdBytes
        : throw new ArgumentOutOfRangeException(nameof(largeFileThresholdBytes));

    public ProjectFileInventory Scan(string projectRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);

        long fileCount = 0;
        long totalBytes = 0;
        long largestFileBytes = 0;
        var largeFileCount = 0;
        var potentialSecretFileCount = 0;
        var skippedReparsePointCount = 0;
        var unreadableItemCount = 0;
        var pendingDirectories = new Queue<string>();
        pendingDirectories.Enqueue(projectRoot);

        while (pendingDirectories.TryDequeue(out var currentDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(currentDirectory);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                unreadableItemCount++;
                continue;
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var attributes = File.GetAttributes(entry);
                    if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        skippedReparsePointCount++;
                        continue;
                    }

                    if (attributes.HasFlag(FileAttributes.Directory))
                    {
                        pendingDirectories.Enqueue(entry);
                        continue;
                    }

                    var fileLength = new FileInfo(entry).Length;
                    fileCount++;
                    totalBytes += fileLength;
                    largestFileBytes = Math.Max(largestFileBytes, fileLength);
                    if (fileLength >= _largeFileThresholdBytes)
                    {
                        largeFileCount++;
                    }

                    if (IsPotentialSecretFile(entry))
                    {
                        potentialSecretFileCount++;
                    }
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
                {
                    unreadableItemCount++;
                }
            }
        }

        return new ProjectFileInventory(
            fileCount,
            totalBytes,
            largeFileCount,
            largestFileBytes,
            potentialSecretFileCount,
            skippedReparsePointCount,
            unreadableItemCount);
    }

    private static bool IsPotentialSecretFile(string path)
    {
        var fileName = Path.GetFileName(path);
        return PotentialSecretFileNames.Contains(fileName) ||
               fileName.StartsWith(".env.", StringComparison.OrdinalIgnoreCase) ||
               PotentialSecretExtensions.Contains(Path.GetExtension(fileName));
    }
}
