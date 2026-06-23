using CodexBackup.Core.Discovery;

namespace CodexBackup.Infrastructure.Windows.Discovery;

public sealed class ProjectDiscoveryService(
    CodexSessionPathReader sessionPathReader,
    ProjectRootResolver rootResolver,
    LightweightProjectScanner supplementalScanner,
    ProjectGitStatusReader? gitStatusReader = null,
    ProjectFileInventoryScanner? fileInventoryScanner = null)
{
    public ProjectDiscoveryResult Discover(
        ProjectDiscoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var warnings = new List<DiscoveryWarning>();
        var projects = new Dictionary<string, ProjectAccumulator>(StringComparer.OrdinalIgnoreCase);
        var sessionResult = sessionPathReader.Read(request.SessionRoots);
        warnings.AddRange(sessionResult.Warnings);

        foreach (var group in sessionResult.Records.GroupBy(
                     record => record.WorkingDirectory,
                     StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolution = rootResolver.Resolve(
                group.Key,
                ProjectDiscoverySource.CodexSessionPath,
                warnings);
            if (resolution is not null)
            {
                Merge(projects, resolution, group.Count());
            }
        }

        var supplementalResult = supplementalScanner.Scan(
            request.SupplementalRoots,
            request.MaximumSupplementalDepth,
            cancellationToken);
        warnings.AddRange(supplementalResult.Warnings);
        foreach (var resolution in supplementalResult.Projects)
        {
            Merge(projects, resolution, 0);
        }

        foreach (var manualPath in request.ManuallyAddedPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolution = rootResolver.Resolve(
                manualPath,
                ProjectDiscoverySource.UserAdded,
                warnings);
            if (resolution is not null)
            {
                Merge(projects, resolution, 0);
            }
        }

        var discoveredProjects = projects.Values
            .Select(project => project.ToDiscoveredProject())
            .OrderBy(project => project.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(project => project.RootPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (gitStatusReader is not null || fileInventoryScanner is not null)
        {
            for (var index = 0; index < discoveredProjects.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var project = discoveredProjects[index];

                if (gitStatusReader is not null && project.IsGitRepository)
                {
                    var gitStatus = gitStatusReader.Read(project.RootPath, cancellationToken);
                    project = project with { GitStatus = gitStatus };
                    if (!gitStatus.IsAvailable)
                    {
                        warnings.Add(new DiscoveryWarning(
                            gitStatus.ErrorCode ?? "GIT_STATUS_UNAVAILABLE",
                            project.RootPath,
                            gitStatus.ErrorMessage ?? "Git status is unavailable."));
                    }
                }

                if (fileInventoryScanner is not null)
                {
                    var inventory = fileInventoryScanner.Scan(project.RootPath, cancellationToken);
                    project = project with { FileInventory = inventory };
                    if (!inventory.IsComplete)
                    {
                        warnings.Add(new DiscoveryWarning(
                            "PROJECT_INVENTORY_PARTIAL",
                            project.RootPath,
                            $"{inventory.UnreadableItemCount} project entries could not be read."));
                    }
                }

                discoveredProjects[index] = project;
            }
        }

        return new ProjectDiscoveryResult(
            discoveredProjects,
            warnings,
            sessionResult.ScannedFileCount,
            sessionResult.Records.Count,
            sessionResult.UniqueWorkingDirectories.Count);
    }

    private static void Merge(
        Dictionary<string, ProjectAccumulator> projects,
        ProjectRootResolution resolution,
        int sessionReferences)
    {
        if (!projects.TryGetValue(resolution.RootPath, out var project))
        {
            project = new ProjectAccumulator(resolution.RootPath);
            projects.Add(resolution.RootPath, project);
        }

        project.Sources |= resolution.Sources;
        project.SessionReferenceCount += sessionReferences;
        foreach (var marker in resolution.Markers)
        {
            project.Markers.Add(marker);
        }
    }

    private sealed class ProjectAccumulator(string rootPath)
    {
        public string RootPath { get; } = rootPath;

        public ProjectDiscoverySource Sources { get; set; }

        public int SessionReferenceCount { get; set; }

        public HashSet<string> Markers { get; } = new(StringComparer.OrdinalIgnoreCase);

        public DiscoveredProject ToDiscoveredProject() => new(
            RootPath,
            Path.GetFileName(RootPath.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name
                ? name
                : RootPath,
            Sources,
            Markers.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            SessionReferenceCount);
    }
}
