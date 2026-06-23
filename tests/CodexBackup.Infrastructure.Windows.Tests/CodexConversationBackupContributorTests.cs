using System.Text.Json;
using System.Security.Cryptography;
using CodexBackup.Core.Backup;
using CodexBackup.Core.Codex;
using CodexBackup.Core.Export;
using CodexBackup.Core.Manifest;
using CodexBackup.Infrastructure.Windows.Codex;
using CodexBackup.Infrastructure.Windows.Export;

namespace CodexBackup.Infrastructure.Windows.Tests;

public sealed class CodexConversationBackupContributorTests
{
    [Fact]
    public async Task Export_GeneratesPortableConversationsAndAssociatesProject()
    {
        var fixture = CreateFixture();
        try
        {
            var project = Directory.CreateDirectory(Path.Combine(fixture, "project")).FullName;
            var sessions = Directory.CreateDirectory(Path.Combine(fixture, ".codex", "sessions")).FullName;
            var destination = Directory.CreateDirectory(Path.Combine(fixture, "destination")).FullName;
            File.WriteAllText(Path.Combine(project, "README.md"), "project");
            WriteSession(
                Path.Combine(sessions, "session.jsonl"),
                "session-123",
                project,
                includeInvalidLine: false);

            var plan = CreatePlan(project, sessions);
            var engine = CreateEngine();

            var result = await engine.ExportAsync(plan, destination, "test-1.0");

            Assert.Equal(BackupExportStatus.Success, result.Status);
            var manifest = BackupManifestJson.Deserialize(
                await File.ReadAllTextAsync(Path.Combine(result.CompletedPackagePath!, "manifest.json")));
            Assert.Contains(manifest.Entries, entry =>
                entry.RelativePath == "portable/conversations/index.json");
            Assert.Contains(manifest.Entries, entry =>
                entry.RelativePath.StartsWith("portable/conversations/json/", StringComparison.Ordinal));
            Assert.Contains(manifest.Entries, entry =>
                entry.RelativePath.StartsWith("portable/conversations/markdown/", StringComparison.Ordinal));
            Assert.Contains(manifest.Entries, entry =>
                entry.RelativePath.EndsWith("/session.jsonl", StringComparison.Ordinal));

            var index = PortableConversationJson.DeserializeIndex(
                await File.ReadAllTextAsync(Path.Combine(
                    result.CompletedPackagePath!,
                    "portable",
                    "conversations",
                    "index.json")));
            var indexEntry = Assert.Single(index.Conversations);
            Assert.Equal("session-123", indexEntry.SessionId);
            Assert.Equal(["project-test"], indexEntry.AssociatedProjectIds);
            Assert.Equal(2, indexEntry.MessageCount);
            var portableJson = await File.ReadAllTextAsync(Path.Combine(
                result.CompletedPackagePath!,
                indexEntry.JsonRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            Assert.DoesNotContain("internal developer content", portableJson);
            Assert.DoesNotContain("tool output content", portableJson);
            Assert.True((await new BackupPackageVerifier().VerifyAsync(
                result.CompletedPackagePath!)).IsValid);
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }

    [Fact]
    public async Task Export_InvalidConversationLineProducesPartialSuccessButKeepsRawSnapshot()
    {
        var fixture = CreateFixture();
        try
        {
            var project = Directory.CreateDirectory(Path.Combine(fixture, "project")).FullName;
            var sessions = Directory.CreateDirectory(Path.Combine(fixture, ".codex", "sessions")).FullName;
            var destination = Directory.CreateDirectory(Path.Combine(fixture, "destination")).FullName;
            File.WriteAllText(Path.Combine(project, "README.md"), "project");
            WriteSession(
                Path.Combine(sessions, "session.jsonl"),
                "session-broken",
                project,
                includeInvalidLine: true);

            var result = await CreateEngine().ExportAsync(
                CreatePlan(project, sessions),
                destination,
                "test-1.0");

            Assert.Equal(BackupExportStatus.PartialSuccess, result.Status);
            Assert.NotNull(result.CompletedPackagePath);
            Assert.Contains(result.Issues, issue =>
                issue.Code == "CONVERSATION_INVALID_JSON_LINE");
            var manifest = BackupManifestJson.Deserialize(
                await File.ReadAllTextAsync(Path.Combine(result.CompletedPackagePath!, "manifest.json")));
            Assert.Contains(manifest.Entries, entry =>
                entry.RelativePath.EndsWith("/session.jsonl", StringComparison.Ordinal));
            Assert.Contains(manifest.Entries, entry =>
                entry.RelativePath.StartsWith("portable/conversations/json/", StringComparison.Ordinal));
            Assert.True((await new BackupPackageVerifier().VerifyAsync(
                result.CompletedPackagePath!)).IsValid);
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }

    [Fact]
    public async Task Verify_DetectsPortableConversationIndexSemanticMismatch()
    {
        var fixture = CreateFixture();
        try
        {
            var project = Directory.CreateDirectory(Path.Combine(fixture, "project")).FullName;
            var sessions = Directory.CreateDirectory(Path.Combine(fixture, ".codex", "sessions")).FullName;
            var destination = Directory.CreateDirectory(Path.Combine(fixture, "destination")).FullName;
            File.WriteAllText(Path.Combine(project, "README.md"), "project");
            WriteSession(
                Path.Combine(sessions, "session.jsonl"),
                "session-semantic",
                project,
                includeInvalidLine: false);
            var result = await CreateEngine().ExportAsync(
                CreatePlan(project, sessions),
                destination,
                "test-1.0");
            var portableIndexPath = Path.Combine(
                result.CompletedPackagePath!,
                "portable",
                "conversations",
                "index.json");
            var index = PortableConversationJson.DeserializeIndex(
                await File.ReadAllTextAsync(portableIndexPath));
            var changedEntry = index.Conversations[0] with
            {
                MessageCount = index.Conversations[0].MessageCount + 1,
            };
            await File.WriteAllTextAsync(
                portableIndexPath,
                PortableConversationJson.Serialize(index with
                {
                    Conversations = [changedEntry],
                }));
            await UpdateManifestHashAsync(
                result.CompletedPackagePath!,
                "portable/conversations/index.json");

            var verification = await new BackupPackageVerifier().VerifyAsync(
                result.CompletedPackagePath!);

            Assert.False(verification.IsValid);
            Assert.Contains(verification.Issues, issue =>
                issue.Code == "CONVERSATION_INDEX_CONTENT_MISMATCH");
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }

    private static BackupExportEngine CreateEngine() => new(
        new FixedVolumeInfoProvider(),
        contributors: [new CodexConversationBackupContributor()]);

    private static BackupPlan CreatePlan(string project, string sessions)
    {
        var candidates = new[]
        {
            new BackupCandidate(
                "project-test",
                "project",
                project,
                BackupDataKind.Project,
                BackupPolicy.Include,
                RestoreLevel.VerifiedExact,
                10,
                true),
            new BackupCandidate(
                "codex-sessions",
                "sessions",
                sessions,
                BackupDataKind.CodexSession,
                BackupPolicy.IncludePortableAndNative,
                RestoreLevel.NativeBestEffort,
                10,
                true),
        };
        return new BackupPlanBuilder().Build(candidates);
    }

    private static void WriteSession(
        string path,
        string sessionId,
        string workingDirectory,
        bool includeInvalidLine)
    {
        var lines = new List<string>
        {
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-06-22T01:00:00Z",
                type = "session_meta",
                payload = new
                {
                    id = sessionId,
                    cwd = workingDirectory,
                    cli_version = "1.2.3",
                },
            }),
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-06-22T01:01:00Z",
                type = "response_item",
                payload = new
                {
                    type = "message",
                    role = "developer",
                    content = new[] { new { type = "input_text", text = "internal developer content" } },
                },
            }),
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-06-22T01:02:00Z",
                type = "response_item",
                payload = new
                {
                    type = "function_call_output",
                    output = "tool output content",
                },
            }),
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-06-22T01:03:00Z",
                type = "event_msg",
                payload = new { type = "user_message", message = "请继续" },
            }),
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-06-22T01:04:00Z",
                type = "event_msg",
                payload = new { type = "agent_message", message = "继续处理。" },
            }),
        };
        if (includeInvalidLine)
        {
            lines.Insert(3, "{invalid json");
        }

        File.WriteAllLines(path, lines);
    }

    private static async Task UpdateManifestHashAsync(
        string packagePath,
        string relativePath)
    {
        var manifestPath = Path.Combine(packagePath, "manifest.json");
        var manifest = BackupManifestJson.Deserialize(
            await File.ReadAllTextAsync(manifestPath));
        var targetPath = Path.Combine(
            packagePath,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        var fileInfo = new FileInfo(targetPath);
        var hash = Convert.ToHexString(
            SHA256.HashData(await File.ReadAllBytesAsync(targetPath))).ToLowerInvariant();
        var entries = manifest.Entries
            .Select(entry => entry.RelativePath == relativePath
                ? entry with
                {
                    Length = fileInfo.Length,
                    Sha256 = hash,
                    LastWriteTimeUtc = new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero),
                }
                : entry)
            .ToArray();
        await File.WriteAllTextAsync(
            manifestPath,
            BackupManifestJson.Serialize(manifest with { Entries = entries }));
    }

    private static string CreateFixture()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "codex-conversation-contributor-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FixedVolumeInfoProvider : IDestinationVolumeInfoProvider
    {
        public DestinationVolumeInfo GetInfo(string destinationPath) => new(long.MaxValue, "NTFS");
    }
}
