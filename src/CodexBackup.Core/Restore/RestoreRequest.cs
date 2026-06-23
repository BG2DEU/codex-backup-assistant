namespace CodexBackup.Core.Restore;

public sealed record RestoreRequest(
    string PackagePath,
    string ProjectDestinationRoot,
    string CodexDestinationRoot,
    string PortableDataDestinationRoot,
    bool RestoreProjects = true,
    bool RestorePortableConversations = true,
    bool RestoreCodexConfiguration = true,
    bool RestoreNativeCodexState = false,
    RestoreConflictPolicy ProjectConflictPolicy = RestoreConflictPolicy.KeepBoth,
    RestoreConflictPolicy CodexConflictPolicy = RestoreConflictPolicy.MergePreserveExisting);
