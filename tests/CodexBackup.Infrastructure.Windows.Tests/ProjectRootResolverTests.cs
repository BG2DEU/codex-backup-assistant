using CodexBackup.Core.Discovery;
using CodexBackup.Infrastructure.Windows.Discovery;

namespace CodexBackup.Infrastructure.Windows.Tests;

public sealed class ProjectRootResolverTests
{
    [Fact]
    public void Resolve_PrefersGitRootOverNestedWorkingDirectory()
    {
        var fixture = CreateFixture();
        try
        {
            var repository = Directory.CreateDirectory(Path.Combine(fixture, "repository")).FullName;
            Directory.CreateDirectory(Path.Combine(repository, ".git"));
            File.WriteAllText(Path.Combine(repository, "package.json"), "{}");
            var workingDirectory = Directory.CreateDirectory(Path.Combine(repository, "src", "feature")).FullName;
            var warnings = new List<DiscoveryWarning>();

            var result = CreateResolver().Resolve(
                workingDirectory,
                ProjectDiscoverySource.CodexSessionPath,
                warnings);

            Assert.NotNull(result);
            Assert.Equal(repository, result.RootPath);
            Assert.True(result.Sources.HasFlag(ProjectDiscoverySource.CodexSessionPath));
            Assert.True(result.Sources.HasFlag(ProjectDiscoverySource.GitRepository));
            Assert.True(result.Sources.HasFlag(ProjectDiscoverySource.ProjectMarker));
            Assert.Empty(warnings);
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }

    [Fact]
    public void Resolve_UsesNearestMarkerWhenNoGitRepositoryExists()
    {
        var fixture = CreateFixture();
        try
        {
            var project = Directory.CreateDirectory(Path.Combine(fixture, "python-project")).FullName;
            File.WriteAllText(Path.Combine(project, "pyproject.toml"), string.Empty);
            var workingDirectory = Directory.CreateDirectory(Path.Combine(project, "src", "package")).FullName;
            var warnings = new List<DiscoveryWarning>();

            var result = CreateResolver().Resolve(
                workingDirectory,
                ProjectDiscoverySource.CodexSessionPath,
                warnings);

            Assert.NotNull(result);
            Assert.Equal(project, result.RootPath);
            Assert.Contains("pyproject.toml", result.Markers);
            Assert.Empty(warnings);
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }

    [Fact]
    public void Resolve_ReturnsWarningForMissingRecordedPath()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var warnings = new List<DiscoveryWarning>();

        var result = CreateResolver().Resolve(
            missing,
            ProjectDiscoverySource.CodexSessionPath,
            warnings);

        Assert.Null(result);
        Assert.Contains(warnings, warning => warning.Code == "PROJECT_PATH_MISSING");
    }

    [Fact]
    public void Resolve_PreservesRecordedDirectoryWhenNoMarkerExists()
    {
        var fixture = CreateFixture();
        try
        {
            var recordedDirectory = Directory.CreateDirectory(Path.Combine(fixture, "unmarked")).FullName;
            var warnings = new List<DiscoveryWarning>();

            var result = CreateResolver().Resolve(
                recordedDirectory,
                ProjectDiscoverySource.CodexSessionPath,
                warnings);

            Assert.NotNull(result);
            Assert.Equal(recordedDirectory, result.RootPath);
            Assert.Equal(ProjectDiscoverySource.CodexSessionPath, result.Sources);
            Assert.True(new DiscoveredProject(
                result.RootPath,
                "unmarked",
                result.Sources,
                result.Markers,
                1).RequiresReview);
            Assert.Contains(warnings, warning => warning.Code == "PROJECT_ROOT_MARKER_NOT_FOUND");
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }

    private static ProjectRootResolver CreateResolver() => new(new ProjectMarkerCatalog());

    private static string CreateFixture()
    {
        var path = Path.Combine(Path.GetTempPath(), "codex-backup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
