using System.Security.Cryptography;
using System.Text;
using CodexBackup.Core.Backup;
using CodexBackup.Core.Codex;
using CodexBackup.Core.Discovery;

namespace CodexBackup.Infrastructure.Windows.Codex;

public sealed class CodexStorageAdapter
{
    public const string CurrentAdapterVersion = "windows-local-1.0";

    private static readonly HashSet<string> CredentialNames = new(
        [
            ".sandbox-secrets",
            "installation_id",
            "cap_sid",
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> VolatileDirectoryNames = new(
        [
            ".tmp",
            "tmp",
            "cache",
            ".sandbox",
            ".sandbox-bin",
            "browser",
            "computer-use",
            "node_repl",
            "process_manager",
            "ambient-suggestions",
        ],
        StringComparer.OrdinalIgnoreCase);

    public CodexInventoryResult Inspect(
        string codexRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codexRoot);

        var normalizedRoot = Path.GetFullPath(codexRoot);
        var warnings = new List<DiscoveryWarning>();
        var items = new List<CodexDataItem>();
        if (!Directory.Exists(normalizedRoot))
        {
            warnings.Add(new DiscoveryWarning(
                "CODEX_ROOT_MISSING",
                normalizedRoot,
                "Codex data root was not found."));
            return new CodexInventoryResult(normalizedRoot, CurrentAdapterVersion, items, warnings);
        }

        string[] topLevelEntries;
        try
        {
            topLevelEntries = Directory.GetFileSystemEntries(normalizedRoot);
            Array.Sort(topLevelEntries, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add(new DiscoveryWarning(
                "CODEX_ROOT_ENUMERATION_FAILED",
                normalizedRoot,
                exception.Message));
            return new CodexInventoryResult(normalizedRoot, CurrentAdapterVersion, items, warnings);
        }

        foreach (var entry in topLevelEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    warnings.Add(new DiscoveryWarning(
                        "CODEX_REPARSE_POINT_SKIPPED",
                        entry,
                        "Codex top-level reparse point requires explicit review."));
                    continue;
                }

                var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                var statistics = isDirectory
                    ? InspectDirectory(entry, warnings, cancellationToken)
                    : new ItemStatistics(1, new FileInfo(entry).Length);
                var name = Path.GetFileName(entry);
                var classification = Classify(name, isDirectory);
                items.Add(new CodexDataItem(
                    CreateStableId(name),
                    name,
                    entry,
                    isDirectory,
                    classification.Kind,
                    classification.Policy,
                    classification.RestoreLevel,
                    statistics.FileCount,
                    statistics.Bytes,
                    classification.Reason,
                    classification.RequiresCodexStopped,
                    classification.ContainsPotentialSecrets));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                warnings.Add(new DiscoveryWarning(
                    "CODEX_ITEM_INSPECTION_FAILED",
                    entry,
                    exception.Message));
            }
        }

        return new CodexInventoryResult(normalizedRoot, CurrentAdapterVersion, items, warnings);
    }

