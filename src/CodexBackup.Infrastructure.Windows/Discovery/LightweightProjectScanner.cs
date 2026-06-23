using CodexBackup.Core.Discovery;

namespace CodexBackup.Infrastructure.Windows.Discovery;

public sealed class LightweightProjectScanner(
    ProjectMarkerCatalog markerCatalog,
    ProjectRootResolver rootResolver)
{
    private static readonly HashSet<string> IgnoredDirectoryNames = new(
        [
            "$Recycle.Bin",
            "System Volume Information",
            "Windows",
            "Program Files",
            "Program Files (x86)",
            "ProgramData",
            "Recovery",
            "PerfLogs",
            "AppData",
            ".codex",
            ".git",
            "node_modules",
            ".venv",
            "venv",
            "dist",
            "build",
            "target",
            "__pycache__",
            ".idea",
            ".vs",
            ".gradle",
            ".next",
            ".nuxt",
        ],
        StringComparer.OrdinalIgnoreCase);

    public SupplementalProjectScanResult Scan(
        IEnumerable<string> roots,
        int maximumDepth,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumDepth, 0);

        var warnings = new List<DiscoveryWarning>();
        var resolutions = new Dictionary<string, ProjectRootResolution>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Path, int Depth)>();

        foreach (var root in roots.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            try
            {
                var normalized = Path.GetFullPath(root);
                if (Directory.Exists(normalized))
                {
                    queue.Enqueue((normalized, 0));
                }
                else
                {
                    warnings.Add(new DiscoveryWarning(
                        "SUPPLEMENTAL_ROOT_MISSING",
                        root,
                        "Supplemental scan root does not exist."));
                }
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                warnings.Add(new DiscoveryWarning(
                    "SUPPLEMENTAL_ROOT_INVALID",
                    root,
                    exception.Message));
            }
        }

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = queue.Dequeue();
            if (!visited.Add(current.Path) || IsReparsePoint(current.Path))
            {
                continue;
            }

            InspectCurrentDirectory(current.Path, resolutions, warnings);
            if (current.Depth >= maximumDepth)
            {
                continue;
            }

            foreach (var child in EnumerateChildDirectories(current.Path, warnings))
            {
                if (!IgnoredDirectoryNames.Contains(Path.GetFileName(child)))
                {
                    queue.Enqueue((child, current.Depth + 1));
                }
            }
        }

        return new SupplementalProjectScanResult(
            resolutions.Values.OrderBy(project => project.RootPath, StringComparer.OrdinalIgnoreCase).ToArray(),
            warnings);
    }

    private void InspectCurrentDirectory(
        string directory,
        Dictionary<string, ProjectRootResolution> resolutions,
        List<DiscoveryWarning> warnings)
    {
        try
        {
            var hasGit = Directory.Exists(Path.Combine(directory, ".git")) ||
                         File.Exists(Path.Combine(directory, ".git"));
            var markers = markerCatalog.FindMarkers(directory);
            if (!hasGit && markers.Count == 0)
            {
                return;
            }

            var source =
                (hasGit ? ProjectDiscoverySource.GitRepository : ProjectDiscoverySource.None) |
                (markers.Count > 0 ? ProjectDiscoverySource.ProjectMarker : ProjectDiscoverySource.None);
            var resolution = rootResolver.Resolve(directory, source, warnings);
            if (resolution is not null)
            {
                MergeResolution(resolutions, resolution);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add(new DiscoveryWarning(
                "PROJECT_DIRECTORY_INSPECTION_FAILED",
                directory,
                exception.Message));
        }
    }

    private static IEnumerable<string> EnumerateChildDirectories(
        string directory,
        List<DiscoveryWarning> warnings)
    {
        try
        {
            return Directory.EnumerateDirectories(directory).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add(new DiscoveryWarning(
                "PROJECT_DIRECTORY_ENUMERATION_FAILED",
                directory,
                exception.Message));
            return [];
        }
    }

    private static bool IsReparsePoint(string directory)
    {
        try
        {
            return (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void MergeResolution(
        Dictionary<string, ProjectRootResolution> resolutions,
        ProjectRootResolution incoming)
    {
        if (!resolutions.TryGetValue(incoming.RootPath, out var existing))
        {
            resolutions.Add(incoming.RootPath, incoming);
            return;
        }

        resolutions[incoming.RootPath] = existing with
        {
            Sources = existing.Sources | incoming.Sources,
            Markers = existing.Markers
                .Concat(incoming.Markers)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
        };
    }
}
