using CodexBackup.Core.Codex;

namespace CodexBackup.Core.Backup;

public static class CodexBackupCandidateFactory
{
    public static BackupCandidate Create(
        CodexDataItem item,
        bool isSelected,
        bool isReviewApproved = false)
    {
        ArgumentNullException.ThrowIfNull(item);

        return new BackupCandidate(
            item.Id,
            item.Name,
            Path.GetFullPath(item.FullPath),
            item.Kind,
            item.Policy,
            item.RestoreLevel,
            item.EstimatedBytes,
            isSelected,
            isReviewApproved);
    }
}
