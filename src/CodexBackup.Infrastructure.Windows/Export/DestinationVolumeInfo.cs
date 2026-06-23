namespace CodexBackup.Infrastructure.Windows.Export;

public sealed record DestinationVolumeInfo(long AvailableFreeBytes, string? FileSystem);

public interface IDestinationVolumeInfoProvider
{
    DestinationVolumeInfo GetInfo(string destinationPath);
}

public sealed class WindowsDestinationVolumeInfoProvider : IDestinationVolumeInfoProvider
{
    public DestinationVolumeInfo GetInfo(string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var root = Path.GetPathRoot(Path.GetFullPath(destinationPath));
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new IOException("Unable to determine the destination volume.");
        }

        var drive = new DriveInfo(root);
        return new DestinationVolumeInfo(drive.AvailableFreeSpace, drive.DriveFormat);
    }
}
