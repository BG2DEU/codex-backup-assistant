using CodexBackup.Core.Backup;
using CodexBackup.Core.Discovery;

namespace CodexBackup.Core.Tests;

public sealed class ProjectBackupCandidateFactoryTests
{
    [Fact]
    public void Create_UsesInventoryAndPreservesDiscoverySources()
    {
        var sources = ProjectDiscoverySource.CodexSessionPath | ProjectDiscoverySource.GitRepository;
        var project = new DiscoveredProject(
            Path.GetFullPath(Path.Combine("fixture", "project")),
            "project",
            sources,
            [".git"],
            3,
            FileInventory: new ProjectFileInventory(4, 1234, 0, 500, 0, 0, 0));

        var candidate = ProjectBackupCandidateFactory.Create(project, true);

        Assert.Equal(1234, candidate.EstimatedBytes);
        Assert.Equal(sources, candidate.DiscoverySources);
        Assert.Equal(BackupPolicy.Include, candidate.Policy);
        Assert.StartsWith("project-", candidate.Id);
    }

    [Fact]
    public void Create_RequiresReviewForMarkerlessRecordedPath()
    {
        var project = new DiscoveredProject(
            Path.GetFullPath(Path.Combine("fixture", "unknown")),
            "unknown",
            ProjectDiscoverySource.CodexSessionPath,
            [],
            1);

        var candidate = ProjectBackupCandidateFactory.Create(project, true);

        Assert.Equal(BackupPolicy.UnknownReviewRequired, candidate.Policy);
    }
}
