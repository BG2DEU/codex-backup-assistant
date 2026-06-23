using System.Text.Json;
using CodexBackup.Infrastructure.Windows.Discovery;

namespace CodexBackup.Infrastructure.Windows.Tests;

public sealed class CodexSessionPathReaderTests
{
    [Fact]
    public void Read_ExtractsOnlyMetadataWorkingDirectory()
    {
        var fixture = CreateFixture();
        try
        {
            var project = Directory.CreateDirectory(Path.Combine(fixture, "project")).FullName;
            var sessions = Directory.CreateDirectory(Path.Combine(fixture, "sessions", "2026", "06", "17")).FullName;
            var sessionFile = Path.Combine(sessions, "session.jsonl");
            var metadata = JsonSerializer.Serialize(new
            {
                type = "session_meta",
                payload = new { cwd = project },
            });
            File.WriteAllLines(sessionFile, [metadata, "DIALOGUE_SENTINEL_MUST_NOT_BE_PARSED"]);

            var result = new CodexSessionPathReader().Read([Path.Combine(fixture, "sessions")]);

            var record = Assert.Single(result.Records);
            Assert.Equal(project, record.WorkingDirectory);
            Assert.True(record.DirectoryExists);
            Assert.Empty(result.Warnings);
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }

    [Fact]
    public void Read_ReportsMissingRootWithoutFailingTheScan()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        var result = new CodexSessionPathReader().Read([missing]);

        Assert.Empty(result.Records);
        Assert.Contains(result.Warnings, warning => warning.Code == "CODEX_SESSION_ROOT_MISSING");
    }

    [Fact]
    public void Read_CanReadSessionWhileAnotherProcessKeepsItOpen()
    {
        var fixture = CreateFixture();
        try
        {
            var project = Directory.CreateDirectory(Path.Combine(fixture, "project")).FullName;
            var sessions = Directory.CreateDirectory(Path.Combine(fixture, "sessions")).FullName;
            var sessionFile = Path.Combine(sessions, "active.jsonl");
            var metadata = JsonSerializer.Serialize(new
            {
                type = "session_meta",
                payload = new { cwd = project },
            });
            File.WriteAllText(sessionFile, metadata);

            using var activeWriter = new FileStream(
                sessionFile,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            var result = new CodexSessionPathReader().Read([sessions]);

            Assert.Single(result.Records);
            Assert.Empty(result.Warnings);
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }

    private static string CreateFixture()
    {
        var path = Path.Combine(Path.GetTempPath(), "codex-backup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
