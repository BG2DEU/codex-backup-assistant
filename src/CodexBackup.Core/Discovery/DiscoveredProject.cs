namespace CodexBackup.Core.Discovery;

public sealed record DiscoveredProject(
    string RootPath,
    string DisplayName,
    ProjectDiscoverySource Sources,
    IReadOnlyList<string> Markers,
    int SessionReferenceCount,
    ProjectGitStatus? GitStatus = null,
    ProjectFileInventory? FileInventory = null)
{
    public bool IsGitRepository => Sources.HasFlag(ProjectDiscoverySource.GitRepository);

    public bool RequiresReview =>
        !Sources.HasFlag(ProjectDiscoverySource.GitRepository) &&
        !Sources.HasFlag(ProjectDiscoverySource.ProjectMarker);
}
