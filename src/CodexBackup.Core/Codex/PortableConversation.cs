namespace CodexBackup.Core.Codex;

public sealed record PortableConversation(
    string FormatVersion,
    string SessionId,
    string Title,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? UpdatedAtUtc,
    string? WorkingDirectory,
    string? SourceCliVersion,
    string SourceSchemaFingerprint,
    IReadOnlyList<string> AssociatedProjectIds,
    IReadOnlyList<PortableConversationMessage> Messages,
    int RollbackEventCount,
    int RedactionCount,
    IReadOnlyList<PortableConversationWarning> Warnings)
{
    public const string CurrentFormatVersion = "1.0";
}

public sealed record PortableConversationMessage(
    long Sequence,
    DateTimeOffset? TimestampUtc,
    PortableConversationRole Role,
    string Text,
    int ImageAttachmentCount);

public enum PortableConversationRole
{
    User,
    Assistant,
}

public sealed record PortableConversationWarning(
    string Code,
    string Message,
    long? LineNumber = null);
