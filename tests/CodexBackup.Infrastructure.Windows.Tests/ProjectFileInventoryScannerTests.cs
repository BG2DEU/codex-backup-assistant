using CodexBackup.Infrastructure.Windows.Discovery;

namespace CodexBackup.Infrastructure.Windows.Tests;

public sealed class ProjectFileInventoryScannerTests
{
    [Fact]
    public void Scan_CountsFilesSizesAndFilenameRisksWithoutReadingContents()
    {
        var fixture = Path.Combine(Path.GetTempPath(), "codex-backup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(fixture, "src"));
        try
        {
            File.WriteAllBytes(Path.Combine(fixture, "src", "small.txt"), new byte[3]);
            File.WriteAllBytes(Path.Combine(fixture, "large.bin"), new byte[10]);
            File.WriteAllBytes(Path.Combine(fixture, ".env"), new byte[2]);

            var inventory = new ProjectFileInventoryScanner(largeFileThresholdBytes: 5).Scan(fixture);

            Assert.Equal(3, inventory.FileCount);
            Assert.Equal(15, inventory.TotalBytes);
            Assert.Equal(1, inventory.LargeFileCount);
            Assert.Equal(10, inventory.LargestFileBytes);
            Assert.Equal(1, inventory.PotentialSecretFileCount);
            Assert.True(inventory.IsComplete);
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }
}
