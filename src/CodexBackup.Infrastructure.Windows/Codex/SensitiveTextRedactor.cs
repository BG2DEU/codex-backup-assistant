using System.Text.RegularExpressions;

namespace CodexBackup.Infrastructure.Windows.Codex;

public static partial class SensitiveTextRedactor
{
    public static RedactionResult Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var redactionCount = 0;
        var redacted = PrivateKeyRegex().Replace(text, _ =>
        {
            redactionCount++;
            return "[REDACTED_PRIVATE_KEY]";
        });
        redacted = PrefixedTokenRegex().Replace(redacted, match =>
        {
            redactionCount++;
            return match.Value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? "Bearer [REDACTED_TOKEN]"
                : "[REDACTED_TOKEN]";
        });
        redacted = NamedSecretRegex().Replace(redacted, match =>
        {
            redactionCount++;
            return $"{match.Groups["name"].Value}{match.Groups["separator"].Value}[REDACTED_SECRET]";
        });

        return new RedactionResult(NormalizeControlCharacters(redacted), redactionCount);
    }

    private static string NormalizeControlCharacters(string text) => new(
        text.Where(character =>
                character is '\r' or '\n' or '\t' || !char.IsControl(character))
            .ToArray());

    [GeneratedRegex(
        "-----BEGIN(?: [A-Z0-9]+)* PRIVATE KEY-----[\\s\\S]*?-----END(?: [A-Z0-9]+)* PRIVATE KEY-----",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyRegex();

    [GeneratedRegex(
        "\\b(?:Bearer\\s+[A-Za-z0-9._~+/=-]{20,}|sk-(?:proj-)?[A-Za-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[A-Z0-9]{16})\\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrefixedTokenRegex();

    [GeneratedRegex(
        "(?<name>[\"']?[A-Z0-9_]*(?:TOKEN|PASSWORD|PASSWD|API_KEY|SECRET|PRIVATE_KEY)[A-Z0-9_]*[\"']?)(?<separator>\\s*[:=]\\s*)[\"']?[^\\s,\"']{8,}[\"']?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NamedSecretRegex();
}

public sealed record RedactionResult(string Text, int RedactionCount);
