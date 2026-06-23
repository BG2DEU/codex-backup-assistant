using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexBackup.Core.Backup;
using CodexBackup.Core.Export;
using CodexBackup.Core.Manifest;

namespace CodexBackup.Infrastructure.Windows.Export;

public sealed class BackupExportEngine(
    IDestinationVolumeInfoProvider? volumeInfoProvider = null,
    TimeProvider? timeProvider = null,
    IEnumerable<IBackupPackageContributor>? contributors = null,
    string? codexAdapterVersion = null,
    string? sourceCodexVersion = null)
{
    private const int CopyBufferSize = 1024 * 1024;
    private const long Fat32MaximumFileBytes = uint.MaxValue;
    private const string IncompleteMarkerFileName = "INCOMPLETE.json";
    private const string ManifestFileName = "manifest.json";
    private const string PackageIndexFileName = "package-index.json";
    private const string JsonReportFileName = "export-report.json";
    private const string HtmlReportFileName = "export-report.html";

    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly IDestinationVolumeInfoProvider _volumeInfoProvider =
        volumeInfoProvider ?? new WindowsDestinationVolumeInfoProvider();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IReadOnlyList<IBackupPackageContributor> _contributors =
        contributors?.ToArray() ?? [];
    private readonly string? _codexAdapterVersion = codexAdapterVersion;
    private readonly string? _sourceCodexVersion = sourceCodexVersion;

    public async Task<BackupExportResult> ExportAsync(
        BackupPlan plan,
        string destinationRoot,
        string producerVersion,
        IProgress<BackupExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(producerVersion);

        var backupId = Guid.NewGuid().ToString("N");
        string? incompletePath = null;
        long copiedFiles = 0;
        long copiedBytes = 0;
        var issues = new List<BackupExportIssue>();
        var startedAt = _timeProvider.GetUtcNow();

        try
        {
            if (!plan.CanExport)
            {
                throw new ExportFailureException(
                    "planning",
                    "PLAN_NOT_EXPORTABLE",
                    "The backup plan contains blocking issues.");
            }

            var normalizedDestination = Path.GetFullPath(destinationRoot);
            EnsureDestinationIsOutsideSources(plan, normalizedDestination);
            Directory.CreateDirectory(normalizedDestination);

            progress?.Report(new BackupExportProgress(
                BackupExportStage.Planning,
                null,
                0,
                0,
                0,
                0));

            var snapshot = InspectSources(plan, cancellationToken);
            var estimatedGeneratedBytes = _contributors.Sum(contributor =>
                contributor.EstimateAdditionalBytes(plan));
            ValidateDestinationCapacity(
                normalizedDestination,
                snapshot,
                estimatedGeneratedBytes);

            var packageName = $"CodexBackup_{startedAt:yyyyMMdd_HHmmss}_{backupId[..8]}";
            var completedPath = Path.Combine(normalizedDestination, packageName);
            incompletePath = Path.Combine(normalizedDestination, $".{packageName}.incomplete");
            if (Directory.Exists(completedPath) || Directory.Exists(incompletePath))
            {
                throw new ExportFailureException(
                    "planning",
                    "DESTINATION_PACKAGE_EXISTS",
                    "A package with the generated name already exists.");
            }

            Directory.CreateDirectory(incompletePath);
            await WriteIncompleteMarkerAsync(
                incompletePath,
                backupId,
                BackupExportStage.Copying,
                startedAt,
                cancellationToken);

            var manifestEntries = new List<BackupManifestEntry>();
            var packageItems = new List<BackupPackageItem>();
            var stopwatch = Stopwatch.StartNew();

            foreach (var item in plan.Items.Where(item => item.State is BackupPlanItemState.Included))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var itemRelativeRoot = $"payload/{GetKindDirectory(item.Kind)}/{GetSafeItemDirectoryName(item)}";
                Directory.CreateDirectory(GetSafePackagePath(incompletePath, itemRelativeRoot));
                packageItems.Add(new BackupPackageItem(
                    item.Id,
                    item.DisplayName,
                    item.SourcePath,
                    itemRelativeRoot,
                    item.Kind,
                    item.RestoreLevel,
                    item.DiscoverySources,
                    Directory.Exists(item.SourcePath)));

                foreach (var sourceFile in EnumerateSourceFiles(
                             item,
                             relativeDirectory => Directory.CreateDirectory(GetSafePackagePath(
                                 incompletePath,
                                 $"{itemRelativeRoot}/{ToManifestPath(relativeDirectory)}"))))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var packageRelativePath = $"{itemRelativeRoot}/{ToManifestPath(sourceFile.RelativePath)}";
                    var targetPath = GetSafePackagePath(incompletePath, packageRelativePath);

                    progress?.Report(new BackupExportProgress(
                        BackupExportStage.Copying,
                        packageRelativePath,
                        copiedFiles,
                        snapshot.FileCount,
                        copiedBytes,
                        snapshot.TotalBytes));

                    var copyResult = await CopyFileAsync(
                        sourceFile.FullPath,
                        targetPath,
                        packageRelativePath,
                        cancellationToken);
                    copiedFiles++;
                    copiedBytes += copyResult.Length;
                    manifestEntries.Add(new BackupManifestEntry(
                        packageRelativePath,
                        copyResult.Length,
                        copyResult.Sha256,
                        item.Kind,
                        item.RestoreLevel,
                        copyResult.LastWriteTimeUtc));

                }
            }

            if (copiedFiles != snapshot.FileCount || copiedBytes != snapshot.TotalBytes)
            {
                throw new ExportFailureException(
                    "copying",
                    "SOURCE_TREE_CHANGED",
                    "The source tree changed after export planning. Run the scan again.");
            }

            foreach (var contributor in _contributors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new BackupExportProgress(
                    BackupExportStage.Transforming,
                    contributor.GetType().Name,
                    copiedFiles,
                    copiedFiles,
                    copiedBytes,
                    copiedBytes));
                try
                {
                    var contribution = await contributor.ContributeAsync(
                        new BackupContributionContext(
                            plan,
                            incompletePath,
                            backupId,
                            startedAt,
                            packageItems),
                        cancellationToken);
                    foreach (var generatedFile in contribution.Files)
                    {
                        var generatedEntry = await InspectGeneratedFileAsync(
                            incompletePath,
                            generatedFile,
                            cancellationToken);
                        manifestEntries.Add(generatedEntry);
                        copiedFiles++;
                        copiedBytes += generatedEntry.Length;
                    }

                    packageItems.AddRange(contribution.Items);
                    issues.AddRange(contribution.Issues);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or JsonException)
                {
                    issues.Add(new BackupExportIssue(
                        "transforming",
                        "PORTABLE_CONVERSATION_EXPORT_FAILED",
                        exception.Message,
                        IsRetryable: true));
                }
            }

            progress?.Report(new BackupExportProgress(
                BackupExportStage.Committing,
                null,
                copiedFiles,
                snapshot.FileCount,
                copiedBytes,
                snapshot.TotalBytes));

            var manifest = new BackupManifest(
                BackupManifest.CurrentFormatVersion,
                backupId,
                startedAt,
                producerVersion,
                manifestEntries.OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray());
            var manifestIssues = BackupManifestValidator.Validate(manifest);
            if (manifestIssues.Count > 0)
            {
                throw new ExportFailureException(
                    "committing",
                    "MANIFEST_INVALID",
                    manifestIssues[0].Message,
                    manifestIssues[0].RelativePath);
            }

            var packageIndex = new BackupPackageIndex(
                BackupPackageIndex.CurrentFormatVersion,
                backupId,
                plan.PlanId,
                startedAt,
                producerVersion,
                packageItems,
                _codexAdapterVersion,
                _sourceCodexVersion);
            var indexIssues = BackupPackageIndexValidator.Validate(packageIndex);
            if (indexIssues.Count > 0)
            {
                throw new ExportFailureException(
                    "committing",
                    "PACKAGE_INDEX_INVALID",
                    indexIssues[0].Message,
                    indexIssues[0].RelativePath);
            }
            await WriteUtf8Async(
                Path.Combine(incompletePath, ManifestFileName),
                BackupManifestJson.Serialize(manifest),
                CancellationToken.None);
            await WriteUtf8Async(
                Path.Combine(incompletePath, PackageIndexFileName),
                BackupPackageIndexJson.Serialize(packageIndex),
                CancellationToken.None);

            await VerifyCopiedFilesAsync(
                incompletePath,
                manifest.Entries,
                progress,
                cancellationToken);

            stopwatch.Stop();
            var completedAt = _timeProvider.GetUtcNow();
            var completedStatus = issues.Count == 0
                ? BackupExportStatus.Success
                : BackupExportStatus.PartialSuccess;
            await WriteReportsAsync(
                incompletePath,
                backupId,
                completedStatus,
                startedAt,
                completedAt,
                copiedFiles,
                copiedBytes,
                stopwatch.Elapsed,
                issues,
                CancellationToken.None);

            File.Delete(Path.Combine(incompletePath, IncompleteMarkerFileName));
            Directory.Move(incompletePath, completedPath);
            incompletePath = null;

            progress?.Report(new BackupExportProgress(
                BackupExportStage.Completed,
                null,
                copiedFiles,
                snapshot.FileCount,
                copiedBytes,
                snapshot.TotalBytes));

            return new BackupExportResult(
                backupId,
                completedStatus,
                completedPath,
                null,
                copiedFiles,
                copiedBytes,
                issues);
        }
        catch (OperationCanceledException)
        {
            issues.Add(new BackupExportIssue(
                "copying",
                "EXPORT_CANCELLED",
                "The export was cancelled by the user.",
                IsRetryable: true));
            await PreserveIncompleteStateAsync(
                incompletePath,
                backupId,
                BackupExportStatus.Cancelled,
                startedAt,
                copiedFiles,
                copiedBytes,
                issues);
            progress?.Report(new BackupExportProgress(
                BackupExportStage.Cancelled,
                null,
                copiedFiles,
                0,
                copiedBytes,
                0));
            return new BackupExportResult(
                backupId,
                BackupExportStatus.Cancelled,
                null,
                incompletePath,
                copiedFiles,
                copiedBytes,
                issues);
        }
        catch (Exception exception)
        {
            issues.Add(MapIssue(exception));
            await PreserveIncompleteStateAsync(
                incompletePath,
                backupId,
                BackupExportStatus.Failed,
                startedAt,
                copiedFiles,
                copiedBytes,
                issues);
            progress?.Report(new BackupExportProgress(
                BackupExportStage.Failed,
                null,
                copiedFiles,
                0,
                copiedBytes,
                0));
            return new BackupExportResult(
                backupId,
                BackupExportStatus.Failed,
                null,
                incompletePath,
                copiedFiles,
                copiedBytes,
                issues);
        }
    }

    private SourceSnapshot InspectSources(BackupPlan plan, CancellationToken cancellationToken)
    {
        long fileCount = 0;
        long totalBytes = 0;
        long largestFileBytes = 0;

        foreach (var item in plan.Items.Where(item => item.State is BackupPlanItemState.Included))
        {
            foreach (var sourceFile in EnumerateSourceFiles(item))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var length = new FileInfo(sourceFile.FullPath).Length;
                fileCount++;
                totalBytes = checked(totalBytes + length);
                largestFileBytes = Math.Max(largestFileBytes, length);
            }
        }

        return new SourceSnapshot(fileCount, totalBytes, largestFileBytes);
    }

    private void ValidateDestinationCapacity(
        string destinationRoot,
        SourceSnapshot snapshot,
        long estimatedGeneratedBytes)
    {
        var volume = _volumeInfoProvider.GetInfo(destinationRoot);
        var estimatedTotalBytes = checked(snapshot.TotalBytes + estimatedGeneratedBytes);
        var safetyMargin = Math.Max(64L * 1024 * 1024, estimatedTotalBytes / 20);
        var requiredBytes = checked(estimatedTotalBytes + safetyMargin);
        if (volume.AvailableFreeBytes < requiredBytes)
        {
            throw new ExportFailureException(
                "planning",
                "DESTINATION_SPACE_INSUFFICIENT",
                $"Destination requires at least {requiredBytes} bytes including safety margin, but only {volume.AvailableFreeBytes} bytes are available.");
        }

        if (string.Equals(volume.FileSystem, "FAT32", StringComparison.OrdinalIgnoreCase) &&
            snapshot.LargestFileBytes > Fat32MaximumFileBytes)
        {
            throw new ExportFailureException(
                "planning",
                "DESTINATION_FILE_TOO_LARGE",
                "The destination uses FAT32 and cannot store at least one selected file.");
        }
    }

    private static IEnumerable<SourceFile> EnumerateSourceFiles(
        BackupPlanItem item,
        Action<string>? directoryDiscovered = null)
    {
        if (File.Exists(item.SourcePath))
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(item.SourcePath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new ExportFailureException(
                    "planning",
                    "SOURCE_METADATA_FAILED",
                    exception.Message,
                    item.DisplayName,
                    true,
                    exception);
            }

            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new ExportFailureException(
                    "planning",
                    "SOURCE_REPARSE_POINT_UNSUPPORTED",
                    "A symbolic link requires an explicit export policy.",
                    item.DisplayName);
            }

            yield return new SourceFile(item.SourcePath, Path.GetFileName(item.SourcePath));
            yield break;
        }

        if (!Directory.Exists(item.SourcePath))
        {
            throw new ExportFailureException(
                "planning",
                "SOURCE_ITEM_MISSING",
                "A selected source file or directory no longer exists.",
                item.DisplayName);
        }

        var pendingDirectories = new Queue<string>();
        pendingDirectories.Enqueue(item.SourcePath);
        while (pendingDirectories.TryDequeue(out var currentDirectory))
        {
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(currentDirectory);
                Array.Sort(entries, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new ExportFailureException(
                    "planning",
                    "SOURCE_ENUMERATION_FAILED",
                    exception.Message,
                    Path.GetRelativePath(item.SourcePath, currentDirectory),
                    true,
                    exception);
            }

            foreach (var entry in entries)
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new ExportFailureException(
                        "planning",
                        "SOURCE_METADATA_FAILED",
                        exception.Message,
                        Path.GetRelativePath(item.SourcePath, entry),
                        true,
                        exception);
                }

                var relativePath = Path.GetRelativePath(item.SourcePath, entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new ExportFailureException(
                        "planning",
                        "SOURCE_REPARSE_POINT_UNSUPPORTED",
                        "A symbolic link or directory junction requires an explicit export policy.",
                        relativePath);
                }

                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    directoryDiscovered?.Invoke(relativePath);
                    pendingDirectories.Enqueue(entry);
                }
                else
                {
                    yield return new SourceFile(entry, relativePath);
                }
            }
        }
    }

    private static async Task<CopiedFile> CopyFileAsync(
        string sourcePath,
        string targetPath,
        string packageRelativePath,
        CancellationToken cancellationToken)
    {
        var sourceInfo = new FileInfo(sourcePath);
        var originalLength = sourceInfo.Length;
        var originalLastWriteTimeUtc = sourceInfo.LastWriteTimeUtc;
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        byte[] sourceHash;
        long copiedLength = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var target = new FileStream(
                targetPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), CancellationToken.None);
                if (bytesRead == 0)
                {
                    break;
                }

                incrementalHash.AppendData(buffer, 0, bytesRead);
                await target.WriteAsync(buffer.AsMemory(0, bytesRead), CancellationToken.None);
                copiedLength += bytesRead;
            }

            await target.FlushAsync(CancellationToken.None);
            target.Flush(flushToDisk: true);
            sourceHash = incrementalHash.GetHashAndReset();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ExportFailureException(
                "copying",
                "FILE_COPY_FAILED",
                exception.Message,
                packageRelativePath,
                true,
                exception);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        sourceInfo.Refresh();
        if (!sourceInfo.Exists ||
            sourceInfo.Length != originalLength ||
            sourceInfo.LastWriteTimeUtc != originalLastWriteTimeUtc ||
            copiedLength != originalLength)
        {
            throw new ExportFailureException(
                "copying",
                "SOURCE_FILE_CHANGED",
                "A source file changed while it was being copied.",
                packageRelativePath,
                true);
        }

        File.SetLastWriteTimeUtc(targetPath, originalLastWriteTimeUtc);
        return new CopiedFile(
            copiedLength,
            Convert.ToHexString(sourceHash).ToLowerInvariant(),
            new DateTimeOffset(originalLastWriteTimeUtc, TimeSpan.Zero));
    }

    private static async Task VerifyCopiedFilesAsync(
        string packageRoot,
        IReadOnlyList<BackupManifestEntry> entries,
        IProgress<BackupExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var totalBytes = entries.Sum(entry => entry.Length);
        long verifiedFiles = 0;
        long verifiedBytes = 0;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new BackupExportProgress(
                BackupExportStage.Verifying,
                entry.RelativePath,
                verifiedFiles,
                entries.Count,
                verifiedBytes,
                totalBytes));

            var targetPath = GetSafePackagePath(packageRoot, entry.RelativePath);
            var targetInfo = new FileInfo(targetPath);
            if (!targetInfo.Exists)
            {
                throw new ExportFailureException(
                    "verifying",
                    "TARGET_FILE_MISSING",
                    "A copied target file is missing.",
                    entry.RelativePath,
                    true);
            }

            if (targetInfo.Length != entry.Length)
            {
                throw new ExportFailureException(
                    "verifying",
                    "TARGET_LENGTH_MISMATCH",
                    "A copied target file length differs from the source.",
                    entry.RelativePath,
                    true);
            }

            byte[] targetHash;
            try
            {
                await using var targetRead = new FileStream(
                    targetPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    CopyBufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                targetHash = await SHA256.HashDataAsync(targetRead, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new ExportFailureException(
                    "verifying",
                    "TARGET_READ_FAILED",
                    exception.Message,
                    entry.RelativePath,
                    true,
                    exception);
            }

            var expectedHash = Convert.FromHexString(entry.Sha256);
            if (!CryptographicOperations.FixedTimeEquals(expectedHash, targetHash))
            {
                throw new ExportFailureException(
                    "verifying",
                    "TARGET_HASH_MISMATCH",
                    "A copied target file hash differs from the source.",
                    entry.RelativePath,
                    true);
            }

            verifiedFiles++;
            verifiedBytes += entry.Length;
        }
    }

    private static async Task<BackupManifestEntry> InspectGeneratedFileAsync(
        string packageRoot,
        GeneratedPackageFile generatedFile,
        CancellationToken cancellationToken)
    {
        if (!BackupPathRules.IsSafeRelativePath(generatedFile.RelativePath))
        {
            throw new IOException("A generated package file path is unsafe.");
        }

        var targetPath = GetSafePackagePath(packageRoot, generatedFile.RelativePath);
        var fileInfo = new FileInfo(targetPath);
        if (!fileInfo.Exists)
        {
            throw new IOException("A generated package file is missing.");
        }

        await using var stream = new FileStream(
            targetPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return new BackupManifestEntry(
            generatedFile.RelativePath,
            fileInfo.Length,
            Convert.ToHexString(hash).ToLowerInvariant(),
            generatedFile.Kind,
            generatedFile.RestoreLevel,
            new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero));
    }

    private static void EnsureDestinationIsOutsideSources(BackupPlan plan, string destinationRoot)
    {
        foreach (var item in plan.Items.Where(item => item.State is BackupPlanItemState.Included))
        {
            if (IsSameOrDescendant(item.SourcePath, destinationRoot) ||
                IsSameOrDescendant(destinationRoot, item.SourcePath))
            {
                throw new ExportFailureException(
                    "planning",
                    "DESTINATION_OVERLAPS_SOURCE",
                    "The destination and a selected source directory overlap.",
                    item.DisplayName);
            }
        }
    }

    private static bool IsSameOrDescendant(string parentPath, string candidatePath)
    {
        var relativePath = Path.GetRelativePath(Path.GetFullPath(parentPath), Path.GetFullPath(candidatePath));
        return relativePath == "." ||
               (!Path.IsPathFullyQualified(relativePath) &&
                relativePath != ".." &&
                !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static string GetSafeItemDirectoryName(BackupPlanItem item)
    {
        var safeId = new string(item.Id
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            .Take(48)
            .ToArray());
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item.Id)))[..12]
            .ToLowerInvariant();
        return safeId.Length > 0 ? $"{safeId}-{hash}" : hash;
    }

    private static string GetKindDirectory(BackupDataKind kind) => kind switch
    {
        BackupDataKind.Project => "projects",
        BackupDataKind.CodexSession => "codex-sessions",
        BackupDataKind.CodexState => "codex-state",
        BackupDataKind.Configuration => "configuration",
        BackupDataKind.Skill => "skills",
        BackupDataKind.Plugin => "plugins",
        BackupDataKind.GeneratedAsset => "generated-assets",
        BackupDataKind.EnvironmentInventory => "environment",
        _ => "other",
    };

    private static string ToManifestPath(string path) => path.Replace('\\', '/');

    private static string GetSafePackagePath(string packageRoot, string relativePath)
    {
        var normalizedRoot = Path.GetFullPath(packageRoot);
        var targetPath = Path.GetFullPath(Path.Combine(
            normalizedRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsSameOrDescendant(normalizedRoot, targetPath))
        {
            throw new ExportFailureException(
                "planning",
                "PACKAGE_PATH_ESCAPE",
                "A generated package path escaped the package root.",
                relativePath);
        }

        return targetPath;
    }

    private static async Task WriteIncompleteMarkerAsync(
        string incompletePath,
        string backupId,
        BackupExportStage stage,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var marker = JsonSerializer.Serialize(new
        {
            backupId,
            status = "incomplete",
            stage,
            startedAtUtc = startedAt,
        }, ReportJsonOptions);
        await WriteUtf8Async(
            Path.Combine(incompletePath, IncompleteMarkerFileName),
            marker,
            cancellationToken);
    }

    private async Task PreserveIncompleteStateAsync(
        string? incompletePath,
        string backupId,
        BackupExportStatus status,
        DateTimeOffset startedAt,
        long copiedFiles,
        long copiedBytes,
        IReadOnlyList<BackupExportIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(incompletePath) || !Directory.Exists(incompletePath))
        {
            return;
        }

        try
        {
            await WriteIncompleteMarkerAsync(
                incompletePath,
                backupId,
                status is BackupExportStatus.Cancelled
                    ? BackupExportStage.Cancelled
                    : BackupExportStage.Failed,
                startedAt,
                CancellationToken.None);
            await WriteReportsAsync(
                incompletePath,
                backupId,
                status,
                startedAt,
                _timeProvider.GetUtcNow(),
                copiedFiles,
                copiedBytes,
                TimeSpan.Zero,
                issues,
                CancellationToken.None);
        }
        catch
        {
            // The target may have been removed. The .incomplete directory name still prevents success classification.
        }
    }

    private static async Task WriteReportsAsync(
        string packagePath,
        string backupId,
        BackupExportStatus status,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        long fileCount,
        long bytes,
        TimeSpan elapsed,
        IReadOnlyList<BackupExportIssue> issues,
        CancellationToken cancellationToken)
    {
        var report = new
        {
            reportVersion = "1.0",
            backupId,
            status,
            startedAtUtc = startedAt,
            completedAtUtc = completedAt,
            fileCount,
            bytes,
            elapsedMilliseconds = (long)elapsed.TotalMilliseconds,
            issues,
        };
        await WriteUtf8Async(
            Path.Combine(packagePath, JsonReportFileName),
            JsonSerializer.Serialize(report, ReportJsonOptions),
            cancellationToken);

        var issueHtml = issues.Count == 0
            ? "<p>没有错误。</p>"
            : $"<ul>{string.Join(string.Empty, issues.Select(issue =>
                $"<li><strong>{WebUtility.HtmlEncode(issue.Code)}</strong>：{WebUtility.HtmlEncode(issue.Message)}</li>"))}</ul>";
        var html = $"""
                    <!doctype html>
                    <html lang="zh-CN">
                    <head><meta charset="utf-8"><title>Codex 备份导出报告</title></head>
                    <body>
                    <h1>Codex 备份导出报告</h1>
                    <p>状态：{WebUtility.HtmlEncode(status.ToString())}</p>
                    <p>文件：{fileCount:N0}，字节：{bytes:N0}</p>
                    <h2>问题</h2>
                    {issueHtml}
                    </body>
                    </html>
                    """;
        await WriteUtf8Async(
            Path.Combine(packagePath, HtmlReportFileName),
            html,
            cancellationToken);
    }

    private static Task WriteUtf8Async(string path, string content, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(path, content, new UTF8Encoding(false), cancellationToken);

    private static BackupExportIssue MapIssue(Exception exception)
    {
        if (exception is ExportFailureException exportFailure)
        {
            return new BackupExportIssue(
                exportFailure.Stage,
                exportFailure.Code,
                exportFailure.Message,
                exportFailure.RelativePath,
                exportFailure.IsRetryable);
        }

        return exception switch
        {
            UnauthorizedAccessException => new BackupExportIssue(
                "unknown",
                "ACCESS_DENIED",
                exception.Message,
                IsRetryable: true),
            IOException => new BackupExportIssue(
                "unknown",
                "IO_FAILURE",
                exception.Message,
                IsRetryable: true),
            _ => new BackupExportIssue(
                "unknown",
                "EXPORT_UNEXPECTED_FAILURE",
                exception.Message),
        };
    }

    private sealed record SourceFile(string FullPath, string RelativePath);

    private sealed record SourceSnapshot(long FileCount, long TotalBytes, long LargestFileBytes);

    private sealed record CopiedFile(long Length, string Sha256, DateTimeOffset LastWriteTimeUtc);

    private sealed class ExportFailureException(
        string stage,
        string code,
        string message,
        string? relativePath = null,
        bool isRetryable = false,
        Exception? innerException = null) : Exception(message, innerException)
    {
        public string Stage { get; } = stage;

        public string Code { get; } = code;

        public string? RelativePath { get; } = relativePath;

        public bool IsRetryable { get; } = isRetryable;
    }
}
