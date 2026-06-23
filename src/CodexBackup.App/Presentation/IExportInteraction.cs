namespace CodexBackup.App.Presentation;

public interface IExportInteraction
{
    string? SelectDestination();

    bool ConfirmExport(
        int projectCount,
        int codexItemCount,
        long estimatedBytes,
        int secretRiskItemCount);

    string? SelectBackupPackage();

    string? SelectProjectRestoreRoot();

    bool ConfirmNativeCodexRestore();

    bool ConfirmRestore(
        string packagePath,
        string projectRestoreRoot,
        bool restoreNativeCodexState);

    void ShowNewComputerGuide();
}
