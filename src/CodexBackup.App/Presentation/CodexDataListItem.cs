using System.ComponentModel;
using CodexBackup.Core.Backup;
using CodexBackup.Core.Codex;

namespace CodexBackup.App.Presentation;

public sealed class CodexDataListItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public CodexDataListItem(CodexDataItem item)
    {
        Item = item;
        _isSelected = item.Policy is BackupPolicy.Include or BackupPolicy.IncludePortableAndNative;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public CodexDataItem Item { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!CanSelect || _isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public bool CanSelect => Item.Policy is
        BackupPolicy.Include or
        BackupPolicy.IncludePortableAndNative or
        BackupPolicy.UnknownReviewRequired;

    public bool RequiresReview => Item.Policy is BackupPolicy.UnknownReviewRequired;

    public string Name => Item.Name;

    public string CategorySummary => Item.Kind switch
    {
        BackupDataKind.CodexSession => "对话会话",
        BackupDataKind.CodexState => "Codex 状态",
        BackupDataKind.Configuration => "规则/配置",
        BackupDataKind.Skill => "技能",
        BackupDataKind.Plugin => "插件",
        BackupDataKind.GeneratedAsset => "生成资源",
        BackupDataKind.EnvironmentInventory => "环境清单",
        _ => Item.Kind.ToString(),
    };

    public string PolicySummary => Item.Policy switch
    {
        BackupPolicy.Include => "包含",
        BackupPolicy.IncludePortableAndNative => "原始快照+通用 JSON/Markdown",
        BackupPolicy.InventoryOnly => "仅记录清单",
        BackupPolicy.ExcludeCredential => "凭据：强制排除",
        BackupPolicy.ExcludeVolatile => "临时数据：强制排除",
        BackupPolicy.UnknownReviewRequired => "未知项：需确认",
        _ => Item.Policy.ToString(),
    };

    public string SizeSummary => $"{ProjectListItem.FormatBytes(Item.EstimatedBytes)} / {Item.FileCount:N0} 个文件";

    public string RiskSummary => Item.Policy switch
    {
        BackupPolicy.ExcludeCredential => "不会迁移",
        BackupPolicy.UnknownReviewRequired => "适配器尚未识别",
        _ when Item.ContainsPotentialSecrets => "可能含私人信息",
        _ when Item.RestoreLevel is RestoreLevel.NativeBestEffort => "恢复前检查版本兼容",
        _ => "可精确复制",
    };

    public string Reason => Item.ClassificationReason;
}
