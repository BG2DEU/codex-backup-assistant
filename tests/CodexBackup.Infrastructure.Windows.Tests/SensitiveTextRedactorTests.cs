using CodexBackup.Infrastructure.Windows.Codex;

namespace CodexBackup.Infrastructure.Windows.Tests;

public sealed class SensitiveTextRedactorTests
{
    [Theory]
    [InlineData("Authorization: Bearer abcdefghijklmnopqrstuvwxyz012345", "abcdefghijklmnopqrstuvwxyz")]
    [InlineData("\"api_key\": \"abcdefghijklmnopqrstuvwxyz012345\"", "abcdefghijklmnopqrstuvwxyz")]
    [InlineData("GITHUB_TOKEN=ghp_abcdefghijklmnopqrstuvwxyz012345", "ghp_")]
    [InlineData(
        "-----BEGIN PRIVATE KEY-----\nsecret-material\n-----END PRIVATE KEY-----",
        "secret-material")]
    public void Redact_RemovesCommonCredentialPatterns(string input, string forbidden)
    {
        var result = SensitiveTextRedactor.Redact(input);

        Assert.True(result.RedactionCount > 0);
        Assert.DoesNotContain(forbidden, result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED", result.Text);
    }

    [Fact]
    public void Redact_LeavesOrdinaryConversationUnchanged()
    {
        const string input = "请帮我检查项目中的普通配置说明。";

        var result = SensitiveTextRedactor.Redact(input);

        Assert.Equal(input, result.Text);
        Assert.Equal(0, result.RedactionCount);
    }
}
