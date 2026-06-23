using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexBackup.Core.Manifest;

public static class BackupPackageIndexJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize(BackupPackageIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        return JsonSerializer.Serialize(index, Options);
    }

    public static BackupPackageIndex Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<BackupPackageIndex>(json, Options)
            ?? throw new JsonException("Package index JSON returned null.");
    }
}
