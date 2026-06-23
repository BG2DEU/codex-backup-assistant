using CodexBackup.Core.Discovery;

namespace CodexBackup.Infrastructure.Windows.Discovery;

public static class WindowsProjectDiscovery
{
    public static ProjectDiscoveryService CreateService()
    {
        var markerCatalog = new ProjectMarkerCatalog();
        var rootResolver = new ProjectRootResolver(markerCatalog);
        return new ProjectDiscoveryService(
            new CodexSessionPathReader(),
            rootResolver,
            new LightweightProjectScanner(markerCatalog, rootResolver),
            new ProjectGitStatusReader(),
            new ProjectFileInventoryScanner());
    }

    public static ProjectDiscoveryRequest CreateDefaultRequest(int maximumSupplementalDepth = 6)
    {
        var codexRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex");
        var sessionRoots = new[]
        {
            Path.Combine(codexRoot, "sessions"),
            Path.Combine(codexRoot, "archived_sessions"),
        };

        var supplementalRoots = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady && drive.DriveType is DriveType.Fixed)
                {
                    supplementalRoots.Add(drive.RootDirectory.FullName);
                }
            }
            catch (IOException)
            {
            }
        }

        return new ProjectDiscoveryRequest(
            sessionRoots,
            supplementalRoots
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            [],
            maximumSupplementalDepth);
    }
}
