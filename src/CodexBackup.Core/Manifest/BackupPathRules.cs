namespace CodexBackup.Core.Manifest;

public static class BackupPathRules
{
    private static readonly HashSet<string> WindowsReservedNames = new(
        [
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        ],
        StringComparer.OrdinalIgnoreCase);

    public static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            return false;
        }

        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/') ||
            normalized.EndsWith('/') ||
            normalized.Contains("//", StringComparison.Ordinal))
        {
            return false;
        }

        var segments = normalized.Split('/');
        return segments.Length > 0 && segments.All(IsSafeWindowsSegment);
    }

    private static bool IsSafeWindowsSegment(string segment)
    {
        if (segment is "" or "." or ".." ||
            segment.EndsWith('.') ||
            segment.EndsWith(' ') ||
            segment.Any(character => char.IsControl(character)) ||
            segment.IndexOfAny(['<', '>', ':', '"', '/', '\\', '|', '?', '*']) >= 0)
        {
            return false;
        }

        var baseName = segment.Split('.')[0];
        return !WindowsReservedNames.Contains(baseName);
    }
}
