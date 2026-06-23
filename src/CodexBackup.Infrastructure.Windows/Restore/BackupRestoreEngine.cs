using System.Buffers;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexBackup.Core.Backup;
using CodexBackup.Core.Export;
using CodexBackup.Core.Manifest;
using CodexBackup.Core.Restore;
using CodexBackup.Infrastructure.Windows.Codex;
using CodexBackup.Infrastructure.Windows.Export;

namespace CodexBackup.Infrastructure.Windows.Restore;

public sealed class BackupRestoreEngine(
    BackupPackageVerifier? packageVerifier = null,
    RestorePlanBuilder? planBuilder = null,
    CodexUsageInspector? codexUsageInspector = null,
    TimeProvider? timeProvider = null)
{
    private const int BufferSize = 1024 * 1024;

    private static readonly JsonSerializerOptions ReportOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly BackupPackageVerifier _packageVerifier =
        packageVerifier ?? new BackupPackageVerifier();
    private readonly RestorePlanBuilder _planBuilder =
        planBuilder ?? new RestorePlanBuilder();
    private readonly CodexUsageInspector _codexUsageInspector =
        codexUsageInspector ?? new CodexUsageInspector();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<RestoreResult> RestoreAsync(
        RestoreRequest request,
        IProgress<RestoreProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var issues = new List<RestoreIssue>();
        var completedItems = new List<RestorePlanItem>();
        var rollbackActions = new Stack<RollbackAction>();
        long restoredFiles = 0;
        long restoredBytes = 0;
        RestorePlan? plan = null;

        try
        {
            progress?.Report(new RestoreProgress(
                RestoreStage.VerifyingPackage,
                null,
                0,
                0,
                0,
                0));
            var verification = await _packageVerifier.VerifyAsync(
                request.PackagePath,
                cancellationToken: cancellationToken);
            if (!verification.IsValid)
            {
                issues.AddRange(verification.Issues.Select(issue =>
                    new RestoreIssue(
                        issue.Code,
                        issue.Message,
                        true)));
                return new RestoreResult(
                    RestoreStatus.Failed,
                    string.Empty,
                    null,
                    0,
                    0,
                    [],
                    issues);
            }

            var packageIndex = BackupPackageIndexJson.Deserialize(
                await File.ReadAllTextAsync(
                    Path.Combine(request.PackagePath, "package-index.json"),
                    cancellationToken));
            var manifest = BackupManifestJson.Deserialize(
                await File.ReadAllTextAsync(
                    Path.Combine(request.PackagePath, "manifest.json"),
                    cancellationToken));

            progress?.Report(new RestoreProgress(
                RestoreStage.Planning,
                null,
                0,
                manifest.Entries.Count,
                0,
                manifest.Entries.Sum(entry => entry.Length)));
            plan = _planBuilder.Build(
                request,
                packageIndex,
                CodexStorageAdapter.CurrentAdapterVersion);
            issues.AddRange(plan.Issues);
            if (!plan.CanRestore)
            {
                return new RestoreResult(
                    RestoreStatus.Failed,
                    plan.BackupId,
                    null,
                    0,
                    0,
                    plan.Items,
                    issues);
            }

            var nativeItems = plan.Items.Where(item =>
                item.State is RestoreItemState.Ready &&
                item.RestoreLevel is RestoreLevel.NativeBestEffort);
            if (nativeItems.Any())
            {
                if (!Directory.Exists(request.CodexDestinationRoot))
                {
                    issues.Add(new RestoreIssue(
                        "RESTORE_CODEX_NOT_INITIALIZED",
                        "Codex must be installed, started, signed in, and then fully closed before native state restore.",
                        true));
                }
                else
                {
                    var usage = _codexUsageInspector.Inspect(
                        request.CodexDestinationRoot);
                    if (!usage.CanCreateNativeSnapshot)
                    {
                        issues.Add(new RestoreIssue(
                            "RESTORE_CODEX_STILL_RUNNING",
                            "Codex or its databases are still in use. Fully close Codex before restoring native state.",
                            true));
                    }
                }
            }

            if (issues.Any(issue => issue.IsBlocking))
            {
                return new RestoreResult(
                    RestoreStatus.Failed,
                    plan.BackupId,
                    null,
                    0,
                    0,
                    plan.Items,
                    issues);
            }

            Directory.CreateDirectory(request.ProjectDestinationRoot);
            Directory.CreateDirectory(request.PortableDataDestinationRoot);
            if (plan.Items.Any(item =>
                    item.State is RestoreItemState.Ready &&
                    item.Kind is not BackupDataKind.Project &&
                    item.PackageItemId != "portable-conversations-v1"))
            {
                Directory.CreateDirectory(request.CodexDestinationRoot);
            }

            var readyItems = plan.Items
                .Where(item => item.State is RestoreItemState.Ready)
                .ToArray();
            var totalEntries = readyItems.Sum(item =>
                GetManifestEntries(manifest, item.SourceRelativeRoot).Count);
            var totalBytes = readyItems.Sum(item =>
                GetManifestEntries(manifest, item.SourceRelativeRoot)
                    .Sum(entry => entry.Entry.Length));

            foreach (var item in plan.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.State is not RestoreItemState.Ready)
                {
                    completedItems.Add(item);
                    continue;
                }

                progress?.Report(new RestoreProgress(
                    RestoreStage.Restoring,
                    item.DisplayName,
                    restoredFiles,
                    totalEntries,
                    restoredBytes,
                    totalBytes));
                var itemEntries = GetManifestEntries(
                    manifest,
                    item.SourceRelativeRoot);
                var sourceRoot = GetSafePackagePath(
                    request.PackagePath,
                    item.SourceRelativeRoot);

                if (item.ConflictPolicy is RestoreConflictPolicy.ReplaceWithRollback &&
                    TargetExists(item.TargetPath, item.SourceWasDirectory))
                {
                    progress?.Report(new RestoreProgress(
                        RestoreStage.CreatingRollback,
                        item.DisplayName,
                        restoredFiles,
                        totalEntries,
                        restoredBytes,
                        totalBytes));
                    var rollbackPath = Path.Combine(
                        plan.RollbackRoot,
                        CreateSafeRollbackName(item));
                    await CopyPathAsync(
                        item.TargetPath,
                        rollbackPath,
                        item.SourceWasDirectory,
                        overwrite: false,
                        cancellationToken);
                    rollbackActions.Push(new RollbackAction(
                        item.TargetPath,
                        rollbackPath,
                        item.SourceWasDirectory,
                        RollbackActionKind.RestoreOriginal));
                }

                var itemResult = await RestoreItemAsync(
                    sourceRoot,
                    item,
                    itemEntries,
                    plan.RollbackRoot,
                    rollbackActions,
                    progress,
                    restoredFiles,
                    totalEntries,
                    restoredBytes,
                    totalBytes,
                    cancellationToken);
                restoredFiles += itemResult.RestoredFiles;
                restoredBytes += itemResult.RestoredBytes;
                issues.AddRange(itemResult.Issues);
                completedItems.Add(item with
                {
                    State = itemResult.Failed
                        ? RestoreItemState.Failed
                        : itemResult.SkippedFiles > 0
                            ? RestoreItemState.Restored
                            : RestoreItemState.Restored,
                    Reason = itemResult.SkippedFiles > 0
                        ? $"Restored with {itemResult.SkippedFiles} existing files preserved."
                        : "Restored and verified.",
                });

                if (itemResult.Failed)
                {
                    throw new RestoreFailureException(
                        "RESTORE_ITEM_FAILED",
                        $"Restore failed for {item.DisplayName}.");
                }
            }

            var status = issues.Any(issue => !issue.IsBlocking)
                ? RestoreStatus.PartialSuccess
                : RestoreStatus.Success;
            var reportPaths = await WriteRestoreReportAsync(
                request.PortableDataDestinationRoot,
                plan,
                status,
                restoredFiles,
                restoredBytes,
                completedItems,
                issues,
                cancellationToken);
            progress?.Report(new RestoreProgress(
                RestoreStage.Completed,
                null,
                restoredFiles,
                totalEntries,
                restoredBytes,
                totalBytes));
            return new RestoreResult(
                status,
                plan.BackupId,
                Directory.Exists(plan.RollbackRoot) ? plan.RollbackRoot : null,
                restoredFiles,
                restoredBytes,
                completedItems,
                issues,
                reportPaths.JsonPath,
                reportPaths.HtmlPath);
        }
        catch (OperationCanceledException)
        {
            issues.Add(new RestoreIssue(
                "RESTORE_CANCELLED",
                "Restore was cancelled. Created files will be rolled back.",
                true));
            var rolledBack = await RollbackAsync(
                rollbackActions,
                issues,
                CancellationToken.None);
            var reportPaths = plan is null
                ? null
                : await WriteRestoreReportAsync(
                    request.PortableDataDestinationRoot,
                    plan,
                    rolledBack ? RestoreStatus.Cancelled : RestoreStatus.Failed,
                    restoredFiles,
                    restoredBytes,
                    completedItems,
                    issues,
                    CancellationToken.None);
            return new RestoreResult(
                rolledBack ? RestoreStatus.Cancelled : RestoreStatus.Failed,
                plan?.BackupId ?? string.Empty,
                plan is not null && Directory.Exists(plan.RollbackRoot)
                    ? plan.RollbackRoot
                    : null,
                restoredFiles,
                restoredBytes,
                completedItems,
                issues,
                reportPaths?.JsonPath,
                reportPaths?.HtmlPath);
        }
        catch (Exception exception)
        {
            issues.Add(new RestoreIssue(
                exception is RestoreFailureException failure
                    ? failure.Code
                    : "RESTORE_UNEXPECTED_FAILURE",
                exception.Message,
                true));
            progress?.Report(new RestoreProgress(
                RestoreStage.RolledBack,
                null,
                restoredFiles,
                0,
                restoredBytes,
                0));
            var rolledBack = await RollbackAsync(
                rollbackActions,
                issues,
                CancellationToken.None);
            RestoreReportPaths? reportPaths = null;
            if (plan is not null)
            {
                reportPaths = await WriteRestoreReportAsync(
                    request.PortableDataDestinationRoot,
                    plan,
                    rolledBack ? RestoreStatus.RolledBack : RestoreStatus.Failed,
                    restoredFiles,
                    restoredBytes,
                    completedItems,
                    issues,
                    CancellationToken.None);
            }

            return new RestoreResult(
                rolledBack ? RestoreStatus.RolledBack : RestoreStatus.Failed,
                plan?.BackupId ?? string.Empty,
                plan is not null && Directory.Exists(plan.RollbackRoot)
                    ? plan.RollbackRoot
                    : null,
                restoredFiles,
                restoredBytes,
                completedItems,
                issues,
                reportPaths?.JsonPath,
                reportPaths?.HtmlPath);
        }
    }

    private static async Task<ItemRestoreResult> RestoreItemAsync(
        string sourceRoot,
        RestorePlanItem item,
        IReadOnlyList<ItemManifestEntry> manifestEntries,
        string rollbackRoot,
        Stack<RollbackAction> rollbackActions,
        IProgress<RestoreProgress>? progress,
        long completedFiles,
        long totalFiles,
        long processedBytes,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(sourceRoot))
        {
            return ItemRestoreResult.Failure(new RestoreIssue(
                "RESTORE_SOURCE_ITEM_MISSING",
                "A package item directory is missing.",
                true,
                item.PackageItemId));
        }

        return item.SourceWasDirectory
            ? await RestoreDirectoryAsync(
                sourceRoot,
                item,
                manifestEntries,
                rollbackRoot,
                rollbackActions,
                progress,
                completedFiles,
                totalFiles,
                processedBytes,
                totalBytes,
                cancellationToken)
            : await RestoreFileItemAsync(
                sourceRoot,
                item,
                manifestEntries,
                rollbackActions,
                progress,
                completedFiles,
                totalFiles,
                processedBytes,
                totalBytes,
                cancellationToken);
    }

    private static async Task<ItemRestoreResult> RestoreDirectoryAsync(
        string sourceRoot,
        RestorePlanItem item,
        IReadOnlyList<ItemManifestEntry> manifestEntries,
        string rollbackRoot,
        Stack<RollbackAction> rollbackActions,
        IProgress<RestoreProgress>? progress,
        long completedFiles,
        long totalFiles,
        long processedBytes,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        var restoredFiles = 0L;
        var restoredBytes = 0L;
        var skippedFiles = 0;
        var createdPaths = new List<string>();

        if (item.ConflictPolicy is RestoreConflictPolicy.ReplaceWithRollback &&
            Directory.Exists(item.TargetPath))
        {
            Directory.Delete(item.TargetPath, recursive: true);
        }

        if (!Directory.Exists(item.TargetPath))
        {
            Directory.CreateDirectory(item.TargetPath);
            rollbackActions.Push(new RollbackAction(
                item.TargetPath,
                null,
                IsDirectory: true,
                RollbackActionKind.DeleteCreated));
        }

        foreach (var sourceDirectory in Directory.EnumerateDirectories(
                     sourceRoot,
                     "*",
                     SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, sourceDirectory);
            var targetDirectory = GetSafeTargetPath(item.TargetPath, relative);
            if (!Directory.Exists(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
                createdPaths.Add(targetDirectory);
            }
        }

        foreach (var manifestEntry in manifestEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetFile = GetSafeTargetPath(
                item.TargetPath,
                manifestEntry.RelativeWithinItem);
            if (File.Exists(targetFile) &&
                item.ConflictPolicy is
                    RestoreConflictPolicy.MergePreserveExisting or
                    RestoreConflictPolicy.SkipExisting)
            {
                skippedFiles++;
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            await CopyAndVerifyAsync(
                GetSafePackagePath(
                    sourceRoot,
                    manifestEntry.RelativeWithinItem),
                targetFile,
                manifestEntry.Entry,
                overwrite: item.ConflictPolicy is
                    RestoreConflictPolicy.ReplaceWithRollback,
                cancellationToken);
            rollbackActions.Push(new RollbackAction(
                targetFile,
                null,
                IsDirectory: false,
                RollbackActionKind.DeleteCreated));
            restoredFiles++;
            restoredBytes += manifestEntry.Entry.Length;
            progress?.Report(new RestoreProgress(
                RestoreStage.VerifyingRestoredData,
                manifestEntry.Entry.RelativePath,
                completedFiles + restoredFiles,
                totalFiles,
                processedBytes + restoredBytes,
                totalBytes));
        }

        return new ItemRestoreResult(
            false,
            restoredFiles,
            restoredBytes,
            skippedFiles,
            []);
    }

    private static async Task<ItemRestoreResult> RestoreFileItemAsync(
        string sourceRoot,
        RestorePlanItem item,
        IReadOnlyList<ItemManifestEntry> manifestEntries,
        Stack<RollbackAction> rollbackActions,
        IProgress<RestoreProgress>? progress,
        long completedFiles,
        long totalFiles,
        long processedBytes,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        if (manifestEntries.Count != 1)
        {
            return ItemRestoreResult.Failure(new RestoreIssue(
                "RESTORE_FILE_ITEM_INVALID",
                "A single-file backup item does not contain exactly one file.",
                true,
                item.PackageItemId));
        }

        if (File.Exists(item.TargetPath) &&
            item.ConflictPolicy is
                RestoreConflictPolicy.MergePreserveExisting or
                RestoreConflictPolicy.SkipExisting)
        {
            return new ItemRestoreResult(false, 0, 0, 1, []);
        }

        if (item.ConflictPolicy is RestoreConflictPolicy.ReplaceWithRollback &&
            File.Exists(item.TargetPath))
        {
            File.Delete(item.TargetPath);
        }

        var entry = manifestEntries[0];
        Directory.CreateDirectory(Path.GetDirectoryName(item.TargetPath)!);
        await CopyAndVerifyAsync(
            GetSafePackagePath(sourceRoot, entry.RelativeWithinItem),
            item.TargetPath,
            entry.Entry,
            overwrite: item.ConflictPolicy is
                RestoreConflictPolicy.ReplaceWithRollback,
            cancellationToken);
        rollbackActions.Push(new RollbackAction(
            item.TargetPath,
            null,
            IsDirectory: false,
            RollbackActionKind.DeleteCreated));
        progress?.Report(new RestoreProgress(
            RestoreStage.VerifyingRestoredData,
            entry.Entry.RelativePath,
            completedFiles + 1,
            totalFiles,
            processedBytes + entry.Entry.Length,
            totalBytes));
        return new ItemRestoreResult(
            false,
            1,
            entry.Entry.Length,
            0,
            []);
    }

    private static async Task CopyAndVerifyAsync(
        string sourcePath,
        string targetPath,
        BackupManifestEntry expected,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            await using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var target = new FileStream(
                targetPath,
                overwrite ? FileMode.Create : FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = await source.ReadAsync(
                    buffer.AsMemory(0, buffer.Length),
                    CancellationToken.None);
                if (count == 0)
                {
                    break;
                }

                await target.WriteAsync(
                    buffer.AsMemory(0, count),
                    CancellationToken.None);
            }

            await target.FlushAsync(CancellationToken.None);
            target.Flush(flushToDisk: true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        File.SetLastWriteTimeUtc(targetPath, expected.LastWriteTimeUtc.UtcDateTime);
        var fileInfo = new FileInfo(targetPath);
        if (fileInfo.Length != expected.Length)
        {
            throw new IOException("Restored file length does not match the backup manifest.");
        }

        await using var verifyStream = new FileStream(
            targetPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexString(
            await SHA256.HashDataAsync(verifyStream, cancellationToken))
            .ToLowerInvariant();
        if (!string.Equals(hash, expected.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Restored file hash does not match the backup manifest.");
        }
    }

    private static async Task CopyPathAsync(
        string sourcePath,
        string targetPath,
        bool isDirectory,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        if (!isDirectory)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await using var source = File.OpenRead(sourcePath);
            await using var target = new FileStream(
                targetPath,
                overwrite ? FileMode.Create : FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(target, cancellationToken);
            await target.FlushAsync(cancellationToken);
            return;
        }

        Directory.CreateDirectory(targetPath);
        foreach (var directory in Directory.EnumerateDirectories(
                     sourcePath,
                     "*",
                     SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(
                targetPath,
                Path.GetRelativePath(sourcePath, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(
                     sourcePath,
                     "*",
                     SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetFile = Path.Combine(
                targetPath,
                Path.GetRelativePath(sourcePath, file));
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            await using var source = File.OpenRead(file);
            await using var target = new FileStream(
                targetFile,
                overwrite ? FileMode.Create : FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(target, cancellationToken);
        }
    }

    private static async Task<bool> RollbackAsync(
        Stack<RollbackAction> actions,
        ICollection<RestoreIssue> issues,
        CancellationToken cancellationToken)
    {
        var succeeded = true;
        while (actions.TryPop(out var action))
        {
            try
            {
                switch (action.Kind)
                {
                    case RollbackActionKind.DeleteCreated:
                        DeletePathIfExists(action.TargetPath, action.IsDirectory);
                        break;
                    case RollbackActionKind.RestoreOriginal:
                        DeletePathIfExists(action.TargetPath, action.IsDirectory);
                        if (action.RollbackPath is null)
                        {
                            throw new IOException("Rollback source path is missing.");
                        }

                        await CopyPathAsync(
                            action.RollbackPath,
                            action.TargetPath,
                            action.IsDirectory,
                            overwrite: false,
                            cancellationToken);
                        break;
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                succeeded = false;
                issues.Add(new RestoreIssue(
                    "RESTORE_ROLLBACK_FAILED",
                    exception.Message,
                    true));
            }
        }

        return succeeded;
    }

    private async Task<RestoreReportPaths> WriteRestoreReportAsync(
        string reportRoot,
        RestorePlan plan,
        RestoreStatus status,
        long restoredFiles,
        long restoredBytes,
        IReadOnlyList<RestorePlanItem> items,
        IReadOnlyList<RestoreIssue> issues,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(reportRoot);
        var timestamp = _timeProvider.GetUtcNow();
        var report = new
        {
            reportVersion = "1.0",
            plan.BackupId,
            status,
            completedAtUtc = timestamp,
            restoredFiles,
            restoredBytes,
            rollbackPath = Directory.Exists(plan.RollbackRoot)
                ? plan.RollbackRoot
                : null,
            items,
            issues,
        };
        var baseName = $"Codex恢复报告_{timestamp:yyyyMMdd_HHmmss}";
        var jsonPath = Path.Combine(reportRoot, $"{baseName}.json");
        var htmlPath = Path.Combine(reportRoot, $"{baseName}.html");
        await File.WriteAllTextAsync(
            jsonPath,
            JsonSerializer.Serialize(report, ReportOptions),
            new UTF8Encoding(false),
            cancellationToken);

        var restoredItemCount = items.Count(item => item.State is RestoreItemState.Restored);
        var skippedItemCount = items.Count(item =>
            item.State is RestoreItemState.SkippedByUser or
                RestoreItemState.SkippedExisting or
                RestoreItemState.SkippedIncompatible);
        var failedItemCount = items.Count(item =>
            item.State is RestoreItemState.Failed or RestoreItemState.RolledBack);
        var rollbackPath = Directory.Exists(plan.RollbackRoot)
            ? plan.RollbackRoot
            : null;
        var issueHtml = issues.Count == 0
            ? "<p>没有问题。</p>"
            : $"""
               <table>
               <thead><tr><th>代码</th><th>级别</th><th>说明</th><th>备份项</th></tr></thead>
               <tbody>
               {string.Join(string.Empty, issues.Select(RenderIssueRow))}
               </tbody>
               </table>
               """;
        var itemHtml = items.Count == 0
            ? "<p>没有恢复项。</p>"
            : $"""
               <table>
               <thead><tr><th>名称</th><th>类型</th><th>状态</th><th>目标位置</th><th>冲突策略</th><th>说明</th></tr></thead>
               <tbody>
               {string.Join(string.Empty, items.Select(RenderItemRow))}
               </tbody>
               </table>
               """;
        var html = $$"""
                    <!doctype html>
                    <html lang="zh-CN">
                    <head>
                    <meta charset="utf-8">
                    <title>Codex 恢复报告</title>
                    <style>
                    body { font-family: "Segoe UI", "Microsoft YaHei", sans-serif; margin: 32px; color: #17202A; }
                    h1, h2 { color: #1B4F72; }
                    table { border-collapse: collapse; width: 100%; margin: 12px 0 24px; }
                    th, td { border: 1px solid #D5DBDB; padding: 8px 10px; text-align: left; vertical-align: top; }
                    th { background: #EBF5FB; }
                    code { background: #F4F6F7; padding: 2px 4px; border-radius: 4px; }
                    .summary { background: #E8F6F3; border: 1px solid #A3E4D7; padding: 14px 18px; border-radius: 8px; }
                    .warning { background: #FEF9E7; border: 1px solid #F7DC6F; padding: 12px 16px; border-radius: 8px; }
                    </style>
                    </head>
                    <body>
                    <h1>Codex 恢复报告</h1>
                    <div class="summary">
                    <p><strong>状态：</strong>{{Html(status.ToString())}}</p>
                    <p><strong>备份 ID：</strong><code>{{Html(plan.BackupId)}}</code></p>
                    <p><strong>备份包：</strong>{{Html(plan.PackagePath)}}</p>
                    <p><strong>完成时间：</strong>{{Html(timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"))}}</p>
                    <p><strong>恢复文件：</strong>{{restoredFiles:N0}}，<strong>恢复体量：</strong>{{restoredBytes:N0}} 字节</p>
                    <p><strong>恢复项：</strong>{{restoredItemCount:N0}}，<strong>跳过项：</strong>{{skippedItemCount:N0}}，<strong>失败/回滚项：</strong>{{failedItemCount:N0}}</p>
                    <p><strong>回滚目录：</strong>{{Html(rollbackPath ?? "未创建")}}</p>
                    </div>
                    <div class="warning">
                    本报告不包含旧电脑登录令牌、浏览器会话、机器标识或 sandbox secrets。项目默认另存恢复，Codex 配置默认保留新电脑已有文件。
                    </div>
                    <h2>恢复项</h2>
                    {{itemHtml}}
                    <h2>问题</h2>
                    {{issueHtml}}
                    </body>
                    </html>
                    """;
        await File.WriteAllTextAsync(
            htmlPath,
            html,
            new UTF8Encoding(false),
            cancellationToken);
        return new RestoreReportPaths(jsonPath, htmlPath);
    }

    private static IReadOnlyList<ItemManifestEntry> GetManifestEntries(
        BackupManifest manifest,
        string itemRoot)
    {
        var prefix = itemRoot.Replace('\\', '/').TrimEnd('/') + "/";
        return manifest.Entries
            .Where(entry => entry.RelativePath.StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase))
            .Select(entry => new ItemManifestEntry(
                entry,
                entry.RelativePath[prefix.Length..]))
            .ToArray();
    }

    private static string GetSafePackagePath(string root, string relativePath)
    {
        var normalizedRoot = Path.GetFullPath(root);
        var targetPath = Path.GetFullPath(Path.Combine(
            normalizedRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        EnsureWithin(normalizedRoot, targetPath);
        return targetPath;
    }

    private static string GetSafeTargetPath(string root, string relativePath)
    {
        var normalizedRoot = Path.GetFullPath(root);
        var targetPath = Path.GetFullPath(Path.Combine(
            normalizedRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        EnsureWithin(normalizedRoot, targetPath);
        return targetPath;
    }

    private static void EnsureWithin(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        if (relative == ".." ||
            Path.IsPathFullyQualified(relative) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new IOException("A restore path escaped its selected destination root.");
        }
    }

    private static bool TargetExists(string path, bool isDirectory) =>
        isDirectory ? Directory.Exists(path) : File.Exists(path);

    private static void DeletePathIfExists(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string CreateSafeRollbackName(RestorePlanItem item)
    {
        var safe = new string(item.PackageItemId
            .Where(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            .Take(48)
            .ToArray());
        return safe.Length == 0 ? Guid.NewGuid().ToString("N") : safe;
    }

    private static string RenderItemRow(RestorePlanItem item) => $"""
        <tr>
        <td>{Html(item.DisplayName)}</td>
        <td>{Html(FormatDataKind(item.Kind, item.PackageItemId))}</td>
        <td>{Html(FormatRestoreItemState(item.State))}</td>
        <td>{Html(string.IsNullOrWhiteSpace(item.TargetPath) ? "未写入" : item.TargetPath)}</td>
        <td>{Html(FormatConflictPolicy(item.ConflictPolicy))}</td>
        <td>{Html(item.Reason)}</td>
        </tr>
        """;

    private static string RenderIssueRow(RestoreIssue issue) => $"""
        <tr>
        <td><code>{Html(issue.Code)}</code></td>
        <td>{Html(issue.IsBlocking ? "阻止恢复" : "提示")}</td>
        <td>{Html(issue.Message)}</td>
        <td>{Html(issue.ItemId ?? string.Empty)}</td>
        </tr>
        """;

    private static string FormatDataKind(BackupDataKind kind, string packageItemId) =>
        packageItemId.Equals(
            "portable-conversations-v1",
            StringComparison.OrdinalIgnoreCase)
            ? "通用对话"
            : kind switch
            {
                BackupDataKind.Project => "项目",
                BackupDataKind.CodexSession => "Codex 会话/状态",
                BackupDataKind.CodexState => "Codex 状态",
                BackupDataKind.Configuration => "配置",
                BackupDataKind.Skill => "技能",
                BackupDataKind.Plugin => "插件",
                BackupDataKind.GeneratedAsset => "生成工具",
                BackupDataKind.EnvironmentInventory => "环境清单",
                _ => kind.ToString(),
            };

    private static string FormatRestoreItemState(RestoreItemState state) => state switch
    {
        RestoreItemState.Ready => "准备恢复",
        RestoreItemState.SkippedByUser => "用户跳过",
        RestoreItemState.SkippedIncompatible => "不兼容跳过",
        RestoreItemState.SkippedExisting => "已有文件保留",
        RestoreItemState.Restored => "已恢复",
        RestoreItemState.Failed => "失败",
        RestoreItemState.RolledBack => "已回滚",
        _ => state.ToString(),
    };

    private static string FormatConflictPolicy(RestoreConflictPolicy policy) => policy switch
    {
        RestoreConflictPolicy.KeepBoth => "同名另存",
        RestoreConflictPolicy.SkipExisting => "保留已有",
        RestoreConflictPolicy.MergePreserveExisting => "合并并保留已有",
        RestoreConflictPolicy.ReplaceWithRollback => "替换并建立回滚",
        _ => policy.ToString(),
    };

    private static string Html(string value) => WebUtility.HtmlEncode(value);

    private sealed record ItemManifestEntry(
        BackupManifestEntry Entry,
        string RelativeWithinItem);

    private sealed record ItemRestoreResult(
        bool Failed,
        long RestoredFiles,
        long RestoredBytes,
        int SkippedFiles,
        IReadOnlyList<RestoreIssue> Issues)
    {
        public static ItemRestoreResult Failure(RestoreIssue issue) =>
            new(true, 0, 0, 0, [issue]);
    }

    private sealed record RollbackAction(
        string TargetPath,
        string? RollbackPath,
        bool IsDirectory,
        RollbackActionKind Kind);

    private sealed record RestoreReportPaths(
        string JsonPath,
        string HtmlPath);

    private enum RollbackActionKind
    {
        DeleteCreated,
        RestoreOriginal,
    }

    private sealed class RestoreFailureException(
        string code,
        string message) : Exception(message)
    {
        public string Code { get; } = code;
    }
}
