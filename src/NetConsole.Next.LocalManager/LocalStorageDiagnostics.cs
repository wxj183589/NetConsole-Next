namespace NetConsole.Next.LocalManager;

internal sealed record LocalStorageReport(string State, long? AvailableBytes, long? VolumeBytes,
    int InspectedLogFiles, long InspectedLogBytes, string Detail);

internal static class LocalStorageDiagnostics
{
    // The service logger retains 14 files of at most 5 MiB each. Diagnose only
    // the bounded top-level service log set; never recurse or delete logs/data.
    internal static LocalStorageReport Run(string requestedRoot, Func<string, (long Available, long Total)>? capacity = null)
    {
        var root = ManagedDataRootPath.Normalize(requestedRoot);
        try
        {
            root = ManagedDataRootPath.ResolveExistingDirectory(root);
            var marker = new FileInfo(Path.Combine(root, ".netconsole-next-root"));
            if (!marker.Exists || marker.LinkTarget is not null || (marker.Attributes & FileAttributes.ReparsePoint) != 0 ||
                marker.Length > 128 || File.ReadAllText(marker.FullName).Trim() != "NETCONSOLE_NEXT_V1")
                throw new InvalidDataException("A valid managed root marker is required for log inspection.");
            var (available, total) = (capacity ?? ReadCapacity)(root);
            if (available < 0 || total <= 0 || available > total) throw new IOException("Volume capacity is unavailable.");
            var logRoot = Path.Combine(root, "logs");
            var count = 0;
            long bytes = 0;
            var oversized = false;
            if (Directory.Exists(logRoot))
            {
                ManagedDataRootPath.ResolveExistingDirectory(logRoot);
                foreach (var path in Directory.EnumerateFiles(logRoot, "service-*.log").Take(15))
                {
                    var file = new FileInfo(path);
                    if (file.LinkTarget is not null || (file.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Log inspection refuses links.");
                    count++;
                    bytes += file.Length;
                    oversized |= file.Length > 5 * 1024 * 1024;
                }
            }
            var low = available < 1024L * 1024 * 1024 || (double)available / total < .05;
            var retention = count > 14 || oversized;
            return new(low || retention ? "WARNING" : "PASS", available, total, count, bytes,
                low ? "Low disk space: retain data, free unrelated files or provision capacity; no automatic data deletion."
                : retention ? "Service log set exceeds 14 files or 5 MiB per file; inspect logger failures and retention permissions."
                : "Capacity is available; service log policy is 14 files × 5 MiB. No files were changed.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new("FAILED", null, null, 0, 0, "Capacity/log inspection failed (" + ex.GetType().Name + "); inspect permissions and directory paths.");
        }
    }

    private static (long, long) ReadCapacity(string root)
    {
        var drive = new DriveInfo(Path.GetPathRoot(root)!);
        return (drive.AvailableFreeSpace, drive.TotalSize);
    }
}
