using System.Security.Cryptography;
using System.Text;
using CodexBackup.Core.Discovery;

namespace CodexBackup.Core.Backup;

public static class ProjectBackupCandidateFactory
{
    public static BackupCandidate Create(
        DiscoveredProject project,
        bool isSelected,
        bool isReviewApproved = false)
    {
        ArgumentNullException.ThrowIfNull(project);

        var normalizedPath = Path.GetFullPath(project.RootPath);
        var pathHash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(normalizedPath.ToUpperInvariant())));

        return new BackupCandidate(
            $"project-{pathHash[..16].ToLowerInvariant()}",
            project.DisplayName,
            normalizedPath,
            BackupDataKind.Project,
            project.RequiresReview ? BackupPolicy.UnknownReviewRequired : BackupPolicy.Include,
            RestoreLevel.VerifiedExact,
            project.FileInventory?.TotalBytes ?? 0,
            isSelected,
            isReviewApproved,
            project.Sources);
    }
}
