using System.Text.Json;
using CodexBackup.Core.Codex;
using CodexBackup.Infrastructure.Windows.Codex;

namespace CodexBackup.Infrastructure.Windows.Tests;

public sealed class CodexConversationParserTests
{
    [Fact]
    public void Parse_ExportsOnlyVisibleMessagesAndRedactsLikelySecrets()
    {
        var fixture = CreateFixture();
        try
        {
            var sessionFile = Path.Combine(fixture, "session.jsonl");
            var workingDirectory = Path.Combine(fixture, "project");
            var lines = new[]
            {
                Serialize("2026-06-22T01:00:00Z", "session_meta", new
                {
                    id = "session-123",
                    cwd = workingDirectory,
                    cli_version = "1.2.3",
                }),
                Serialize("2026-06-22T01:01:00Z", "response_item", new
                {
                    type = "message",
                    role = "developer",
                    content = new[] { new { type = "input_text", text = "internal developer secret" } },
                }),
                Serialize("2026-06-22T01:02:00Z", "event_msg", new
                {
                    type = "user_message",
                    message = "请检查 sk-proj-abcdefghijklmnopqrstuvwxyz123456 和 PASSWORD=supersecretvalue",
                    images = new[] { "data:image/png;base64,not-exported" },
                }),
                Serialize("2026-06-22T01:03:00Z", "response_item", new
                {
                    type = "reasoning",
                    summary = new[] { "internal reasoning" },
                }),
                Serialize("2026-06-22T01:04:00Z", "event_msg", new
                {
                    type = "agent_message",
                    message = "已经完成检查。",
                }),
                Serialize("2026-06-22T01:05:00Z", "event_msg", new
                {
                    type = "thread_rolled_back",
                }),
            };
            File.WriteAllLines(sessionFile, lines);

            var conversation = new CodexConversationParser().Parse(
                sessionFile,
                ["project-1"]);

            Assert.Equal("session-123", conversation.SessionId);
            Assert.Equal(2, conversation.Messages.Count);
            Assert.Equal(PortableConversationRole.User, conversation.Messages[0].Role);
            Assert.Equal(1, conversation.Messages[0].ImageAttachmentCount);
            Assert.DoesNotContain("sk-proj-", conversation.Messages[0].Text);
            Assert.DoesNotContain("supersecretvalue", conversation.Messages[0].Text);
            Assert.Contains("[REDACTED", conversation.Messages[0].Text);
            Assert.DoesNotContain(conversation.Messages, message =>
                message.Text.Contains("developer", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(conversation.Messages, message =>
                message.Text.Contains("reasoning", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(2, conversation.RedactionCount);
            Assert.Equal(1, conversation.RollbackEventCount);
            Assert.Equal(["project-1"], conversation.AssociatedProjectIds);
            Assert.Equal("1.2.3", conversation.SourceCliVersion);
            Assert.Equal(64, conversation.SourceSchemaFingerprint.Length);
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }

    [Fact]
    public void Parse_InvalidLineKeepsValidMessagesAndRecordsWarning()
    {
        var fixture = CreateFixture();
        try
        {
            var sessionFile = Path.Combine(fixture, "session.jsonl");
            File.WriteAllLines(sessionFile,
            [
                Serialize("2026-06-22T01:00:00Z", "session_meta", new { id = "session-1" }),
                "{not valid json",
                Serialize("2026-06-22T01:01:00Z", "event_msg", new
                {
                    type = "user_message",
                    message = "有效消息",
                }),
            ]);

            var conversation = new CodexConversationParser().Parse(sessionFile);

            Assert.Single(conversation.Messages);
            Assert.Contains(conversation.Warnings, warning =>
                warning.Code == "CONVERSATION_INVALID_JSON_LINE" &&
                warning.LineNumber == 2);
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }

    [Fact]
    public void JsonAndMarkdown_ContainReadableRedactedConversation()
    {
        var conversation = new PortableConversation(
            PortableConversation.CurrentFormatVersion,
            "session-1",
            "测试会话",
            DateTimeOffset.Parse("2026-06-22T01:00:00Z"),
            DateTimeOffset.Parse("2026-06-22T01:02:00Z"),
            "C:\\Projects\\demo",
            "1.2.3",
            new string('a', 64),
            ["project-1"],
            [
                new PortableConversationMessage(
                    1,
                    DateTimeOffset.Parse("2026-06-22T01:01:00Z"),
                    PortableConversationRole.User,
                    "你好 <script>alert('x')</script>",
                    0),
                new PortableConversationMessage(
                    2,
                    DateTimeOffset.Parse("2026-06-22T01:02:00Z"),
                    PortableConversationRole.Assistant,
                    "你好，我来处理。",
                    0),
            ],
            0,
            0,
            []);

        var json = PortableConversationJson.Serialize(conversation);
        var restored = PortableConversationJson.DeserializeConversation(json);
        var markdown = PortableConversationMarkdown.Render(conversation);

        Assert.Equal(conversation.SessionId, restored.SessionId);
        Assert.Equal(conversation.Title, restored.Title);
        Assert.Equal(conversation.AssociatedProjectIds, restored.AssociatedProjectIds);
        Assert.Equal(conversation.Messages, restored.Messages);
        Assert.Equal(conversation.Warnings, restored.Warnings);
        Assert.Contains("\"sessionId\": \"session-1\"", json);
        Assert.Contains("# 测试会话", markdown);
        Assert.Contains("## 用户", markdown);
        Assert.Contains("## Codex", markdown);
        Assert.Contains("你好，我来处理。", markdown);
        Assert.DoesNotContain("<script>", markdown);
        Assert.Contains("&lt;script&gt;", markdown);
    }

    private static string Serialize(string timestamp, string type, object payload) =>
        JsonSerializer.Serialize(new { timestamp, type, payload });

    private static string CreateFixture()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "codex-conversation-parser-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
