using CodexBackup.Core.Backup;
using CodexBackup.Core.Codex;

namespace CodexBackup.Core.Tests;

public sealed class CodexSnapshotPlanGuardTests
{
    [Fact]
    public void Apply_BlocksIncludedNativeSnapshotWhileCodexIsRunning()
    {
        var item = CreateItem(requiresStopped: true);
        var plan = new BackupPlanBuilder().Build([
            CodexBackupCandidateFactory.Create(item, isSelected: true),
        ]);
        var usage = new CodexUsageStatus(2, 0, 1, ["CODEX_PROCESS_RUNNING"]);

        var guarded = CodexSnapshotPlanGuard.Apply(plan, [item], usage);

        Assert.False(guarded.CanExport);
        Assert.Contains(guarded.Issues, issue => issue.Code == "CODEX_MUST_BE_STOPPED");
    }

    [Fact]
    public void Apply_AllowsIncludedNativeSnapshotWhenCodexIsStopped()
    {
        var item = CreateItem(requiresStopped: true);
        var plan = new BackupPlanBuilder().Build([
            CodexBackupCandidateFactory.Create(item, isSelected: true),
        ]);
        var usage = new CodexUsageStatus(0, 0, 0, []);

        var guarded = CodexSnapshotPlanGuard.Apply(plan, [item], usage);

        Assert.True(guarded.CanExport);
        Assert.DoesNotContain(guarded.Issues, issue => issue.Code == "CODEX_MUST_BE_STOPPED");
    }

    private static CodexDataItem CreateItem(bool requiresStopped) => new(
        "codex-session",
        "sessions",
        Path.GetFullPath("sessions"),
        true,
        BackupDataKind.CodexSession,
        BackupPolicy.IncludePortableAndNative,
        RestoreLevel.NativeBestEffort,
        1,
        10,
        "fixture",
        requiresStopped,
        true);
}
