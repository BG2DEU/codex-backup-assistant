using System.Security.Cryptography;
using CodexBackup.Core.Codex;
using CodexBackup.Core.Export;
using CodexBackup.Core.Manifest;

namespace CodexBackup.Infrastructure.Windows.Export;

public sealed class BackupPackageVerifier
{
    private const int ReadBufferSize = 1024 * 1024;

    public async Task<BackupVerificationResult> VerifyAsync(
        string packagePath,
        IProgress<BackupExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);

        var issues = new List<BackupExportIssue>();
        long verifiedFiles = 0;
        long verifiedBytes = 0;

        try
        {
            var normalizedPackagePath = Path.GetFullPath(packagePath);
            if (!Directory.Exists(normalizedPackagePath))
            {
                issues.Add(new BackupExportIssue(
                    "verification",
                    "PACKAGE_MISSING",
                    "The backup package directory does not exist."));
                return new BackupVerificationResult(false, 0, 0, issues);
            }

            if (File.Exists(Path.Combine(normalizedPackagePath, "INCOMPLETE.json")) ||
                normalizedPackagePath.EndsWith(".incomplete", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new BackupExportIssue(
                    "verification",
                    "PACKAGE_INCOMPLETE",
                    "The backup package is marked as incomplete."));
                return new BackupVerificationResult(false, 0, 0, issues);
            }

            var manifestPath = Path.Combine(normalizedPackagePath, "manifest.json");
            var indexPath = Path.Combine(normalizedPackagePath, "package-index.json");
            if (!File.Exists(manifestPath) || !File.Exists(indexPath))
            {
                issues.Add(new BackupExportIssue(
                    "verification",
                    "PACKAGE_METADATA_MISSING",
                    "The manifest or package index is missing."));
                return new BackupVerificationResult(false, 0, 0, issues);
            }

            var manifest = BackupManifestJson.Deserialize(
                await File.ReadAllTextAsync(manifestPath, cancellationToken));
            var index = BackupPackageIndexJson.Deserialize(
                await File.ReadAllTextAsync(indexPath, cancellationToken));
            foreach (var issue in BackupManifestValidator.Validate(manifest))
            {
                issues.Add(new BackupExportIssue(
                    "verification",
                    issue.Code,
                    issue.Message,
                    issue.RelativePath));
            }

            foreach (var issue in BackupPackageIndexValidator.Validate(index))
            {
                issues.Add(new BackupExportIssue(
                    "verification",
                    issue.Code,
                    issue.Message,
                    issue.RelativePath));
            }

            if (!string.Equals(manifest.BackupId, index.BackupId, StringComparison.Ordinal))
            {
                issues.Add(new BackupExportIssue(
                    "verification",
                    "PACKAGE_ID_MISMATCH",
                    "The manifest and package index backup IDs differ."));
            }

            if (issues.Count > 0)
            {
                return new BackupVerificationResult(false, 0, 0, issues);
            }

            var itemRootPrefixes = index.Items
                .Select(item => item.RelativeRoot.Replace('\\', '/').TrimEnd('/') + "/")
                .ToArray();
            foreach (var entry in manifest.Entries)
            {
                if (!itemRootPrefixes.Any(prefix =>
                        entry.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    issues.Add(new BackupExportIssue(
                        "verification",
                        "PACKAGE_ENTRY_UNMAPPED",
                        "A manifest entry does not belong to a package item.",
                        entry.RelativePath));
                }
            }

            if (issues.Count > 0)
            {
                return new BackupVerificationResult(false, 0, 0, issues);
            }

            var totalBytes = manifest.Entries.Sum(item => item.Length);
            foreach (var entry in manifest.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var targetPath = GetSafeEntryPath(normalizedPackagePath, entry.RelativePath);
                progress?.Report(new BackupExportProgress(
                    BackupExportStage.Verifying,
                    entry.RelativePath,
                    verifiedFiles,
                    manifest.Entries.Count,
                    verifiedBytes,
                    totalBytes));

                if (!File.Exists(targetPath))
                {
                    issues.Add(new BackupExportIssue(
                        "verification",
                        "TARGET_FILE_MISSING",
                        "A manifest file is missing from the package.",
                        entry.RelativePath));
                    continue;
                }

                var fileInfo = new FileInfo(targetPath);
                if (fileInfo.Length != entry.Length)
                {
                    issues.Add(new BackupExportIssue(
                        "verification",
                        "TARGET_LENGTH_MISMATCH",
                        "A package file length differs from the manifest.",
                        entry.RelativePath));
                    continue;
                }

                await using var stream = new FileStream(
                    targetPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    ReadBufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var hash = Convert.ToHexString(
                    await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
                if (!string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(new BackupExportIssue(
                        "verification",
                        "TARGET_HASH_MISMATCH",
                        "A package file hash differs from the manifest.",
                        entry.RelativePath));
                    continue;
                }

                verifiedFiles++;
                verifiedBytes += entry.Length;
            }

            if (issues.Count == 0)
            {
                await ValidatePortableConversationsAsync(
                    normalizedPackagePath,
                    manifest,
                    issues,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            issues.Add(new BackupExportIssue(
                "verification",
                "PACKAGE_VERIFICATION_FAILED",
                exception.Message,
                IsRetryable: exception is IOException));
        }

        return new BackupVerificationResult(issues.Count == 0, verifiedFiles, verifiedBytes, issues);
    }

    private static async Task ValidatePortableConversationsAsync(
        string packageRoot,
        BackupManifest manifest,
        ICollection<BackupExportIssue> issues,
        CancellationToken cancellationToken)
    {
        const string portableIndexPath = "portable/conversations/index.json";
        var manifestPaths = manifest.Entries
            .Select(entry => entry.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!manifestPaths.Contains(portableIndexPath))
        {
            return;
        }

        var index = PortableConversationJson.DeserializeIndex(
            await File.ReadAllTextAsync(
                GetSafeEntryPath(packageRoot, portableIndexPath),
                cancellationToken));
        if (!string.Equals(
                index.FormatVersion,
                PortableConversationIndex.CurrentFormatVersion,
                StringComparison.Ordinal))
        {
            issues.Add(new BackupExportIssue(
                "verification",
                "CONVERSATION_INDEX_UNSUPPORTED_VERSION",
                $"Unsupported portable conversation index version: {index.FormatVersion}",
                portableIndexPath));
            return;
        }

        var sessionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var indexEntry in index.Conversations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(indexEntry.SessionId) ||
                !sessionIds.Add(indexEntry.SessionId))
            {
                issues.Add(new BackupExportIssue(
                    "verification",
                    "CONVERSATION_INDEX_DUPLICATE_SESSION",
                    "Portable conversation session IDs must be non-empty and unique.",
                    portableIndexPath));
                continue;
            }

            if (!IsPortableConversationPath(indexEntry.JsonRelativePath, ".json") ||
                !IsPortableConversationPath(indexEntry.MarkdownRelativePath, ".md") ||
                !manifestPaths.Contains(indexEntry.JsonRelativePath) ||
                !manifestPaths.Contains(indexEntry.MarkdownRelativePath))
            {
                issues.Add(new BackupExportIssue(
                    "verification",
                    "CONVERSATION_INDEX_PATH_INVALID",
                    "A portable conversation index path is unsafe or missing from the manifest.",
                    indexEntry.JsonRelativePath));
                continue;
            }

            var conversation = PortableConversationJson.DeserializeConversation(
                await File.ReadAllTextAsync(
                    GetSafeEntryPath(packageRoot, indexEntry.JsonRelativePath),
                    cancellationToken));
            if (!string.Equals(
                    conversation.FormatVersion,
                    PortableConversation.CurrentFormatVersion,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    conversation.SessionId,
                    indexEntry.SessionId,
                    StringComparison.Ordinal) ||
                conversation.Messages.Count != indexEntry.MessageCount ||
                conversation.Messages.Count(message =>
                    message.Role is PortableConversationRole.User) != indexEntry.UserMessageCount ||
                conversation.Messages.Count(message =>
                    message.Role is PortableConversationRole.Assistant) !=
                indexEntry.AssistantMessageCount ||
                conversation.RedactionCount != indexEntry.RedactionCount ||
                conversation.RollbackEventCount != indexEntry.RollbackEventCount)
            {
                issues.Add(new BackupExportIssue(
                    "verification",
                    "CONVERSATION_INDEX_CONTENT_MISMATCH",
                    "A portable conversation does not match its index entry.",
                    indexEntry.JsonRelativePath));
            }

            for (var messageIndex = 0; messageIndex < conversation.Messages.Count; messageIndex++)
            {
                var message = conversation.Messages[messageIndex];
                if (message.Sequence != messageIndex + 1 ||
                    string.IsNullOrWhiteSpace(message.Text))
                {
                    issues.Add(new BackupExportIssue(
                        "verification",
                        "CONVERSATION_MESSAGE_INVALID",
                        "Portable conversation message sequences must be contiguous and text cannot be empty.",
                        indexEntry.JsonRelativePath));
                    break;
                }
            }
        }
    }

    private static bool IsPortableConversationPath(string relativePath, string extension) =>
        BackupPathRules.IsSafeRelativePath(relativePath) &&
        relativePath.StartsWith(
            "portable/conversations/",
            StringComparison.OrdinalIgnoreCase) &&
        relativePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase);

    private static string GetSafeEntryPath(string packageRoot, string relativePath)
    {
        var targetPath = Path.GetFullPath(Path.Combine(
            packageRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(packageRoot, targetPath);
        if (relative == ".." ||
            Path.IsPathFullyQualified(relative) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new IOException("A manifest path escapes the package directory.");
        }

        return targetPath;
    }
}
