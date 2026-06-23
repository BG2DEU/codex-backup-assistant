using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using CodexBackup.Core.Backup;
using CodexBackup.Core.Codex;
using CodexBackup.Core.Export;
using CodexBackup.Core.Restore;
using CodexBackup.Infrastructure.Windows.Codex;
using CodexBackup.Infrastructure.Windows.Discovery;
using CodexBackup.Infrastructure.Windows.Export;
using CodexBackup.Infrastructure.Windows.Restore;

namespace CodexBackup.App.Presentation;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly ProjectDiscoveryService _discoveryService;
    private readonly CodexStorageAdapter _codexStorageAdapter;
    private readonly CodexUsageInspector _codexUsageInspector;
    private readonly BackupPlanBuilder _backupPlanBuilder;
    private readonly BackupExportEngine? _exportEngine;
    private readonly BackupPackageVerifier? _packageVerifier;
    private readonly BackupRestoreEngine? _restoreEngine;
    private readonly IExportInteraction? _exportInteraction;
    private readonly IOperationLog _operationLog;
    private readonly string _producerVersion;
    private readonly string _codexRoot;
    private string _statusText = "当前阶段：项目与 Codex 原始数据的可验证导出";
    private string _restoreSummaryText = "尚未执行恢复。恢复完成后这里会显示恢复摘要和报告位置。";
    private string _restoreReportPathText = string.Empty;
    private string? _lastRestoreReportPath;
    private bool _isScanning;
    private bool _isExporting;
    private bool _isVerifying;
    private bool _isRestoring;
    private BackupPlan? _currentPlan;
    private CodexUsageStatus? _codexUsageStatus;
    private CancellationTokenSource? _exportCancellation;
    private CancellationTokenSource? _restoreCancellation;

    public MainWindowViewModel(
        ProjectDiscoveryService discoveryService,
        BackupPlanBuilder? backupPlanBuilder = null,
        BackupExportEngine? exportEngine = null,
        BackupPackageVerifier? packageVerifier = null,
        BackupRestoreEngine? restoreEngine = null,
        IExportInteraction? exportInteraction = null,
        IOperationLog? operationLog = null,
        string producerVersion = "0.1.0",
        CodexStorageAdapter? codexStorageAdapter = null,
        CodexUsageInspector? codexUsageInspector = null,
        string? codexRoot = null)
    {
        _discoveryService = discoveryService;
        _backupPlanBuilder = backupPlanBuilder ?? new BackupPlanBuilder();
        _exportEngine = exportEngine;
        _packageVerifier = packageVerifier;
        _restoreEngine = restoreEngine;
        _exportInteraction = exportInteraction;
        _operationLog = operationLog ?? NullOperationLog.Instance;
        _producerVersion = producerVersion;
        _codexStorageAdapter = codexStorageAdapter ?? new CodexStorageAdapter();
        _codexUsageInspector = codexUsageInspector ?? new CodexUsageInspector();
        _codexRoot = codexRoot ?? CodexStorageAdapter.GetDefaultRoot();
        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsBusy);
        BuildPlanCommand = new AsyncRelayCommand(
            BuildPlanAsync,
            () => !IsBusy && (Projects.Count > 0 || CodexItems.Count > 0));
        ExportCommand = new AsyncRelayCommand(ExportAsync, CanExport);
        CancelExportCommand = new AsyncRelayCommand(CancelExportAsync, () => IsExporting);
        VerifyBackupCommand = new AsyncRelayCommand(VerifyBackupAsync, CanUseBackupPackageTools);
        RestoreCommand = new AsyncRelayCommand(RestoreAsync, CanUseBackupPackageTools);
        CancelRestoreCommand = new AsyncRelayCommand(CancelRestoreAsync, () => IsRestoring);
        OpenRestoreReportCommand = new AsyncRelayCommand(
            OpenRestoreReportAsync,
            CanOpenRestoreReport);
        ShowNewComputerGuideCommand = new AsyncRelayCommand(
            ShowNewComputerGuideAsync,
            () => !IsBusy && _exportInteraction is not null);
        OpenLogFolderCommand = new AsyncRelayCommand(
            OpenLogFolderAsync,
            CanOpenLogFolder);
        _operationLog.Info($"APP_START version={_producerVersion}");
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ProjectListItem> Projects { get; } = [];

    public ObservableCollection<CodexDataListItem> CodexItems { get; } = [];

    public AsyncRelayCommand ScanCommand { get; }

    public AsyncRelayCommand BuildPlanCommand { get; }

    public AsyncRelayCommand ExportCommand { get; }

    public AsyncRelayCommand CancelExportCommand { get; }

    public AsyncRelayCommand VerifyBackupCommand { get; }

    public AsyncRelayCommand RestoreCommand { get; }

    public AsyncRelayCommand CancelRestoreCommand { get; }

    public AsyncRelayCommand OpenRestoreReportCommand { get; }

    public AsyncRelayCommand ShowNewComputerGuideCommand { get; }

    public AsyncRelayCommand OpenLogFolderCommand { get; }

    public BackupPlan? CurrentPlan
    {
        get => _currentPlan;
        private set
        {
            if (SetField(ref _currentPlan, value))
            {
                ExportCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public string RestoreSummaryText
    {
        get => _restoreSummaryText;
        private set => SetField(ref _restoreSummaryText, value);
    }

    public string RestoreReportPathText
    {
        get => _restoreReportPathText;
        private set => SetField(ref _restoreReportPathText, value);
    }

    public string? LastRestoreReportPath
    {
        get => _lastRestoreReportPath;
        private set
        {
            if (SetField(ref _lastRestoreReportPath, value))
            {
                RestoreReportPathText = string.IsNullOrWhiteSpace(value)
                    ? string.Empty
                    : $"恢复报告：{value}";
                OpenRestoreReportCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetField(ref _isScanning, value))
            {
                RaiseCommandStates();
                OnPropertyChanged(nameof(CanEditItems));
            }
        }
    }

    public bool IsExporting
    {
        get => _isExporting;
        private set
        {
            if (SetField(ref _isExporting, value))
            {
                RaiseCommandStates();
                OnPropertyChanged(nameof(CanEditItems));
            }
        }
    }

    public bool IsVerifying
    {
        get => _isVerifying;
        private set
        {
            if (SetField(ref _isVerifying, value))
            {
                RaiseCommandStates();
                OnPropertyChanged(nameof(CanEditItems));
            }
        }
    }

    public bool IsRestoring
    {
        get => _isRestoring;
        private set
        {
            if (SetField(ref _isRestoring, value))
            {
                RaiseCommandStates();
                OnPropertyChanged(nameof(CanEditItems));
            }
        }
    }

    public bool CanEditItems => !IsBusy;

    private async Task ScanAsync()
    {
        IsScanning = true;
        StatusText = "正在发现项目、盘点 Codex 数据并检查数据库占用状态……";
        _operationLog.Info("SCAN_START");
        CurrentPlan = null;
        Projects.Clear();
        CodexItems.Clear();

        try
        {
            var projectTask = Task.Run(() => _discoveryService.Discover(
                WindowsProjectDiscovery.CreateDefaultRequest()));
            var codexTask = Task.Run(() => _codexStorageAdapter.Inspect(_codexRoot));
            await Task.WhenAll(projectTask, codexTask);
            var projectResult = await projectTask;
            var codexResult = await codexTask;
            _codexUsageStatus = await Task.Run(() => _codexUsageInspector.Inspect(_codexRoot));

            foreach (var project in projectResult.Projects)
            {
                var item = new ProjectListItem(project);
                item.PropertyChanged += SelectionChanged;
                Projects.Add(item);
            }

            foreach (var codexData in codexResult.Items)
            {
                var item = new CodexDataListItem(codexData);
                item.PropertyChanged += SelectionChanged;
                CodexItems.Add(item);
            }

            var credentialCount = codexResult.Items.Count(item =>
                item.Policy is BackupPolicy.ExcludeCredential);
            var usageText = _codexUsageStatus.CanCreateNativeSnapshot
                ? "Codex 已停止，可创建原生快照。"
                : $"检测到 {_codexUsageStatus.RunningProcessCount} 个 Codex 进程或 {_codexUsageStatus.LockedDatabaseCount} 个锁定数据库，导出前必须退出 Codex。";
            StatusText = $"发现 {projectResult.Projects.Count} 个项目和 {codexResult.Items.Count} 个 Codex 顶层项；" +
                         $"{credentialCount} 个凭据项已强制排除。{usageText}";
            _operationLog.Info(
                $"SCAN_SUCCESS projects={projectResult.Projects.Count} codexItems={codexResult.Items.Count} credentialsExcluded={credentialCount} codexProcesses={_codexUsageStatus.RunningProcessCount} lockedDatabases={_codexUsageStatus.LockedDatabaseCount}");
        }
        catch (Exception exception)
        {
            StatusText = $"扫描失败：{exception.Message}";
            _operationLog.Error("SCAN_FAILED", exception);
        }
        finally
        {
            IsScanning = false;
            BuildPlanCommand.RaiseCanExecuteChanged();
        }
    }

    private Task BuildPlanAsync()
    {
        var projectCandidates = Projects.Select(item => ProjectBackupCandidateFactory.Create(
            item.Project,
            item.IsSelected,
            isReviewApproved: item.RequiresReview && item.IsSelected));
        var codexCandidates = CodexItems.Select(item => CodexBackupCandidateFactory.Create(
            item.Item,
            item.IsSelected,
            isReviewApproved: item.RequiresReview && item.IsSelected));
        var plan = _backupPlanBuilder.Build(projectCandidates.Concat(codexCandidates));

        if (_codexUsageStatus is not null)
        {
            plan = CodexSnapshotPlanGuard.Apply(
                plan,
                CodexItems.Select(item => item.Item),
                _codexUsageStatus);
        }

        CurrentPlan = plan;
        var includedProjects = plan.Items.Count(item =>
            item.Kind is BackupDataKind.Project && item.State is BackupPlanItemState.Included);
        var includedCodexItems = plan.Items.Count(item =>
            item.Kind is not BackupDataKind.Project && item.State is BackupPlanItemState.Included);
        StatusText = plan.CanExport
            ? $"计划已生成：{includedProjects} 个项目、{includedCodexItems} 个 Codex 数据项，预计 {ProjectListItem.FormatBytes(plan.IncludedBytes)}。"
            : $"计划暂不可导出：{FormatBlockingIssue(plan.Issues.First(issue => issue.Severity is BackupPlanIssueSeverity.Blocking))}";
        _operationLog.Info(
            plan.CanExport
                ? $"PLAN_READY projects={includedProjects} codexItems={includedCodexItems} bytes={plan.IncludedBytes}"
                : $"PLAN_BLOCKED code={plan.Issues.First(issue => issue.Severity is BackupPlanIssueSeverity.Blocking).Code}");

        return Task.CompletedTask;
    }

    private async Task ExportAsync()
    {
        if (CurrentPlan is not { CanExport: true } plan ||
            _exportEngine is null ||
            _exportInteraction is null)
        {
            return;
        }

        if (HasSelectedNativeCodexData())
        {
            var currentUsage = await Task.Run(() => _codexUsageInspector.Inspect(_codexRoot));
            if (!currentUsage.CanCreateNativeSnapshot)
            {
                CurrentPlan = null;
                StatusText = "Codex 已重新运行或数据库被占用。请退出 Codex、重新扫描并生成计划。";
                return;
            }
        }

        var destination = _exportInteraction.SelectDestination();
        if (string.IsNullOrWhiteSpace(destination))
        {
            StatusText = "已取消选择导出位置，未写入任何文件。";
            _operationLog.Info("EXPORT_CANCELLED reason=destination-not-selected");
            return;
        }

        var selectedProjects = Projects.Where(item => item.IsSelected).ToArray();
        var selectedCodexItems = CodexItems.Where(item => item.IsSelected).ToArray();
        var secretRiskItems = selectedProjects.Count(item =>
                                  item.Project.FileInventory?.PotentialSecretFileCount > 0) +
                              selectedCodexItems.Count(item => item.Item.ContainsPotentialSecrets);
        if (!_exportInteraction.ConfirmExport(
                selectedProjects.Length,
                selectedCodexItems.Length,
                plan.IncludedBytes,
                secretRiskItems))
        {
            StatusText = "已取消备份导出，未写入备份包。";
            _operationLog.Info("EXPORT_CANCELLED reason=user-confirmation");
            return;
        }

        _operationLog.Info(
            $"EXPORT_START destination={destination} items={plan.Items.Count(item => item.State is BackupPlanItemState.Included)} bytes={plan.IncludedBytes}");
        _exportCancellation = new CancellationTokenSource();
        IsExporting = true;
        var stopwatch = Stopwatch.StartNew();
        var progress = new Progress<BackupExportProgress>(value =>
            UpdateExportProgress(value, stopwatch.Elapsed));

        try
        {
            var result = await _exportEngine.ExportAsync(
                plan,
                destination,
                _producerVersion,
                progress,
                _exportCancellation.Token);
            StatusText = result.Status switch
            {
                BackupExportStatus.Success =>
                    $"备份导出并完整校验成功：{result.CopiedFileCount:N0} 个文件，{ProjectListItem.FormatBytes(result.CopiedBytes)}。位置：{result.CompletedPackagePath}",
                BackupExportStatus.PartialSuccess =>
                    $"备份主体已完成并校验，但通用对话转换存在 {result.Issues.Count} 项提示。原始会话仍已保存。位置：{result.CompletedPackagePath}",
                BackupExportStatus.Cancelled =>
                    $"导出已取消，未完成包保留在：{result.IncompletePackagePath}",
                _ =>
                    $"导出失败：{result.Issues.FirstOrDefault()?.Message ?? "未知错误"}。未完成包：{result.IncompletePackagePath ?? "未创建"}",
            };
            _operationLog.Info(
                $"EXPORT_RESULT status={result.Status} completed={result.CompletedPackagePath ?? string.Empty} incomplete={result.IncompletePackagePath ?? string.Empty} files={result.CopiedFileCount} bytes={result.CopiedBytes} firstIssue={FormatBackupIssueForLog(result.Issues.FirstOrDefault())}");
        }
        catch (Exception exception)
        {
            StatusText = $"导出流程异常：{exception.Message}";
            _operationLog.Error("EXPORT_EXCEPTION", exception);
        }
        finally
        {
            stopwatch.Stop();
            _exportCancellation.Dispose();
            _exportCancellation = null;
            IsExporting = false;
        }
    }

    private Task CancelExportAsync()
    {
        _exportCancellation?.Cancel();
        StatusText = "正在安全取消：完成当前数据块后会保留未完成标记……";
        _operationLog.Info("EXPORT_CANCEL_REQUESTED");
        return Task.CompletedTask;
    }

    private async Task VerifyBackupAsync()
    {
        if (_packageVerifier is null || _exportInteraction is null)
        {
            return;
        }

        var packagePath = _exportInteraction.SelectBackupPackage();
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            StatusText = "已取消选择备份包，未执行校验。";
            _operationLog.Info("VERIFY_CANCELLED reason=package-not-selected");
            return;
        }

        _operationLog.Info($"VERIFY_START package={packagePath}");
        IsVerifying = true;
        var stopwatch = Stopwatch.StartNew();
        var progress = new Progress<BackupExportProgress>(value =>
            UpdateBackupVerificationProgress(value, stopwatch.Elapsed));

        try
        {
            StatusText = "正在从备份介质重新读取文件并校验清单……";
            var result = await _packageVerifier.VerifyAsync(packagePath, progress);
            StatusText = result.IsValid
                ? $"备份校验通过：{result.VerifiedFileCount:N0} 个文件，{ProjectListItem.FormatBytes(result.VerifiedBytes)}。位置：{packagePath}"
                : $"备份校验失败：{result.Issues.Count} 个问题。首个问题：{FormatBackupIssue(result.Issues.First())}";
            _operationLog.Info(
                $"VERIFY_RESULT valid={result.IsValid} files={result.VerifiedFileCount} bytes={result.VerifiedBytes} firstIssue={FormatBackupIssueForLog(result.Issues.FirstOrDefault())}");
        }
        catch (OperationCanceledException)
        {
            StatusText = "备份校验已取消。";
            _operationLog.Info("VERIFY_CANCELLED reason=operation-cancelled");
        }
        catch (Exception exception)
        {
            StatusText = $"备份校验异常：{exception.Message}";
            _operationLog.Error("VERIFY_EXCEPTION", exception);
        }
        finally
        {
            stopwatch.Stop();
            IsVerifying = false;
        }
    }

    private async Task RestoreAsync()
    {
        if (_restoreEngine is null || _exportInteraction is null)
        {
            return;
        }

        var packagePath = _exportInteraction.SelectBackupPackage();
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            StatusText = "已取消选择备份包，未执行恢复。";
            _operationLog.Info("RESTORE_CANCELLED reason=package-not-selected");
            return;
        }

        var projectRestoreRoot = _exportInteraction.SelectProjectRestoreRoot();
        if (string.IsNullOrWhiteSpace(projectRestoreRoot))
        {
            StatusText = "已取消选择项目恢复目录，未执行恢复。";
            _operationLog.Info("RESTORE_CANCELLED reason=project-root-not-selected");
            return;
        }

        var restoreNativeCodexState = _exportInteraction.ConfirmNativeCodexRestore();
        if (!_exportInteraction.ConfirmRestore(
                packagePath,
                projectRestoreRoot,
                restoreNativeCodexState))
        {
            StatusText = "已取消恢复，未写入任何文件。";
            _operationLog.Info("RESTORE_CANCELLED reason=user-confirmation");
            return;
        }

        _operationLog.Info(
            $"RESTORE_START package={packagePath} projectRoot={projectRestoreRoot} native={restoreNativeCodexState}");
        LastRestoreReportPath = null;
        RestoreSummaryText = "正在恢复。完成后会在这里显示项目、Codex 数据、跳过项和报告位置。";
        var portableRoot = Path.Combine(projectRestoreRoot, "Codex换机恢复资料");
        var request = new RestoreRequest(
            Path.GetFullPath(packagePath),
            Path.GetFullPath(projectRestoreRoot),
            Path.GetFullPath(_codexRoot),
            Path.GetFullPath(portableRoot),
            RestoreNativeCodexState: restoreNativeCodexState);

        _restoreCancellation = new CancellationTokenSource();
        IsRestoring = true;
        var stopwatch = Stopwatch.StartNew();
        var progress = new Progress<RestoreProgress>(value =>
            UpdateRestoreProgress(value, stopwatch.Elapsed));

        try
        {
            var result = await _restoreEngine.RestoreAsync(
                request,
                progress,
                _restoreCancellation.Token);
            StatusText = result.Status switch
            {
                RestoreStatus.Success =>
                    $"恢复完成：{result.RestoredFileCount:N0} 个文件，{ProjectListItem.FormatBytes(result.RestoredBytes)}。项目位置：{projectRestoreRoot}",
                RestoreStatus.PartialSuccess =>
                    $"恢复主体完成，但有 {result.Issues.Count} 项提示。项目位置：{projectRestoreRoot}",
                RestoreStatus.Cancelled =>
                    $"恢复已取消，已执行安全回滚。回滚目录：{result.RollbackPath ?? "未创建"}",
                RestoreStatus.RolledBack =>
                    $"恢复失败并已回滚。首个问题：{FormatRestoreIssue(result.Issues.FirstOrDefault())}",
                _ =>
                    $"恢复失败：{FormatRestoreIssue(result.Issues.FirstOrDefault())}",
            };
            LastRestoreReportPath = result.ReportHtmlPath;
            RestoreSummaryText = FormatRestoreSummary(result);
            _operationLog.Info(
                $"RESTORE_RESULT status={result.Status} files={result.RestoredFileCount} bytes={result.RestoredBytes} report={result.ReportHtmlPath ?? string.Empty} rollback={result.RollbackPath ?? string.Empty} firstIssue={FormatRestoreIssueForLog(result.Issues.FirstOrDefault())}");
        }
        catch (Exception exception)
        {
            StatusText = $"恢复流程异常：{exception.Message}";
            RestoreSummaryText = "恢复流程异常，未能生成完整摘要。请查看状态栏错误信息。";
            _operationLog.Error("RESTORE_EXCEPTION", exception);
        }
        finally
        {
            stopwatch.Stop();
            _restoreCancellation.Dispose();
            _restoreCancellation = null;
            IsRestoring = false;
        }
    }

    private Task CancelRestoreAsync()
    {
        _restoreCancellation?.Cancel();
        StatusText = "正在安全取消恢复：已写入项目会按回滚记录清理……";
        _operationLog.Info("RESTORE_CANCEL_REQUESTED");
        return Task.CompletedTask;
    }

    private Task OpenRestoreReportAsync()
    {
        if (LastRestoreReportPath is null || !File.Exists(LastRestoreReportPath))
        {
            StatusText = "没有可打开的恢复报告。";
            return Task.CompletedTask;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = LastRestoreReportPath,
                UseShellExecute = true,
            });
            StatusText = $"已打开恢复报告：{LastRestoreReportPath}";
            _operationLog.Info($"OPEN_RESTORE_REPORT path={LastRestoreReportPath}");
        }
        catch (Exception exception)
        {
            StatusText = $"打开恢复报告失败：{exception.Message}";
            _operationLog.Error("OPEN_RESTORE_REPORT_FAILED", exception);
        }

        return Task.CompletedTask;
    }

    private Task OpenLogFolderAsync()
    {
        if (string.IsNullOrWhiteSpace(_operationLog.LogDirectory) ||
            !Directory.Exists(_operationLog.LogDirectory))
        {
            StatusText = "当前没有可打开的日志目录。";
            return Task.CompletedTask;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _operationLog.LogDirectory,
                UseShellExecute = true,
            });
            StatusText = $"已打开日志目录：{_operationLog.LogDirectory}";
            _operationLog.Info($"OPEN_LOG_FOLDER path={_operationLog.LogDirectory}");
        }
        catch (Exception exception)
        {
            StatusText = $"打开日志目录失败：{exception.Message}";
            _operationLog.Error("OPEN_LOG_FOLDER_FAILED", exception);
        }

        return Task.CompletedTask;
    }

    private Task ShowNewComputerGuideAsync()
    {
        if (_exportInteraction is null)
        {
            StatusText = "当前运行环境没有可用的说明弹窗。";
            return Task.CompletedTask;
        }

        _exportInteraction.ShowNewComputerGuide();
        StatusText = "已显示新电脑操作说明。前提：新电脑已经安装并登录过 Codex，恢复前请完全退出 Codex。";
        _operationLog.Info("SHOW_NEW_COMPUTER_GUIDE");
        return Task.CompletedTask;
    }

    private void UpdateExportProgress(BackupExportProgress progress, TimeSpan elapsed)
    {
        var speed = elapsed.TotalSeconds > 0 ? progress.ProcessedBytes / elapsed.TotalSeconds : 0;
        var remainingSeconds = speed > 0
            ? (progress.TotalBytes - progress.ProcessedBytes) / speed
            : 0;
        var stage = progress.Stage switch
        {
            BackupExportStage.Planning => "盘点与空间检查",
            BackupExportStage.Copying => "复制",
            BackupExportStage.Transforming => "生成通用对话",
            BackupExportStage.Verifying => "从目标重新校验",
            BackupExportStage.Committing => "提交完成包",
            _ => progress.Stage.ToString(),
        };
        var remaining = remainingSeconds > 0
            ? $"，预计剩余 {TimeSpan.FromSeconds(remainingSeconds):hh\\:mm\\:ss}"
            : string.Empty;
        StatusText = $"{stage}：{progress.CompletedFiles:N0}/{progress.TotalFiles:N0} 个文件，" +
                     $"{ProjectListItem.FormatBytes(progress.ProcessedBytes)}/{ProjectListItem.FormatBytes(progress.TotalBytes)}，" +
                     $"{ProjectListItem.FormatBytes((long)speed)}/秒{remaining}";
    }

    private void UpdateBackupVerificationProgress(BackupExportProgress progress, TimeSpan elapsed)
    {
        var speed = elapsed.TotalSeconds > 0 ? progress.ProcessedBytes / elapsed.TotalSeconds : 0;
        var remainingSeconds = speed > 0
            ? (progress.TotalBytes - progress.ProcessedBytes) / speed
            : 0;
        var remaining = remainingSeconds > 0
            ? $"，预计剩余 {TimeSpan.FromSeconds(remainingSeconds):hh\\:mm\\:ss}"
            : string.Empty;
        StatusText = $"校验备份：{progress.CompletedFiles:N0}/{progress.TotalFiles:N0} 个文件，" +
                     $"{ProjectListItem.FormatBytes(progress.ProcessedBytes)}/{ProjectListItem.FormatBytes(progress.TotalBytes)}，" +
                     $"{ProjectListItem.FormatBytes((long)speed)}/秒{remaining}";
    }

    private void UpdateRestoreProgress(RestoreProgress progress, TimeSpan elapsed)
    {
        var speed = elapsed.TotalSeconds > 0 ? progress.ProcessedBytes / elapsed.TotalSeconds : 0;
        var remainingSeconds = speed > 0
            ? (progress.TotalBytes - progress.ProcessedBytes) / speed
            : 0;
        var stage = progress.Stage switch
        {
            RestoreStage.VerifyingPackage => "校验备份包",
            RestoreStage.Planning => "生成恢复计划",
            RestoreStage.CreatingRollback => "创建回滚副本",
            RestoreStage.Restoring => "恢复文件",
            RestoreStage.VerifyingRestoredData => "校验已恢复文件",
            RestoreStage.Completed => "恢复完成",
            RestoreStage.Failed => "恢复失败",
            RestoreStage.RolledBack => "已回滚",
            _ => progress.Stage.ToString(),
        };
        var item = string.IsNullOrWhiteSpace(progress.CurrentItem)
            ? string.Empty
            : $"：{progress.CurrentItem}";
        var remaining = remainingSeconds > 0
            ? $"，预计剩余 {TimeSpan.FromSeconds(remainingSeconds):hh\\:mm\\:ss}"
            : string.Empty;
        StatusText = $"{stage}{item}，{progress.CompletedFiles:N0}/{progress.TotalFiles:N0} 个文件，" +
                     $"{ProjectListItem.FormatBytes(progress.ProcessedBytes)}/{ProjectListItem.FormatBytes(progress.TotalBytes)}，" +
                     $"{ProjectListItem.FormatBytes((long)speed)}/秒{remaining}";
    }

    private void SelectionChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is not (nameof(ProjectListItem.IsSelected) or nameof(CodexDataListItem.IsSelected)) ||
            IsBusy)
        {
            return;
        }

        CurrentPlan = null;
        StatusText = "备份勾选已改变，请重新生成备份计划。";
    }

    private bool HasSelectedNativeCodexData() => CodexItems.Any(item =>
        item.IsSelected && item.Item.RequiresCodexStopped);

    private static string FormatBlockingIssue(BackupPlanIssue issue) => issue.Code switch
    {
        "CODEX_MUST_BE_STOPPED" => "Codex 或其状态数据库仍在使用。请退出 Codex 后重新扫描。",
        _ => issue.Message,
    };

    private static string FormatBackupIssue(BackupExportIssue issue)
    {
        var path = string.IsNullOrWhiteSpace(issue.RelativePath)
            ? string.Empty
            : $"（{issue.RelativePath}）";
        return $"{issue.Code}：{issue.Message}{path}";
    }

    private static string FormatBackupIssueForLog(BackupExportIssue? issue) =>
        issue is null
            ? string.Empty
            : $"{issue.Code}:{issue.Message}";

    private static string FormatRestoreIssue(RestoreIssue? issue) =>
        issue is null
            ? "未知错误"
            : $"{issue.Code}：{issue.Message}";

    private static string FormatRestoreIssueForLog(RestoreIssue? issue) =>
        issue is null
            ? string.Empty
            : $"{issue.Code}:{issue.Message}";

    private static string FormatRestoreSummary(RestoreResult result)
    {
        var restoredProjects = result.Items.Count(item =>
            item.Kind is BackupDataKind.Project &&
            item.State is RestoreItemState.Restored);
        var restoredCodexItems = result.Items.Count(item =>
            item.Kind is not BackupDataKind.Project &&
            !item.PackageItemId.Equals(
                "portable-conversations-v1",
                StringComparison.OrdinalIgnoreCase) &&
            item.State is RestoreItemState.Restored);
        var portableConversationRestored = result.Items.Any(item =>
            item.PackageItemId.Equals(
                "portable-conversations-v1",
                StringComparison.OrdinalIgnoreCase) &&
            item.State is RestoreItemState.Restored);
        var keepBothProjects = result.Items.Count(item =>
            item.Kind is BackupDataKind.Project &&
            item.State is RestoreItemState.Restored &&
            item.TargetPath.Contains("从备份恢复", StringComparison.Ordinal));
        var skippedItems = result.Items.Count(item =>
            item.State is
                RestoreItemState.SkippedByUser or
                RestoreItemState.SkippedExisting or
                RestoreItemState.SkippedIncompatible);
        var failedItems = result.Items.Count(item =>
            item.State is RestoreItemState.Failed or RestoreItemState.RolledBack);
        var issueText = result.Issues.Count == 0
            ? "没有问题。"
            : $"{result.Issues.Count} 项问题或提示，首项：{FormatRestoreIssue(result.Issues[0])}";
        var portableText = portableConversationRestored
            ? "通用对话已恢复。"
            : "通用对话未恢复或备份包中没有通用对话。";
        var reportText = string.IsNullOrWhiteSpace(result.ReportHtmlPath)
            ? "未生成 HTML 报告。"
            : $"HTML 报告已生成。";

        return $"恢复摘要：项目 {restoredProjects} 个，Codex 数据 {restoredCodexItems} 项，" +
               $"{portableText} 同名项目另存 {keepBothProjects} 个，跳过 {skippedItems} 项，失败 {failedItems} 项。{issueText} {reportText}";
    }

    private bool IsBusy => IsScanning || IsExporting || IsVerifying || IsRestoring;

    private bool CanExport() =>
        !IsBusy &&
        CurrentPlan?.CanExport == true &&
        _exportEngine is not null &&
        _exportInteraction is not null;

    private bool CanUseBackupPackageTools() =>
        !IsBusy &&
        _packageVerifier is not null &&
        _restoreEngine is not null &&
        _exportInteraction is not null;

    private bool CanOpenRestoreReport() =>
        !IsBusy &&
        !string.IsNullOrWhiteSpace(LastRestoreReportPath) &&
        File.Exists(LastRestoreReportPath);

    private bool CanOpenLogFolder() =>
        !IsBusy &&
        !string.IsNullOrWhiteSpace(_operationLog.LogDirectory) &&
        Directory.Exists(_operationLog.LogDirectory);

    private void RaiseCommandStates()
    {
        ScanCommand.RaiseCanExecuteChanged();
        BuildPlanCommand.RaiseCanExecuteChanged();
        ExportCommand.RaiseCanExecuteChanged();
        CancelExportCommand.RaiseCanExecuteChanged();
        VerifyBackupCommand.RaiseCanExecuteChanged();
        RestoreCommand.RaiseCanExecuteChanged();
        CancelRestoreCommand.RaiseCanExecuteChanged();
        OpenRestoreReportCommand.RaiseCanExecuteChanged();
        ShowNewComputerGuideCommand.RaiseCanExecuteChanged();
        OpenLogFolderCommand.RaiseCanExecuteChanged();
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
