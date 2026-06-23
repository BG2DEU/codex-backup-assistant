using CodexBackup.Core.Discovery;

namespace CodexBackup.Infrastructure.Windows.Discovery;

public sealed class ProjectRootResolver(ProjectMarkerCatalog markerCatalog)
{
    public ProjectRootResolution? Resolve(
        string path,
        ProjectDiscoverySource initialSource,
        ICollection<DiscoveryWarning> warnings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(warnings);

        var directory = NormalizeExistingDirectory(path);
        if (directory is null)
        {
            warnings.Add(new DiscoveryWarning(
                "PROJECT_PATH_MISSING",
                path,
                "Recorded project path does not exist or is not accessible."));
            return null;
        }

        string? nearestMarkerDirectory = null;
        IReadOnlyList<string> nearestMarkers = [];
        var collectedMarkers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            IReadOnlyList<string> markers;
            try
            {
                markers = markerCatalog.FindMarkers(current.FullName);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                warnings.Add(new DiscoveryWarning(
                    "PROJECT_MARKER_READ_FAILED",
                    current.FullName,
                    exception.Message));
                markers = [];
            }

            if (markers.Count > 0)
            {
                nearestMarkerDirectory ??= current.FullName;
                if (nearestMarkers.Count == 0)
                {
                    nearestMarkers = markers;
                }

                foreach (var marker in markers)
                {
                    collectedMarkers.Add(marker);
                }
            }

            if (HasGitMetadata(current.FullName))
            {
                return new ProjectRootResolution(
                    current.FullName,
                    initialSource |
                    ProjectDiscoverySource.GitRepository |
                    (collectedMarkers.Count > 0 ? ProjectDiscoverySource.ProjectMarker : ProjectDiscoverySource.None),
                    collectedMarkers.Order(StringComparer.OrdinalIgnoreCase).ToArray());
            }
        }

        if (nearestMarkerDirectory is not null)
        {
            return new ProjectRootResolution(
                nearestMarkerDirectory,
                initialSource | ProjectDiscoverySource.ProjectMarker,
                nearestMarkers);
        }

        if (initialSource.HasFlag(ProjectDiscoverySource.CodexSessionPath) ||
            initialSource.HasFlag(ProjectDiscoverySource.UserAdded))
        {
            warnings.Add(new DiscoveryWarning(
                "PROJECT_ROOT_MARKER_NOT_FOUND",
                directory,
                "No Git metadata or known project marker was found; the recorded directory requires review."));
            return new ProjectRootResolution(directory, initialSource, []);
        }

        return null;
    }

    private static string? NormalizeExistingDirectory(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (Directory.Exists(fullPath))
            {
                return fullPath;
            }

            if (File.Exists(fullPath))
            {
                return Path.GetDirectoryName(fullPath);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        return null;
    }

    private static bool HasGitMetadata(string directory) =>
        Directory.Exists(Path.Combine(directory, ".git")) ||
        File.Exists(Path.Combine(directory, ".git"));
}
