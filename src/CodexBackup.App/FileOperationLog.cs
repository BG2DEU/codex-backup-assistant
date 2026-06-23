using System.IO;
using System.Text;
using CodexBackup.App.Presentation;

namespace CodexBackup.App;

public sealed class FileOperationLog : IOperationLog
{
    private readonly object _gate = new();

    public FileOperationLog(string logDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        LogDirectory = Path.GetFullPath(logDirectory);
        Directory.CreateDirectory(LogDirectory);
        CurrentLogPath = Path.Combine(
            LogDirectory,
            $"codex-backup-{DateTimeOffset.Now:yyyyMMdd}.log");
    }

    public string LogDirectory { get; }

    public string CurrentLogPath { get; }

    public static FileOperationLog CreateDefault()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new FileOperationLog(Path.Combine(root, "CodexBackupAssistant", "logs"));
    }

    public void Info(string message) => Write("INFO", message, null);

    public void Error(string message, Exception? exception = null) =>
        Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        try
        {
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}\t{level}\t{message}";
            if (exception is not null)
            {
                line += $"\t{exception.GetType().Name}: {exception.Message}";
            }

            lock (_gate)
            {
                File.AppendAllText(
                    CurrentLogPath,
                    line + Environment.NewLine,
                    new UTF8Encoding(false));
            }
        }
        catch
        {
            // Logging must never break backup or restore flows.
        }
    }
}
