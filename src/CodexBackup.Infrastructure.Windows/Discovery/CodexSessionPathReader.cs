using System.Text.Json;
using CodexBackup.Core.Discovery;

namespace CodexBackup.Infrastructure.Windows.Discovery;

public sealed class CodexSessionPathReader
{
    private const int MaximumMetadataLines = 32;

    public CodexSessionPathReadResult Read(IEnumerable<string> sessionRoots)
    {
        ArgumentNullException.ThrowIfNull(sessionRoots);

        var records = new List<CodexSessionPathRecord>();
        var warnings = new List<DiscoveryWarning>();
        var scannedFileCount = 0;

        foreach (var root in sessionRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root))
            {
                warnings.Add(new DiscoveryWarning(
                    "CODEX_SESSION_ROOT_MISSING",
                    root,
                    "Codex session root does not exist."));
                continue;
            }

            foreach (var file in EnumerateSessionFiles(root, warnings))
            {
                scannedFileCount++;
                var workingDirectory = TryReadWorkingDirectory(file, warnings);
                if (workingDirectory is null)
                {
                    continue;
                }

                records.Add(new CodexSessionPathRecord(
                    file,
                    workingDirectory,
                    Directory.Exists(workingDirectory)));
            }
        }

        return new CodexSessionPathReadResult(records, warnings, scannedFileCount);
    }

    private static IEnumerable<string> EnumerateSessionFiles(
        string root,
        List<DiscoveryWarning> warnings)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MatchCasing = MatchCasing.CaseInsensitive,
        };

        try
        {
            return Directory.EnumerateFiles(root, "*.jsonl", options).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add(new DiscoveryWarning(
                "CODEX_SESSION_ENUMERATION_FAILED",
                root,
                exception.Message));
            return [];
        }
    }

    private static string? TryReadWorkingDirectory(
        string file,
        List<DiscoveryWarning> warnings)
    {
        try
        {
            using var stream = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                4096,
                FileOptions.SequentialScan);
            using var reader = new StreamReader(stream);
            for (var lineNumber = 0; lineNumber < MaximumMetadataLines; lineNumber++)
            {
                var line = reader.ReadLine();
                if (line is null)
                {
                    break;
                }

                if (TryExtractWorkingDirectory(line, out var workingDirectory))
                {
                    return Path.GetFullPath(workingDirectory);
                }
            }

            warnings.Add(new DiscoveryWarning(
                "CODEX_SESSION_CWD_NOT_FOUND",
                file,
                $"No working directory was found in the first {MaximumMetadataLines} lines."));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add(new DiscoveryWarning(
                "CODEX_SESSION_READ_FAILED",
                file,
                exception.Message));
        }

        return null;
    }

    private static bool TryExtractWorkingDirectory(string line, out string workingDirectory)
    {
        workingDirectory = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(line);
            if (!document.RootElement.TryGetProperty("payload", out var payload) ||
                !payload.TryGetProperty("cwd", out var cwd) ||
                cwd.ValueKind is not JsonValueKind.String)
            {
                return false;
            }

            var value = cwd.GetString();
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            {
                return false;
            }

            workingDirectory = value;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
