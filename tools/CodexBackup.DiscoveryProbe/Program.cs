using System.Diagnostics;
using System.Text.Json;
using CodexBackup.Core.Backup;
using CodexBackup.Core.Discovery;
using CodexBackup.Infrastructure.Windows.Codex;
using CodexBackup.Infrastructure.Windows.Discovery;

var stopwatch = Stopwatch.StartNew();
var result = WindowsProjectDiscovery.CreateService().Discover(
    WindowsProjectDiscovery.CreateDefaultRequest());
stopwatch.Stop();

var defaultPlan = new BackupPlanBuilder().Build(result.Projects.Select(project =>
    ProjectBackupCandidateFactory.Create(project, isSelected: !project.RequiresReview)));
var codexRoot = CodexStorageAdapter.GetDefaultRoot();
var codexInventory = new CodexStorageAdapter().Inspect(codexRoot);
var codexUsage = new CodexUsageInspector().Inspect(codexRoot);
var conversationStopwatch = Stopwatch.StartNew();
var conversationFiles = EnumerateConversationFiles(codexRoot);
var conversationParser = new CodexConversationParser();
var conversations = new List<CodexBackup.Core.Codex.PortableConversation>();
var conversationReadFailureCount = 0;
foreach (var conversationFile in conversationFiles)
{
    try
    {
        conversations.Add(conversationParser.Parse(conversationFile));
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        conversationReadFailureCount++;
    }
}

conversationStopwatch.Stop();

var summary = new
{
    result.SessionFileCount,
    result.SessionPathRecordCount,
    result.UniqueSessionPathCount,
    ProjectCount = result.Projects.Count,
    CodexRecordedProjectCount = result.Projects.Count(project =>
        project.Sources.HasFlag(ProjectDiscoverySource.CodexSessionPath)),
    SupplementalOnlyProjectCount = result.Projects.Count(project =>
        !project.Sources.HasFlag(ProjectDiscoverySource.CodexSessionPath)),
    GitProjectCount = result.Projects.Count(project => project.IsGitRepository),
    GitStatusAvailableCount = result.Projects.Count(project => project.GitStatus?.IsAvailable == true),
    GitProjectsWithChanges = result.Projects.Count(project => project.GitStatus?.HasLocalChanges == true),
    GitProjectsWithoutRemote = result.Projects.Count(project =>
        project.GitStatus is { IsAvailable: true, HasRemote: false }),
    InventoriedProjectCount = result.Projects.Count(project => project.FileInventory is not null),
    EstimatedProjectBytes = result.Projects.Sum(project => project.FileInventory?.TotalBytes ?? 0),
    DefaultSelectedProjectBytes = result.Projects
        .Where(project => !project.RequiresReview)
        .Sum(project => project.FileInventory?.TotalBytes ?? 0),
    DefaultPlanIncludedProjectCount = defaultPlan.Items.Count(item =>
        item.State is BackupPlanItemState.Included),
    defaultPlan.IncludedBytes,
    defaultPlan.CanExport,
    DefaultPlanBlockingIssueCodes = defaultPlan.Issues
        .Where(issue => issue.Severity is BackupPlanIssueSeverity.Blocking)
        .Select(issue => issue.Code)
        .ToArray(),
    ProjectsWithPotentialSecrets = result.Projects.Count(project =>
        project.FileInventory?.PotentialSecretFileCount > 0),
    ProjectsWithLargeFiles = result.Projects.Count(project =>
        project.FileInventory?.LargeFileCount > 0),
    ProjectsWithPartialInventory = result.Projects.Count(project =>
        project.FileInventory?.IsComplete == false),
    MarkerlessCodexPathCount = result.Projects.Count(project =>
        project.Sources == ProjectDiscoverySource.CodexSessionPath),
    CodexAdapterVersion = codexInventory.AdapterVersion,
    CodexTopLevelItemCount = codexInventory.Items.Count,
    CodexDefaultIncludedItemCount = codexInventory.Items.Count(item =>
        item.Policy is BackupPolicy.Include or BackupPolicy.IncludePortableAndNative),
    CodexCredentialExcludedItemCount = codexInventory.Items.Count(item =>
        item.Policy is BackupPolicy.ExcludeCredential),
    CodexVolatileExcludedItemCount = codexInventory.Items.Count(item =>
        item.Policy is BackupPolicy.ExcludeVolatile),
    CodexInventoryOnlyItemCount = codexInventory.Items.Count(item =>
        item.Policy is BackupPolicy.InventoryOnly),
    CodexUnknownReviewItemCount = codexInventory.Items.Count(item =>
        item.Policy is BackupPolicy.UnknownReviewRequired),
    CodexTotalBytes = codexInventory.TotalBytes,
    codexUsage.RunningProcessCount,
    codexUsage.LockedDatabaseCount,
    codexUsage.DatabaseSidecarCount,
    codexUsage.CanCreateNativeSnapshot,
    ConversationFileCount = conversationFiles.Count,
    PortableConversationCount = conversations.Count,
    PortableConversationMessageCount = conversations.Sum(conversation =>
        conversation.Messages.Count),
    PortableConversationUserMessageCount = conversations.Sum(conversation =>
        conversation.Messages.Count(message =>
            message.Role is CodexBackup.Core.Codex.PortableConversationRole.User)),
    PortableConversationAssistantMessageCount = conversations.Sum(conversation =>
        conversation.Messages.Count(message =>
            message.Role is CodexBackup.Core.Codex.PortableConversationRole.Assistant)),
    PortableConversationRedactionCount = conversations.Sum(conversation =>
        conversation.RedactionCount),
    PortableConversationWarningCount = conversations.Sum(conversation =>
        conversation.Warnings.Count),
    ConversationReadFailureCount = conversationReadFailureCount,
    ConversationSchemaFingerprintCount = conversations
        .Select(conversation => conversation.SourceSchemaFingerprint)
        .Distinct(StringComparer.Ordinal)
        .Count(),
    ConversationsWithRollbackEvents = conversations.Count(conversation =>
        conversation.RollbackEventCount > 0),
    ConversationParseElapsedMilliseconds = conversationStopwatch.ElapsedMilliseconds,
    WarningCount = result.Warnings.Count,
    WarningCodes = result.Warnings
        .GroupBy(warning => warning.Code)
        .ToDictionary(group => group.Key, group => group.Count()),
    ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
};

Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions
{
    WriteIndented = true,
}));

static IReadOnlyList<string> EnumerateConversationFiles(string codexRoot)
{
    var files = new List<string>();
    foreach (var directoryName in new[] { "sessions", "archived_sessions" })
    {
        var root = Path.Combine(codexRoot, directoryName);
        if (!Directory.Exists(root))
        {
            continue;
        }

        files.AddRange(Directory.EnumerateFiles(
            root,
            "*.jsonl",
            new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            }));
    }

    return files;
}
