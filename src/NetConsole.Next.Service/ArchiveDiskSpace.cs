using System.Security;

namespace NetConsole.Next.Service;

internal interface IArchiveDiskSpaceProvider
{
    long? GetAvailableFreeSpace(string path);
}

internal sealed class DriveInfoArchiveDiskSpaceProvider : IArchiveDiskSpaceProvider
{
    public long? GetAvailableFreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(root)) return null;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or DriveNotFoundException or SecurityException)
        {
            return null;
        }
    }
}
