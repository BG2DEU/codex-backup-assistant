using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexBackup.Core.Codex;

public static class PortableConversationJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize(PortableConversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        return JsonSerializer.Serialize(conversation, Options);
    }

    public static PortableConversation DeserializeConversation(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<PortableConversation>(json, Options)
            ?? throw new JsonException("Portable conversation JSON returned null.");
    }

    public static string Serialize(PortableConversationIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        return JsonSerializer.Serialize(index, Options);
    }

    public static PortableConversationIndex DeserializeIndex(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<PortableConversationIndex>(json, Options)
            ?? throw new JsonException("Portable conversation index JSON returned null.");
    }
}
