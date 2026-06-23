using CodexBackup.Core.Backup;
using CodexBackup.Core.Discovery;

namespace CodexBackup.Core.Tests;

public sealed class BackupPlanBuilderTests
{
    [Fact]
    public void Build_AlwaysExcludesCredentialsAndVolatileData()
    {
        var candidates = new[]
        {
            Candidate("project", BackupPolicy.Include, true, 100),
            Candidate("auth", BackupPolicy.ExcludeCredential, true, 200),
            Candidate("cache", BackupPolicy.ExcludeVolatile, true, 300),
        };

        var plan = new BackupPlanBuilder().Build(candidates);

        Assert.True(plan.CanExport);
        Assert.Equal(100, plan.IncludedBytes);
        Assert.Equal(BackupPlanItemState.ExcludedCredential, plan.Items.Single(item => item.Id == "auth").State);
        Assert.Equal(BackupPlanItemState.ExcludedVolatile, plan.Items.Single(item => item.Id == "cache").State);
    }

    [Fact]
    public void Build_BlocksExportUntilUnknownItemsAreReviewed()
    {
        var pending = Candidate("unknown", BackupPolicy.UnknownReviewRequired, true, 10);

        var pendingPlan = new BackupPlanBuilder().Build([pending]);
        var approvedPlan = new BackupPlanBuilder().Build([pending with { IsReviewApproved = true }]);

        Assert.False(pendingPlan.CanExport);
        Assert.Equal(1, pendingPlan.ReviewRequiredCount);
        Assert.True(approvedPlan.CanExport);
    }

    [Fact]
    public void Build_PreservesAllProjectDiscoverySources()
    {
        var sources = ProjectDiscoverySource.CodexSessionPath |
                      ProjectDiscoverySource.GitRepository |
                      ProjectDiscoverySource.ProjectMarker;

        var plan = new BackupPlanBuilder().Build([
            Candidate("project", BackupPolicy.Include, true, 10) with { DiscoverySources = sources },
        ]);

        Assert.Equal(sources, Assert.Single(plan.Items).DiscoverySources);
    }

    [Fact]
    public void Build_ExcludedUnknownItemDoesNotBlockIncludedItems()
    {
        var plan = new BackupPlanBuilder().Build([
            Candidate("included", BackupPolicy.Include, true, 10),
            Candidate("unknown", BackupPolicy.UnknownReviewRequired, false, 20),
        ]);

        Assert.True(plan.CanExport);
        Assert.Equal(0, plan.ReviewRequiredCount);
        Assert.Equal(
            BackupPlanItemState.ExcludedByUser,
            plan.Items.Single(item => item.Id == "unknown").State);
    }

    [Fact]
    public void Build_BlocksOverlappingIncludedPaths()
    {
        var parent = Candidate("parent", BackupPolicy.Include, true, 10);
        var child = Candidate("child", BackupPolicy.Include, true, 20) with
        {
            SourcePath = Path.Combine(parent.SourcePath, "nested"),
        };

        var plan = new BackupPlanBuilder().Build([parent, child]);

        Assert.False(plan.CanExport);
        var issue = Assert.Single(plan.Issues, issue => issue.Code == "PLAN_OVERLAPPING_INCLUDED_PATHS");
        Assert.Contains("parent", issue.Message);
        Assert.Contains("child", issue.Message);
    }

    private static BackupCandidate Candidate(
        string id,
        BackupPolicy policy,
        bool selected,
        long bytes) => new(
            id,
            id,
            Path.Combine(Path.GetPathRoot(Environment.CurrentDirectory)!, "fixture", id),
            BackupDataKind.Project,
            policy,
            RestoreLevel.VerifiedExact,
            bytes,
            selected);
}
