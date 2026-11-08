namespace NetConsole.Next.Service;

internal sealed record CompleteConfigurationStorage(string ProfileId, int ManifestCount, long ProfileStoredBytes,
    int MaximumBackupsPerProfile, long MaximumStoredBytes, long? SharedStoredBytes, long AvailableDiskBytes,
    bool ProfileLimitReached, bool? SharedLimitReached, long MeasuredMs)
{
    public string RetentionPolicy => "PRESERVE_ALL_STOP_AT_CAPACITY";
    public bool AutomaticDeletion => false;
}

internal sealed partial class SimulatorConfigurationService
{
    public CompleteConfigurationStorage Storage(string profileId, WebUserIdentity actor, P2B5SiteScopeDecision scope)
    {
        _ = Target(profileId, actor, scope);
        lock (_storageGate)
        {
            var directory = DirectoryFor(profileId, false);
            var manifests = Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.json").Count() : 0;
            var bytes = MeasureStorage(directory);
            // Shared occupancy is only available to a global authority. A scoped
            // administrator must not learn storage growth in another Site.
            long? shared = scope.IsGlobal ? MeasureStorage(_root) : null;
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_root))!);
            return new(profileId, manifests, bytes, _settings.CompleteConfigMaxBackupsPerProfile,
                _settings.CompleteConfigMaxStoredBytes, shared, drive.AvailableFreeSpace,
                manifests >= _settings.CompleteConfigMaxBackupsPerProfile,
                shared is null ? null : shared >= _settings.CompleteConfigMaxStoredBytes,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
    }

    private long MeasureStorage(string root)
    {
        CheckPath(root);
        // An absent directory has zero usage. A file blocking any directory in
        // the hierarchy is damaged storage, so do not report a healthy zero.
        for (var current = Path.GetFullPath(root); current is not null; current = Path.GetDirectoryName(current))
            if (File.Exists(current)) throw new IOException("Configuration storage directory is occupied by a file.");
        long bytes = 0;
        var pending = new Stack<string>();
        if (Directory.Exists(root)) pending.Push(root);
        while (pending.TryPop(out var directory))
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                CheckPath(entry);
                if (Directory.Exists(entry)) pending.Push(entry);
                else bytes = checked(bytes + new FileInfo(entry).Length);
            }
        return bytes;
    }
}
