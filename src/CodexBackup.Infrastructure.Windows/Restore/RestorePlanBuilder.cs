using CodexBackup.Core.Backup;
using CodexBackup.Core.Manifest;
using CodexBackup.Core.Restore;

namespace CodexBackup.Infrastructure.Windows.Restore;

public sealed class RestorePlanBuilder(TimeProvider? timeProvider = null)
{
    private static readonly HashSet<string> ProtectedCredentialNames = new(
        [
            "auth.json",
            ".cockpit_codex_auth.json",
            ".sandbox-secrets",
            "installation_id",
            "cap_sid",
            "chrome-native-hosts.json",
            "chrome-native-hosts-v2.json",
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public RestorePlan Build(
        RestoreRequest request,
        BackupPackageIndex packageIndex,
        string currentCodexAdapterVersion)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(packageIndex);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentCodexAdapterVersion);

        ValidateAbsolutePath(request.PackagePath, nameof(request.PackagePath));
        ValidateAbsolutePath(
            request.ProjectDestinationRoot,
            nameof(request.ProjectDestinationRoot));
        ValidateAbsolutePath(
            request.CodexDestinationRoot,
            nameof(request.CodexDestinationRoot));
        ValidateAbsolutePath(
            request.PortableDataDestinationRoot,
            nameof(request.PortableDataDestinationRoot));

        var issues = new List<RestoreIssue>();
        var items = new List<RestorePlanItem>();
        var targetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var adapterCompatible = string.Equals(
            packageIndex.CodexAdapterVersion,
            currentCodexAdapterVersion,
            StringComparison.Ordinal);

        foreach (var packageItem in packageIndex.Items)
        {
            var planItem = MapItem(request, packageItem, adapterCompatible);
            if (planItem is null)
            {
                continue;
            }

            if (planItem.State is RestoreItemState.Ready &&
                !targetPaths.Add(planItem.TargetPath))
            {
                issues.Add(new RestoreIssue(
                    "RESTORE_DUPLICATE_TARGET",
                    "Multiple backup items map to the same restore target.",
                    true,
                    packageItem.Id));
            }

            items.Add(planItem);
        }

        if (items.All(item => item.State is not RestoreItemState.Ready))
        {
            issues.Add(new RestoreIssue(
                "RESTORE_NO_READY_ITEMS",
                "No backup items are eligible for restore.",
                true));
        }

        var rollbackRoot = Path.Combine(
            request.PortableDataDestinationRoot,
            "Codex换机回滚",
            $"{packageIndex.BackupId}_{_timeProvider.GetUtcNow():yyyyMMdd_HHmmss}");
        return new RestorePlan(
            packageIndex.BackupId,
            Path.GetFullPath(request.PackagePath),
            rollbackRoot,
            items,
            issues);
    }

    private static RestorePlanItem? MapItem(
        RestoreRequest request,
        BackupPackageItem packageItem,
        bool adapterCompatible)
    {
        if (!BackupPathRules.IsSafeRelativePath(packageItem.RelativeRoot) ||
            !BackupPathRules.IsSafeRelativePath(packageItem.DisplayName))
        {
            return BlockedItem(
                packageItem,
                "Backup item metadata contains an unsafe path.");
        }

        if (IsProtectedCredentialName(packageItem.DisplayName))
        {
            return BlockedItem(
                packageItem,
                "Login credentials and machine-bound identity files are never restored.");
        }

        if (packageItem.Kind is BackupDataKind.Project)
        {
            if (!request.RestoreProjects)
            {
                return SkippedItem(packageItem, "Project restore was not selected.");
            }

            var target = ResolveConflictTarget(
                Path.Combine(
                    request.ProjectDestinationRoot,
                    CreateSafeName(packageItem.DisplayName)),
                request.ProjectConflictPolicy,
                packageItem.SourceWasDirectory);
            return ReadyItem(
                packageItem,
                target.Path,
                request.ProjectConflictPolicy,
                target.State,
                target.Reason);
        }

        if (packageItem.Id.Equals(
                "portable-conversations-v1",
                StringComparison.OrdinalIgnoreCase))
        {
            if (!request.RestorePortableConversations)
            {
                return SkippedItem(
                    packageItem,
                    "Portable conversation restore was not selected.");
            }

            var target = ResolveConflictTarget(
                Path.Combine(
                    request.PortableDataDestinationRoot,
                    "Codex通用对话"),
                RestoreConflictPolicy.KeepBoth,
                sourceWasDirectory: true);
            return ReadyItem(
                packageItem,
                target.Path,
                RestoreConflictPolicy.KeepBoth,
                target.State,
                target.Reason);
        }

        if (packageItem.Id.Equals(
                "restore-tool-v1",
                StringComparison.OrdinalIgnoreCase))
        {
            return SkippedItem(
                packageItem,
                "The restore tool is already running from the backup package.");
        }

        if (packageItem.Kind is BackupDataKind.EnvironmentInventory)
        {
            return SkippedItem(
                packageItem,
                "Environment inventory is informational and is not restored as files.");
        }

        var isNativeState = packageItem.RestoreLevel is RestoreLevel.NativeBestEffort;
        if (isNativeState &&
            (!request.RestoreNativeCodexState || !adapterCompatible))
        {
            var reason = !request.RestoreNativeCodexState
                ? "Native Codex state restore was not selected."
                : "The backup Codex adapter version is not compatible with this app.";
            return new RestorePlanItem(
                packageItem.Id,
                packageItem.DisplayName,
                packageItem.RelativeRoot,
                string.Empty,
                packageItem.Kind,
                packageItem.RestoreLevel,
                request.CodexConflictPolicy,
                RestoreItemState.SkippedIncompatible,
                reason,
                packageItem.SourceWasDirectory);
        }

        if (!request.RestoreCodexConfiguration && !isNativeState)
        {
            return SkippedItem(
                packageItem,
                "Codex configuration restore was not selected.");
        }

        var itemTargetPath = packageItem.SourceWasDirectory
            ? Path.Combine(
                request.CodexDestinationRoot,
                CreateSafeName(packageItem.DisplayName))
            : Path.Combine(
                request.CodexDestinationRoot,
                Path.GetFileName(packageItem.DisplayName));
        var codexTarget = ResolveConflictTarget(
            itemTargetPath,
            request.CodexConflictPolicy,
            packageItem.SourceWasDirectory);
        return ReadyItem(
            packageItem,
            codexTarget.Path,
            request.CodexConflictPolicy,
            codexTarget.State,
            codexTarget.Reason);
    }

