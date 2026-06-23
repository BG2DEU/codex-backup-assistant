using System.Security.Cryptography;
using System.Text;
using CodexBackup.Core.Backup;
using CodexBackup.Core.Codex;
using CodexBackup.Core.Discovery;
using CodexBackup.Core.Export;
using CodexBackup.Core.Manifest;
using CodexBackup.Infrastructure.Windows.Export;

namespace CodexBackup.Infrastructure.Windows.Codex;

public sealed class CodexConversationBackupContributor(
    CodexConversationParser? parser = null,
    TimeProvider? timeProvider = null) : IBackupPackageContributor
{
    private const string PortableRoot = "portable/conversations";
    private readonly CodexConversationParser _parser = parser ?? new CodexConversationParser();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public long EstimateAdditionalBytes(BackupPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Items
            .Where(item =>
                item.State is BackupPlanItemState.Included &&
                item.Kind is BackupDataKind.CodexSession)
            .Sum(item => item.EstimatedBytes);
    }

    public async Task<BackupContributionResult> ContributeAsync(
        BackupContributionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var sessionItems = context.CopiedItems
            .Where(item => item.Kind is BackupDataKind.CodexSession)
            .ToArray();
        if (sessionItems.Length == 0)
        {
            return BackupContributionResult.Empty;
        }

        var issues = new List<BackupExportIssue>();
        var conversations = new List<PortableConversation>();
        foreach (var sessionItem in sessionItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var copiedSessionRoot = GetSafePackagePath(context.PackageRoot, sessionItem.RelativeRoot);
            if (!Directory.Exists(copiedSessionRoot))
            {
                issues.Add(new BackupExportIssue(
                    "transforming",
                    "CONVERSATION_SOURCE_SNAPSHOT_MISSING",
                    "A copied Codex session snapshot directory is missing.",
                    sessionItem.RelativeRoot));
                continue;
            }

            foreach (var sessionFile in Directory.EnumerateFiles(
                         copiedSessionRoot,
                         "*.jsonl",
                         new EnumerationOptions
                         {
                             RecurseSubdirectories = true,
                             IgnoreInaccessible = true,
                             AttributesToSkip = FileAttributes.ReparsePoint,
                         }))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var conversation = _parser.Parse(sessionFile);
                    var associatedProjectIds = FindAssociatedProjects(
                        conversation.WorkingDirectory,
                        context.CopiedItems);
                    conversation = conversation with
                    {
                        AssociatedProjectIds = associatedProjectIds,
                    };
                    conversations.Add(conversation);
                    foreach (var warning in conversation.Warnings)
                    {
                        issues.Add(new BackupExportIssue(
                            "transforming",
                            warning.Code,
                            warning.Message,
                            warning.LineNumber is null
                                ? null
                                : $"session-line-{warning.LineNumber}"));
                    }
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    issues.Add(new BackupExportIssue(
                        "transforming",
                        "CONVERSATION_READ_FAILED",
                        exception.Message,
                        IsRetryable: true));
                }
            }
        }

        var selectedConversations = SelectUniqueConversations(conversations, issues);
        var stagingRelativeRoot = $"portable/.conversations-{Guid.NewGuid():N}.tmp";
        var stagingRoot = GetSafePackagePath(context.PackageRoot, stagingRelativeRoot);
        var finalRoot = GetSafePackagePath(context.PackageRoot, PortableRoot);
        Directory.CreateDirectory(Path.Combine(stagingRoot, "json"));
        Directory.CreateDirectory(Path.Combine(stagingRoot, "markdown"));

        try
        {
            var generatedFiles = new List<GeneratedPackageFile>();
            var indexEntries = new List<PortableConversationIndexEntry>();
            foreach (var conversation in selectedConversations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var safeName = CreateSafeFileName(conversation.SessionId);
                var jsonRelativePath = $"{PortableRoot}/json/{safeName}.json";
                var markdownRelativePath = $"{PortableRoot}/markdown/{safeName}.md";
                var stagingJsonPath = Path.Combine(stagingRoot, "json", $"{safeName}.json");
                var stagingMarkdownPath = Path.Combine(stagingRoot, "markdown", $"{safeName}.md");

                await WriteUtf8Async(
                    stagingJsonPath,
                    PortableConversationJson.Serialize(conversation),
                    cancellationToken);
                await WriteUtf8Async(
                    stagingMarkdownPath,
                    PortableConversationMarkdown.Render(conversation),
                    cancellationToken);
                generatedFiles.Add(new GeneratedPackageFile(
                    jsonRelativePath,
                    BackupDataKind.CodexSession,
                    RestoreLevel.VerifiedExact));
                generatedFiles.Add(new GeneratedPackageFile(
                    markdownRelativePath,
                    BackupDataKind.CodexSession,
                    RestoreLevel.VerifiedExact));
                indexEntries.Add(new PortableConversationIndexEntry(
                    conversation.SessionId,
                    conversation.Title,
                    conversation.StartedAtUtc,
                    conversation.UpdatedAtUtc,
                    conversation.WorkingDirectory,
                    conversation.AssociatedProjectIds,
                    conversation.Messages.Count,
                    conversation.Messages.Count(message =>
                        message.Role is PortableConversationRole.User),
                    conversation.Messages.Count(message =>
                        message.Role is PortableConversationRole.Assistant),
                    conversation.RollbackEventCount,
                    conversation.RedactionCount,
                    conversation.Warnings.Count,
                    jsonRelativePath,
                    markdownRelativePath));
            }

            var index = new PortableConversationIndex(
                PortableConversationIndex.CurrentFormatVersion,
                _timeProvider.GetUtcNow(),
                CodexStorageAdapter.CurrentAdapterVersion,
                indexEntries
                    .OrderBy(entry => entry.StartedAtUtc)
                    .ThenBy(entry => entry.SessionId, StringComparer.Ordinal)
                    .ToArray());
            await WriteUtf8Async(
                Path.Combine(stagingRoot, "index.json"),
                PortableConversationJson.Serialize(index),
                cancellationToken);
            generatedFiles.Add(new GeneratedPackageFile(
                $"{PortableRoot}/index.json",
                BackupDataKind.CodexSession,
                RestoreLevel.VerifiedExact));

            if (Directory.Exists(finalRoot))
            {
                throw new IOException("Portable conversation output already exists.");
            }

            Directory.Move(stagingRoot, finalRoot);
            var originalSourcePath = sessionItems[0].OriginalSourcePath;
            var packageItem = new BackupPackageItem(
                "portable-conversations-v1",
                "通用对话 JSON 与 Markdown",
                originalSourcePath,
                PortableRoot,
                BackupDataKind.CodexSession,
                RestoreLevel.VerifiedExact,
                ProjectDiscoverySource.None,
                SourceWasDirectory: true);
            return new BackupContributionResult(generatedFiles, [packageItem], issues);
        }
        catch
        {
            if (Directory.Exists(stagingRoot))
            {
                Directory.Delete(stagingRoot, recursive: true);
            }

            throw;
        }
    }

    private static IReadOnlyList<PortableConversation> SelectUniqueConversations(
        IEnumerable<PortableConversation> conversations,
        ICollection<BackupExportIssue> issues)
    {
        var selected = new List<PortableConversation>();
        foreach (var group in conversations.GroupBy(
                     conversation => conversation.SessionId,
                     StringComparer.OrdinalIgnoreCase))
        {
            var ordered = group
                .OrderByDescending(conversation => conversation.Messages.Count)
                .ThenByDescending(conversation => conversation.UpdatedAtUtc)
                .ToArray();
            selected.Add(ordered[0]);
            if (ordered.Length > 1)
            {
                issues.Add(new BackupExportIssue(
                    "transforming",
                    "CONVERSATION_DUPLICATE_SESSION_ID",
                    "Duplicate copies of one session were found; the most complete copy was used."));
            }
        }

        return selected;
    }

    private static IReadOnlyList<string> FindAssociatedProjects(
        string? workingDirectory,
        IReadOnlyList<BackupPackageItem> copiedItems)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) ||
            !Path.IsPathFullyQualified(workingDirectory))
        {
            return [];
        }

        var matches = copiedItems
            .Where(item =>
                item.Kind is BackupDataKind.Project &&
                IsSameOrDescendant(item.OriginalSourcePath, workingDirectory))
            .OrderByDescending(item => item.OriginalSourcePath.Length)
            .ToArray();
        return matches.Length == 0 ? [] : [matches[0].Id];
    }

    private static bool IsSameOrDescendant(string parentPath, string candidatePath)
    {
        try
        {
            var relative = Path.GetRelativePath(
                Path.GetFullPath(parentPath),
                Path.GetFullPath(candidatePath));
            return relative == "." ||
                   (!Path.IsPathFullyQualified(relative) &&
                    relative != ".." &&
                    !relative.StartsWith(
                        $"..{Path.DirectorySeparatorChar}",
                        StringComparison.Ordinal) &&
                    !relative.StartsWith(
                        $"..{Path.AltDirectorySeparatorChar}",
                        StringComparison.Ordinal));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string CreateSafeFileName(string sessionId)
    {
        var safe = new string(sessionId
            .Where(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            .Take(48)
            .ToArray());
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId)))
            [..12]
            .ToLowerInvariant();
        return safe.Length == 0 ? hash : $"{safe}-{hash}";
    }

    private static string GetSafePackagePath(string packageRoot, string relativePath)
    {
        if (!BackupPathRules.IsSafeRelativePath(relativePath))
        {
            throw new IOException("A generated conversation path is unsafe.");
        }

        var normalizedRoot = Path.GetFullPath(packageRoot);
        var targetPath = Path.GetFullPath(Path.Combine(
            normalizedRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(normalizedRoot, targetPath);
        if (relative == ".." ||
            Path.IsPathFullyQualified(relative) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new IOException("A generated conversation path escaped the package.");
        }

        return targetPath;
    }

    private static Task WriteUtf8Async(
        string path,
        string content,
        CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(path, content, new UTF8Encoding(false), cancellationToken);
}
