namespace CodexBackup.App.Presentation;

public interface IOperationLog
{
    string? LogDirectory { get; }

    string? CurrentLogPath { get; }

    void Info(string message);

    void Error(string message, Exception? exception = null);
}
