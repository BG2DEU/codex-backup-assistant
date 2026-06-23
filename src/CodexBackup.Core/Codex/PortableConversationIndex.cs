namespace CodexBackup.Core.Codex;

public sealed record PortableConversationIndex(
    string FormatVersion,
    DateTimeOffset GeneratedAtUtc,
    string SourceAdapterVersion,
    IReadOnlyList<PortableConversationIndexEntry> Conversations)
{
    public const string CurrentFormatVersion = "1.0";
}

public sealed record PortableConversationIndexEntry(
    string SessionId,
    string Title,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? UpdatedAtUtc,
    string? WorkingDirectory,
    IReadOnlyList<string> AssociatedProjectIds,
    int MessageCount,
    int UserMessageCount,
    int AssistantMessageCount,
    int RollbackEventCount,
    int RedactionCount,
    int WarningCount,
    string JsonRelativePath,
    string MarkdownRelativePath);
