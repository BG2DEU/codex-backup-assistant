using System.Diagnostics;
using System.Text;
using CodexBackup.Core.Discovery;

namespace CodexBackup.Infrastructure.Windows.Discovery;

public sealed class ProjectGitStatusReader(TimeSpan? commandTimeout = null)
{
    private readonly TimeSpan _commandTimeout = commandTimeout ?? TimeSpan.FromSeconds(5);

    public ProjectGitStatus Read(string projectRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        cancellationToken.ThrowIfCancellationRequested();

        var statusResult = RunGit(
            projectRoot,
            ["status", "--porcelain=v1", "--branch", "--untracked-files=all", "--no-renames"],
            cancellationToken);
        if (!statusResult.Succeeded)
        {
            return new ProjectGitStatus(
                null,
                false,
                false,
                0,
                0,
                ErrorCode: statusResult.ErrorCode,
                ErrorMessage: statusResult.ErrorMessage);
        }

        var remoteResult = RunGit(projectRoot, ["remote"], cancellationToken);
        var hasRemote = remoteResult.Succeeded &&
                        !string.IsNullOrWhiteSpace(remoteResult.StandardOutput);
        return ProjectGitStatusParser.Parse(statusResult.StandardOutput, hasRemote);
    }

    private GitCommandResult RunGit(
        string projectRoot,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                },
            };
            process.StartInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
            process.StartInfo.ArgumentList.Add("-C");
            process.StartInfo.ArgumentList.Add(projectRoot);
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            if (!process.Start())
            {
                return GitCommandResult.Failure("GIT_START_FAILED", "Git process did not start.");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            if (!process.WaitForExit((int)_commandTimeout.TotalMilliseconds))
            {
                process.Kill(entireProcessTree: true);
                return GitCommandResult.Failure("GIT_TIMEOUT", "Git status timed out.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var output = outputTask.GetAwaiter().GetResult();
            var error = errorTask.GetAwaiter().GetResult();
            return process.ExitCode == 0
                ? GitCommandResult.Success(output)
                : GitCommandResult.Failure(
                    "GIT_COMMAND_FAILED",
                    string.IsNullOrWhiteSpace(error) ? "Git command failed." : error.Trim());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return GitCommandResult.Failure("GIT_UNAVAILABLE", exception.Message);
        }
    }

    private sealed record GitCommandResult(
        bool Succeeded,
        string StandardOutput,
        string? ErrorCode,
        string? ErrorMessage)
    {
        public static GitCommandResult Success(string output) => new(true, output, null, null);

        public static GitCommandResult Failure(string code, string message) => new(false, string.Empty, code, message);
    }
}

public static class ProjectGitStatusParser
{
    public static ProjectGitStatus Parse(string output, bool hasRemote)
    {
        ArgumentNullException.ThrowIfNull(output);

        string? branchName = null;
        string? aheadBehindSummary = null;
        var isDetachedHead = false;
        var changedTrackedFiles = 0;
        var untrackedFiles = 0;

        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                ParseHeader(line[3..], out branchName, out isDetachedHead, out aheadBehindSummary);
            }
            else if (line.StartsWith("?? ", StringComparison.Ordinal))
            {
                untrackedFiles++;
            }
            else if (line.Length >= 3)
            {
                changedTrackedFiles++;
            }
        }

        return new ProjectGitStatus(
            branchName,
            isDetachedHead,
            hasRemote,
            changedTrackedFiles,
            untrackedFiles,
            aheadBehindSummary);
    }

    private static void ParseHeader(
        string header,
        out string? branchName,
        out bool isDetachedHead,
        out string? aheadBehindSummary)
    {
        branchName = null;
        aheadBehindSummary = null;
        isDetachedHead = header.StartsWith("HEAD ", StringComparison.OrdinalIgnoreCase);
        if (isDetachedHead)
        {
            return;
        }

        const string noCommitsPrefix = "No commits yet on ";
        const string initialCommitPrefix = "Initial commit on ";
        if (header.StartsWith(noCommitsPrefix, StringComparison.Ordinal))
        {
            header = header[noCommitsPrefix.Length..];
        }
        else if (header.StartsWith(initialCommitPrefix, StringComparison.Ordinal))
        {
            header = header[initialCommitPrefix.Length..];
        }

        var detailStart = header.IndexOf(" [", StringComparison.Ordinal);
        if (detailStart >= 0)
        {
            aheadBehindSummary = header[(detailStart + 2)..].TrimEnd(']');
            header = header[..detailStart];
        }

        var upstreamStart = header.IndexOf("...", StringComparison.Ordinal);
        branchName = (upstreamStart >= 0 ? header[..upstreamStart] : header).Trim();
        if (branchName.Length == 0)
        {
            branchName = null;
        }
    }
}
