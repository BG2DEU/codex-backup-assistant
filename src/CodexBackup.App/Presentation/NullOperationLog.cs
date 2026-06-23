namespace CodexBackup.App.Presentation;

public sealed class NullOperationLog : IOperationLog
{
    public static NullOperationLog Instance { get; } = new();

    private NullOperationLog()
    {
    }

    public string? LogDirectory => null;

    public string? CurrentLogPath => null;

    public void Info(string message)
    {
    }

    public void Error(string message, Exception? exception = null)
    {
    }
}
