using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexBackup.Core.Codex;

namespace CodexBackup.Infrastructure.Windows.Codex;

public sealed partial class CodexConversationParser
{
    public PortableConversation Parse(
        string sessionFile,
        IReadOnlyList<string>? associatedProjectIds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionFile);

        var messages = new List<PortableConversationMessage>();
        var warnings = new List<PortableConversationWarning>();
        var schemaParts = new SortedSet<string>(StringComparer.Ordinal);
        string? sessionId = null;
        string? workingDirectory = null;
        string? cliVersion = null;
        DateTimeOffset? firstTimestamp = null;
        DateTimeOffset? lastTimestamp = null;
        var rollbackEventCount = 0;
        var redactionCount = 0;
        long lineNumber = 0;
        long sequence = 0;

        using var stream = new FileStream(
            sessionFile,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var eventType = GetString(root, "type") ?? "(missing)";
                AddSchemaPart(schemaParts, "root", eventType, root);
                var timestamp = ParseTimestamp(root, "timestamp");
                if (timestamp is not null)
                {
                    firstTimestamp ??= timestamp;
                    lastTimestamp = timestamp;
                }

                if (!root.TryGetProperty("payload", out var payload) ||
                    payload.ValueKind is not JsonValueKind.Object)
                {
                    continue;
                }

                AddSchemaPart(schemaParts, "payload", eventType, payload);
                if (eventType.Equals("session_meta", StringComparison.Ordinal))
                {
                    sessionId ??= GetString(payload, "id");
                    workingDirectory ??= GetString(payload, "cwd");
                    cliVersion ??= GetString(payload, "cli_version");
                    firstTimestamp ??= ParseTimestamp(payload, "timestamp");
                    continue;
                }

                if (!eventType.Equals("event_msg", StringComparison.Ordinal))
                {
                    continue;
                }

                var payloadType = GetString(payload, "type");
                if (payloadType?.Equals("thread_rolled_back", StringComparison.Ordinal) == true)
                {
                    rollbackEventCount++;
                    continue;
                }

                var role = payloadType switch
                {
                    "user_message" => PortableConversationRole.User,
                    "agent_message" => PortableConversationRole.Assistant,
                    _ => (PortableConversationRole?)null,
                };
                var message = GetString(payload, "message");
                if (role is null || string.IsNullOrWhiteSpace(message))
                {
                    continue;
                }

                var redacted = SensitiveTextRedactor.Redact(message);
                redactionCount += redacted.RedactionCount;
                sequence++;
                messages.Add(new PortableConversationMessage(
                    sequence,
                    timestamp,
                    role.Value,
                    redacted.Text,
                    CountImageAttachments(payload)));
            }
            catch (JsonException exception)
            {
                warnings.Add(new PortableConversationWarning(
                    "CONVERSATION_INVALID_JSON_LINE",
                    exception.Message,
                    lineNumber));
            }
        }

        if (messages.Count == 0)
        {
            warnings.Add(new PortableConversationWarning(
                "CONVERSATION_NO_VISIBLE_MESSAGES",
                "No visible user or assistant messages were found."));
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            sessionId = CreateFallbackSessionId(Path.GetFileName(sessionFile));
            warnings.Add(new PortableConversationWarning(
                "CONVERSATION_SESSION_ID_MISSING",
                "The session ID was missing and a stable fallback ID was generated."));
        }

        var title = CreateTitle(messages);
        return new PortableConversation(
            PortableConversation.CurrentFormatVersion,
            sessionId,
            title,
            firstTimestamp,
            lastTimestamp,
            NormalizeWorkingDirectory(workingDirectory),
            cliVersion,
            HashSchema(schemaParts),
            associatedProjectIds ?? [],
            messages,
            rollbackEventCount,
            redactionCount,
            warnings);
    }

    private static void AddSchemaPart(
        ISet<string> schemaParts,
        string scope,
        string eventType,
        JsonElement element)
    {
        var keys = element.EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal);
        schemaParts.Add($"{scope}:{eventType}:{string.Join(',', keys)}");
    }

    private static int CountImageAttachments(JsonElement payload)
    {
        var count = 0;
        foreach (var propertyName in new[] { "images", "local_images" })
        {
            if (payload.TryGetProperty(propertyName, out var images) &&
                images.ValueKind is JsonValueKind.Array)
            {
                count += images.GetArrayLength();
            }
        }

        return count;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is not JsonValueKind.String)
        {
            return null;
        }

        return property.GetString();
    }

    private static DateTimeOffset? ParseTimestamp(JsonElement element, string propertyName)
    {
        var value = GetString(element, propertyName);
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var timestamp)
            ? timestamp
            : null;
    }

    private static string? NormalizeWorkingDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : path;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static string CreateTitle(IReadOnlyList<PortableConversationMessage> messages)
    {
        var firstUserMessage = messages.FirstOrDefault(message =>
            message.Role is PortableConversationRole.User);
        if (firstUserMessage is null)
        {
            return "无用户标题的 Codex 会话";
        }

        var title = WhitespaceRegex().Replace(firstUserMessage.Text, " ").Trim();
        if (title.Length == 0)
        {
            return "空白开场的 Codex 会话";
        }

        return title.Length <= 80 ? title : $"{title[..77]}...";
    }

    private static string CreateFallbackSessionId(string sourceName)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sourceName));
        return $"fallback-{Convert.ToHexString(hash)[..20].ToLowerInvariant()}";
    }

    private static string HashSchema(IEnumerable<string> schemaParts)
    {
        var value = string.Join('\n', schemaParts);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
    }

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
