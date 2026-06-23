using CodexBackup.Infrastructure.Windows.Codex;

namespace CodexBackup.Infrastructure.Windows.Tests;

public sealed class CodexUsageInspectorTests
{
    [Fact]
    public void Inspect_BlocksSnapshotWhenCodexProcessIsRunning()
    {
        var fixture = CreateFixture();
        try
        {
            var status = new CodexUsageInspector(() => 2).Inspect(fixture);

            Assert.False(status.CanCreateNativeSnapshot);
            Assert.Equal(2, status.RunningProcessCount);
            Assert.Contains("CODEX_PROCESS_RUNNING", status.WarningCodes);
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }

    [Fact]
    public void Inspect_DetectsLockedDatabaseAndSidecars()
    {
        var fixture = CreateFixture();
        try
        {
            var databasePath = Path.Combine(fixture, "state_1.sqlite");
            File.WriteAllBytes(databasePath, new byte[32]);
            File.WriteAllBytes($"{databasePath}-wal", new byte[8]);
            using var lockStream = new FileStream(
                databasePath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);

            var status = new CodexUsageInspector(() => 0).Inspect(fixture);

            Assert.False(status.CanCreateNativeSnapshot);
            Assert.Equal(1, status.LockedDatabaseCount);
            Assert.Equal(1, status.DatabaseSidecarCount);
            Assert.Contains("CODEX_DATABASE_LOCKED", status.WarningCodes);
            Assert.Contains("CODEX_DATABASE_SIDECARS_PRESENT", status.WarningCodes);
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }

    [Fact]
    public void Inspect_AllowsSnapshotWhenStoppedAndDatabasesAreReadableExclusively()
    {
        var fixture = CreateFixture();
        try
        {
            File.WriteAllBytes(Path.Combine(fixture, "state_1.sqlite"), new byte[32]);

            var status = new CodexUsageInspector(() => 0).Inspect(fixture);

            Assert.True(status.CanCreateNativeSnapshot);
            Assert.Empty(status.WarningCodes);
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }

    private static string CreateFixture()
    {
        var path = Path.Combine(Path.GetTempPath(), "codex-usage-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
