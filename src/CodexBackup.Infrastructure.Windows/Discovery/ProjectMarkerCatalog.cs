namespace CodexBackup.Infrastructure.Windows.Discovery;

public sealed class ProjectMarkerCatalog
{
    private static readonly HashSet<string> ExactMarkers = new(
        [
            "package.json",
            "pyproject.toml",
            "requirements.txt",
            "Cargo.toml",
            "go.mod",
            "pom.xml",
            "build.gradle",
            "build.gradle.kts",
        ],
        StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> FindMarkers(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var markers = new List<string>();
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var name = Path.GetFileName(file);
            if (ExactMarkers.Contains(name) ||
                name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                markers.Add(name);
            }
        }

        return markers
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