    public static string GetDefaultRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".codex");

    private static ItemStatistics InspectDirectory(
        string root,
        ICollection<DiscoveryWarning> warnings,
        CancellationToken cancellationToken)
    {
        long fileCount = 0;
        long bytes = 0;
        var pending = new Queue<string>();
        pending.Enqueue(root);

        while (pending.TryDequeue(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                warnings.Add(new DiscoveryWarning(
                    "CODEX_DIRECTORY_ENUMERATION_FAILED",
                    directory,
                    exception.Message));
                continue;
            }

            foreach (var entry in entries)
            {
                try
                {
                    var attributes = File.GetAttributes(entry);
                    if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }

                    if (attributes.HasFlag(FileAttributes.Directory))
                    {
                        pending.Enqueue(entry);
                    }
                    else
                    {
                        fileCount++;
                        bytes = checked(bytes + new FileInfo(entry).Length);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    warnings.Add(new DiscoveryWarning(
                        "CODEX_FILE_INSPECTION_FAILED",
                        entry,
                        exception.Message));
                }
            }
        }

        return new ItemStatistics(fileCount, bytes);
    }

    private static Classification Classify(string name, bool isDirectory)
    {
        if (IsCredential(name))
        {
            return new Classification(
                BackupDataKind.CodexState,
                BackupPolicy.ExcludeCredential,
                RestoreLevel.NotMigrated,
                "登录、机器绑定或 sandbox 凭据，强制排除",
                false,
                true);
        }

        if (isDirectory && VolatileDirectoryNames.Contains(name))
        {
            return new Classification(
                BackupDataKind.CodexState,
                BackupPolicy.ExcludeVolatile,
                RestoreLevel.NotMigrated,
                "缓存或运行时临时数据，可在新电脑重建",
                false,
                false);
        }

        if (name.Equals("sessions", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("archived_sessions", StringComparison.OrdinalIgnoreCase))
        {
            return new Classification(
                BackupDataKind.CodexSession,
                BackupPolicy.IncludePortableAndNative,
                RestoreLevel.NativeBestEffort,
                "保留原始会话快照，并在导出时生成通用 JSON 与 Markdown",
                true,
                true);
        }

        if (name.Equals("rules", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase))
        {
            return new Classification(
                BackupDataKind.Configuration,
                BackupPolicy.Include,
                RestoreLevel.VerifiedExact,
                "持久规则与个人指导",
                true,
                false);
        }

        if (name.StartsWith("config.toml", StringComparison.OrdinalIgnoreCase))
        {
            return new Classification(
                BackupDataKind.Configuration,
                BackupPolicy.Include,
                RestoreLevel.VerifiedExact,
                "Codex 配置；可能包含私有服务参数，导出前提示",
                true,
                true);
        }

        if (name.Equals("skills", StringComparison.OrdinalIgnoreCase))
        {
            return new Classification(
                BackupDataKind.Skill,
                BackupPolicy.Include,
                RestoreLevel.VerifiedExact,
                "用户技能与本地技能文件",
                true,
                false);
        }

        if (name.Equals("plugins", StringComparison.OrdinalIgnoreCase))
        {
            return new Classification(
                BackupDataKind.Plugin,
                BackupPolicy.Include,
                RestoreLevel.NativeBestEffort,
                "插件源文件与安装状态快照，恢复时需要兼容性检查",
                true,
                true);
        }

        if (name.Equals("generated_images", StringComparison.OrdinalIgnoreCase))
        {
            return new Classification(
                BackupDataKind.GeneratedAsset,
                BackupPolicy.Include,
                RestoreLevel.VerifiedExact,
                "Codex 生成资源",
                true,
                false);
        }

        if (name.Equals("models_cache.json", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("vendor_imports", StringComparison.OrdinalIgnoreCase))
        {
            return new Classification(
                BackupDataKind.EnvironmentInventory,
                BackupPolicy.InventoryOnly,
                RestoreLevel.Rebuildable,
                "可重新发现或下载，仅记录清单",
                false,
                false);
        }

        if (IsKnownNativeState(name))
        {
            return new Classification(
                BackupDataKind.CodexState,
                BackupPolicy.Include,
                RestoreLevel.NativeBestEffort,
                "Codex 本地状态快照，内部格式按适配器版本处理",
                true,
                true);
        }

        return new Classification(
            BackupDataKind.CodexState,
            BackupPolicy.UnknownReviewRequired,
            RestoreLevel.NativeBestEffort,
            "当前适配器不认识此顶层项，默认不选",
            true,
            true);
    }

    private static bool IsCredential(string name) =>
        CredentialNames.Contains(name) ||
        name.StartsWith("auth.json", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith(".cockpit_codex_auth.json", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("chrome-native-hosts", StringComparison.OrdinalIgnoreCase);

    private static bool IsKnownNativeState(string name) =>
        name.Equals("memories", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("sqlite", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("session_index.jsonl", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith(".codex-global-state.json", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".personality_migration", StringComparison.OrdinalIgnoreCase) ||
        IsSqliteFamily(name, "state_") ||
        IsSqliteFamily(name, "logs_") ||
        IsSqliteFamily(name, "goals_") ||
        IsSqliteFamily(name, "memories_");

    private static bool IsSqliteFamily(string name, string prefix) =>
        name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
        (name.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase) ||
         name.Contains(".sqlite-", StringComparison.OrdinalIgnoreCase));

    private static string CreateStableId(string name)
    {
        var hash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(name.ToUpperInvariant())));
        return $"codex-{hash[..16].ToLowerInvariant()}";
    }

    private sealed record ItemStatistics(long FileCount, long Bytes);

    private sealed record Classification(
        BackupDataKind Kind,
        BackupPolicy Policy,
        RestoreLevel RestoreLevel,
        string Reason,
        bool RequiresCodexStopped,
        bool ContainsPotentialSecrets);
}
