using System.Text;
using CodexBackup.Core.Codex;

namespace CodexBackup.Infrastructure.Windows.Codex;

public static class PortableConversationMarkdown
{
    public static string Render(PortableConversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        var builder = new StringBuilder();
        builder.Append("# ").AppendLine(EscapeInline(conversation.Title));
        builder.AppendLine();
        builder.AppendLine("> 这是从 Codex 原始会话快照生成的通用阅读副本。原始 JSONL 仍保存在同一备份包中。");
        builder.AppendLine();
        AppendMetadata(builder, "会话 ID", conversation.SessionId);
        AppendMetadata(builder, "开始时间", FormatTimestamp(conversation.StartedAtUtc));
        AppendMetadata(builder, "最后时间", FormatTimestamp(conversation.UpdatedAtUtc));
        AppendMetadata(builder, "原工作目录", conversation.WorkingDirectory ?? "未知");
        AppendMetadata(builder, "源 Codex CLI 版本", conversation.SourceCliVersion ?? "未知");
        AppendMetadata(builder, "消息数量", conversation.Messages.Count.ToString());
        AppendMetadata(builder, "脱敏数量", conversation.RedactionCount.ToString());
        AppendMetadata(builder, "回滚事件", conversation.RollbackEventCount.ToString());
        builder.AppendLine();

        if (conversation.RollbackEventCount > 0)
        {
            builder.AppendLine("> 注意：该会话包含回滚事件，通用副本按原始事件出现顺序保留可见消息。");
            builder.AppendLine();
        }

        foreach (var message in conversation.Messages)
        {
            var role = message.Role is PortableConversationRole.User ? "用户" : "Codex";
            builder.Append("## ")
                .Append(role)
                .Append(" · ")
                .AppendLine(FormatTimestamp(message.TimestampUtc));
            builder.AppendLine();
            builder.AppendLine(EscapeMessageBody(message.Text));
            if (message.ImageAttachmentCount > 0)
            {
                builder.AppendLine();
                builder.Append("> 图片附件：")
                    .Append(message.ImageAttachmentCount)
                    .AppendLine(" 个（附件内容保留在原始会话快照中）");
            }

            builder.AppendLine();
        }

        if (conversation.Warnings.Count > 0)
        {
            builder.AppendLine("## 转换提示");
            builder.AppendLine();
            foreach (var warning in conversation.Warnings)
            {
                builder.Append("- ")
                    .Append(warning.Code)
                    .Append("：")
                    .AppendLine(warning.Message);
            }
        }

        return builder.ToString();
    }

    private static void AppendMetadata(StringBuilder builder, string name, string value)
    {
        builder.Append("- **")
            .Append(name)
            .Append("**：")
            .AppendLine(EscapeInline(value));
    }

    private static string EscapeInline(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("*", "\\*", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal)
        .Replace("|", "\\|", StringComparison.Ordinal)
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal);

    private static string EscapeMessageBody(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);

    private static string FormatTimestamp(DateTimeOffset? timestamp) =>
        timestamp?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'") ?? "未知时间";
}
