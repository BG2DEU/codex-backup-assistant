using System.Text.Json;
using CodexBackup.Core.Discovery;
using CodexBackup.Infrastructure.Windows.Discovery;

namespace CodexBackup.Infrastructure.Windows.Tests;

public sealed class ProjectDiscoveryServiceTests
{
    [Fact]
    public void Discover_MergesCodexPathAndSupplementalSourcesAndFindsMissingProject()
    {
        var fixture = CreateFixture();
        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(fixture, "workspace")).FullName;
            var recordedProject = CreateGitProject(workspace, "recorded");
            var recordedWorkingDirectory = Directory.CreateDirectory(Path.Combine(recordedProject, "src")).FullName;
            var supplementalProject = CreateGitProject(workspace, "supplemental");

            var ignored = Directory.CreateDirectory(Path.Combine(workspace, "node_modules", "ignored")).FullName;
            Directory.CreateDirectory(Path.Combine(ignored, ".git"));

            var sessions = Directory.CreateDirectory(Path.Combine(fixture, "sessions")).FullName;
            WriteSession(Path.Combine(sessions, "session.jsonl"), recordedWorkingDirectory);

            var service = CreateService();
            var result = service.Discover(new ProjectDiscoveryRequest(
                [sessions],
                [workspace],
                [],
                5));

            Assert.Equal(2, result.Projects.Count);
            var recorded = result.Projects.Single(project => project.RootPath == recordedProject);
            var supplemental = result.Projects.Single(project => project.RootPath == supplementalProject);
            Assert.True(recorded.Sources.HasFlag(ProjectDiscoverySource.CodexSessionPath));
            Assert.True(recorded.Sources.HasFlag(ProjectDiscoverySource.GitRepository));
            Assert.Equal(1, recorded.SessionReferenceCount);
            Assert.False(supplemental.Sources.HasFlag(ProjectDiscoverySource.CodexSessionPath));
            Assert.True(supplemental.Sources.HasFlag(ProjectDiscoverySource.GitRepository));
            Assert.DoesNotContain(result.Projects, project => project.RootPath == ignored);
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }

    [Fact]
    public void Discover_FindsMarkerOnlyProject()
    {
        var fixture = CreateFixture();
        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(fixture, "workspace")).FullName;
            var project = Directory.CreateDirectory(Path.Combine(workspace, "marker-only")).FullName;
            File.WriteAllText(Path.Combine(project, "go.mod"), "module example");
            var sessions = Directory.CreateDirectory(Path.Combine(fixture, "sessions")).FullName;

            var result = CreateService().Discover(new ProjectDiscoveryRequest(
                [sessions],
                [workspace],
                [],
                3));

            var discovered = Assert.Single(result.Projects);
            Assert.Equal(project, discovered.RootPath);
            Assert.True(discovered.Sources.HasFlag(ProjectDiscoverySource.ProjectMarker));
            Assert.False(discovered.IsGitRepository);
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }

    private static ProjectDiscoveryService CreateService()
    {
        var catalog = new ProjectMarkerCatalog();
        var resolver = new ProjectRootResolver(catalog);
        return new ProjectDiscoveryService(
            new CodexSessionPathReader(),
            resolver,
            new LightweightProjectScanner(catalog, resolver));
    }

    private static string CreateGitProject(string root, string name)
    {
        var project = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        Directory.CreateDirectory(Path.Combine(project, ".git"));
        File.WriteAllText(Path.Combine(project, "package.json"), "{}");
        return project;
    }

    private static void WriteSession(string path, string workingDirectory)
    {
        var metadata = JsonSerializer.Serialize(new
        {
            type = "session_meta",
            payload = new { cwd = workingDirectory },
        });
        File.WriteAllText(path, metadata);
    }

    private static string CreateFixture()
    {
        var path = Path.Combine(Path.GetTempPath(), "codex-backup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
