using CodexBackup.Core.Discovery;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CodexBackup.App.Presentation;

public sealed class ProjectListItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public ProjectListItem(DiscoveredProject project)
    {
        Project = project;
        Name = project.DisplayName;
        Path = project.RootPath;
        SourceSummary = FormatSources(project.Sources);
        GitSummary = FormatGitStatus(project);
        SizeSummary = FormatInventory(project.FileInventory);
        RiskSummary = FormatRisks(project);
        SessionReferenceCount = project.SessionReferenceCount;
        RequiresReview = project.RequiresReview;
        _isSelected = !project.RequiresReview;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public DiscoveredProject Project { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public string Name { get; }

    public string Path { get; }

    public string SourceSummary { get; }

    public string GitSummary { get; }

    public string SizeSummary { get; }

    public string RiskSummary { get; }

    public int SessionReferenceCount { get; }

    public bool RequiresReview { get; }

    private static string FormatGitStatus(DiscoveredProject project)
    {
        if (!project.IsGitRepository)
        {
            return "非 Git 项目";
        }

        var status = project.GitStatus;
        if (status is null || !status.IsAvailable)
        {
            return "Git 状态未知";
        }

        var branch = status.IsDetachedHead ? "分离状态" : status.BranchName ?? "未知分支";
        if (status.IsClean)
        {
            return $"{branch}，干净";
        }

        return $"{branch}，变更 {status.ChangedTrackedFileCount}，未跟踪 {status.UntrackedFileCount}";
    }

    private static string FormatInventory(ProjectFileInventory? inventory)
    {
        return inventory is null
            ? "尚未估算"
            : $"{FormatBytes(inventory.TotalBytes)} / {inventory.FileCount:N0} 个文件";
    }

    private static string FormatRisks(DiscoveredProject project)
    {
        var risks = new List<string>();
        if (project.RequiresReview)
        {
            risks.Add("需确认项目根目录");
        }

        if (project.FileInventory is { PotentialSecretFileCount: > 0 } inventory)
        {
            risks.Add($"疑似敏感文件名 {inventory.PotentialSecretFileCount}");
        }

        if (project.FileInventory is { LargeFileCount: > 0 } largeInventory)
        {
            risks.Add($"大文件 {largeInventory.LargeFileCount}");
        }

        if (project.FileInventory is { UnreadableItemCount: > 0 } partialInventory)
        {
            risks.Add($"未读到 {partialInventory.UnreadableItemCount}");
        }

        return risks.Count == 0 ? "未发现明显风险" : string.Join("；", risks);
    }

    private static string FormatSources(ProjectDiscoverySource sources)
    {
        var names = new List<string>();
        if (sources.HasFlag(ProjectDiscoverySource.CodexSessionPath))
        {
            names.Add("Codex 记录");
        }

        if (sources.HasFlag(ProjectDiscoverySource.GitRepository))
        {
            names.Add("Git 仓库");
        }

        if (sources.HasFlag(ProjectDiscoverySource.ProjectMarker))
        {
            names.Add("项目标志");
        }

        if (sources.HasFlag(ProjectDiscoverySource.UserAdded))
        {
            names.Add("手动添加");
        }

        return names.Count == 0 ? "未知" : string.Join("、", names);
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:0.##} {units[unitIndex]}";
    }
}
