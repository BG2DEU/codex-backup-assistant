using System.Diagnostics;
using CodexBackup.Core.Codex;

namespace CodexBackup.Infrastructure.Windows.Codex;

public sealed class CodexUsageInspector(Func<int>? runningProcessCountProvider = null)
{
    private readonly Func<int> _runningProcessCountProvider =
        runningProcessCountProvider ?? CountRunningCodexProcesses;

    public CodexUsageStatus Inspect(string codexRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codexRoot);

        var warningCodes = new List<string>();
        var lockedDatabaseCount = 0;
        var sidecarCount = 0;
        if (Directory.Exists(codexRoot))
        {
            foreach (var path in FindDatabaseFiles(codexRoot, warningCodes))
            {
                if (path.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith("-shm", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith("-journal", StringComparison.OrdinalIgnoreCase))
                {
                    sidecarCount++;
                    continue;
                }

                try
                {
                    using var stream = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.None,
                        1,
                        FileOptions.RandomAccess);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    lockedDatabaseCount++;
                }
            }
        }

        var runningProcessCount = _runningProcessCountProvider();
        if (runningProcessCount > 0)
        {
            warningCodes.Add("CODEX_PROCESS_RUNNING");
        }

        if (lockedDatabaseCount > 0)
        {
            warningCodes.Add("CODEX_DATABASE_LOCKED");
        }

        if (sidecarCount > 0)
        {
            warningCodes.Add("CODEX_DATABASE_SIDECARS_PRESENT");
        }

        return new CodexUsageStatus(
            runningProcessCount,
            lockedDatabaseCount,
            sidecarCount,
            warningCodes);
    }

    private static IReadOnlyList<string> FindDatabaseFiles(
        string codexRoot,
        ICollection<string> warningCodes)
    {
        var files = new List<string>();
        try
        {
            files.AddRange(Directory.GetFiles(codexRoot, "*.sqlite*", SearchOption.TopDirectoryOnly));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warningCodes.Add("CODEX_DATABASE_ENUMERATION_FAILED");
        }

        var sqliteRoot = Path.Combine(codexRoot, "sqlite");
        if (!Directory.Exists(sqliteRoot))
        {
            return files;
        }

        var pending = new Queue<string>();
        pending.Enqueue(sqliteRoot);
        while (pending.TryDequeue(out var directory))
        {
            try
            {
                foreach (var entry in Directory.GetFileSystemEntries(directory))
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
                    else if (Path.GetFileName(entry).Contains(".sqlite", StringComparison.OrdinalIgnoreCase))
                    {
                        files.Add(entry);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                warningCodes.Add("CODEX_DATABASE_ENUMERATION_FAILED");
            }
        }

        return files;
    }

    private static int CountRunningCodexProcesses()
    {
        var currentProcessId = Environment.ProcessId;
        return Process.GetProcesses()
            .Count(process =>
            {
                using (process)
                {
                    try
                    {
                        return process.Id != currentProcessId &&
                               process.ProcessName.Equals("codex", StringComparison.OrdinalIgnoreCase);
                    }
                    catch (InvalidOperationException)
                    {
                        return false;
                    }
                }
            });
    }
}