    private static RestorePlanItem ReadyItem(
        BackupPackageItem item,
        string targetPath,
        RestoreConflictPolicy conflictPolicy,
        RestoreItemState state,
        string reason) => new(
        item.Id,
        item.DisplayName,
        item.RelativeRoot,
        targetPath,
        item.Kind,
        item.RestoreLevel,
        conflictPolicy,
        state,
        reason,
        item.SourceWasDirectory);

    private static RestorePlanItem SkippedItem(
        BackupPackageItem item,
        string reason) => new(
        item.Id,
        item.DisplayName,
        item.RelativeRoot,
        string.Empty,
        item.Kind,
        item.RestoreLevel,
        RestoreConflictPolicy.SkipExisting,
        RestoreItemState.SkippedByUser,
        reason,
        item.SourceWasDirectory);

    private static RestorePlanItem BlockedItem(
        BackupPackageItem item,
        string reason) => new(
        item.Id,
        item.DisplayName,
        item.RelativeRoot,
        string.Empty,
        item.Kind,
        item.RestoreLevel,
        RestoreConflictPolicy.SkipExisting,
        RestoreItemState.SkippedIncompatible,
        reason,
        item.SourceWasDirectory);

    private static ConflictTarget ResolveConflictTarget(
        string requestedPath,
        RestoreConflictPolicy policy,
        bool sourceWasDirectory)
    {
        var fullPath = Path.GetFullPath(requestedPath);
        var exists = sourceWasDirectory
            ? Directory.Exists(fullPath)
            : File.Exists(fullPath);
        if (!exists)
        {
            return new ConflictTarget(
                fullPath,
                RestoreItemState.Ready,
                "Target does not exist.");
        }

        return policy switch
        {
            RestoreConflictPolicy.KeepBoth => new ConflictTarget(
                CreateUniqueSiblingPath(fullPath, sourceWasDirectory),
                RestoreItemState.Ready,
                "An existing item was found; the backup will be restored beside it."),
            RestoreConflictPolicy.SkipExisting => new ConflictTarget(
                fullPath,
                RestoreItemState.SkippedExisting,
                "An existing item was found and will be preserved."),
            RestoreConflictPolicy.MergePreserveExisting => new ConflictTarget(
                fullPath,
                RestoreItemState.Ready,
                "Missing files will be restored without replacing existing files."),
            RestoreConflictPolicy.ReplaceWithRollback => new ConflictTarget(
                fullPath,
                RestoreItemState.Ready,
                "The existing item will be copied to the rollback directory before replacement."),
            _ => throw new ArgumentOutOfRangeException(nameof(policy)),
        };
    }

    private static string CreateUniqueSiblingPath(
        string path,
        bool isDirectory)
    {
        var parent = Path.GetDirectoryName(path)
            ?? throw new IOException("Unable to determine restore target parent.");
        var name = isDirectory
            ? Path.GetFileName(path)
            : Path.GetFileNameWithoutExtension(path);
        var extension = isDirectory ? string.Empty : Path.GetExtension(path);
        for (var suffix = 1; suffix <= 999; suffix++)
        {
            var candidate = Path.Combine(
                parent,
                $"{name}-从备份恢复-{suffix}{extension}");
            if (!Directory.Exists(candidate) && !File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("Unable to allocate a unique restore target name.");
    }

    private static string CreateSafeName(string displayName)
    {
        var name = Path.GetFileName(displayName.Trim());
        if (!BackupPathRules.IsSafeRelativePath(name))
        {
            throw new IOException("A backup item display name is unsafe.");
        }

        return name;
    }

    private static bool IsProtectedCredentialName(string name) =>
        ProtectedCredentialNames.Contains(name) ||
        name.StartsWith("auth.json", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith(
            ".cockpit_codex_auth.json",
            StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith(
            "chrome-native-hosts",
            StringComparison.OrdinalIgnoreCase);

    private static void ValidateAbsolutePath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException(
                "Restore paths must be absolute.",
                parameterName);
        }
    }

    private sealed record ConflictTarget(
        string Path,
        RestoreItemState State,
        string Reason);
}
