using System.Reflection;
using System.Windows;
using CodexBackup.App.Presentation;
using CodexBackup.Infrastructure.Windows.Codex;
using CodexBackup.Infrastructure.Windows.Discovery;
using CodexBackup.Infrastructure.Windows.Export;
using CodexBackup.Infrastructure.Windows.Restore;

namespace CodexBackup.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var applicationVersion = GetApplicationVersion();
        Title = $"Codex 换机助手 {applicationVersion}";

        var packageVerifier = new BackupPackageVerifier();
        var codexUsageInspector = new CodexUsageInspector();
        var operationLog = FileOperationLog.CreateDefault();
        DataContext = new MainWindowViewModel(
            WindowsProjectDiscovery.CreateService(),
            exportEngine: new BackupExportEngine(
                contributors:
                [
                    new CodexConversationBackupContributor(),
                    new RestoreToolBackupContributor(
                        Environment.ProcessPath ??
                        throw new InvalidOperationException("无法确定当前程序路径。")),
                ],
                codexAdapterVersion: CodexStorageAdapter.CurrentAdapterVersion),
            packageVerifier: packageVerifier,
            restoreEngine: new BackupRestoreEngine(
                packageVerifier: packageVerifier,
                codexUsageInspector: codexUsageInspector),
            exportInteraction: new WindowsExportInteraction(this),
            operationLog: operationLog,
            codexUsageInspector: codexUsageInspector,
            producerVersion: applicationVersion);
    }

    private static string GetApplicationVersion() =>
        typeof(MainWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ??
        typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ??
        "1.0.0-preview.1";
}
