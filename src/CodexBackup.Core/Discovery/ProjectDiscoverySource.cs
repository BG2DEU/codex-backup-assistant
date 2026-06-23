namespace CodexBackup.Core.Discovery;

[Flags]
public enum ProjectDiscoverySource
{
    None = 0,
    CodexSessionPath = 1,
    GitRepository = 2,
    ProjectMarker = 4,
    UserAdded = 8,
}
