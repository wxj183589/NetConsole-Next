using System.Net;

namespace NetConsole.Next.Service;

internal sealed class RuntimeSettings
{
    public required string DataRoot { get; init; }
    public required int WebPort { get; init; }
    public string WebBindAddress { get; init; } = "127.0.0.1";
    public bool WebLanEnabled { get; init; }
    public required int SyslogPort { get; init; }
    public required IPAddress SyslogBind { get; init; }
    public required int SyslogReceiveBufferBytes { get; init; }
    public bool H3cEnabled { get; init; }
    public bool TerminalEnabled { get; init; }
    public bool TerminalDevelopmentSimulationEnabled { get; init; }
    public bool H3cDevelopmentEnvironmentCredentialsEnabled { get; init; }

    // Archive jobs are deliberately bounded so file packaging cannot compete
    // with the long-running collection paths on an industrial host.
    public int ArchiveMaxRunningJobs { get; init; } = 1;
    public int ArchiveMaxQueuedJobs { get; init; } = 8;
    public int ArchiveMaxActiveJobsPerUser { get; init; } = 2;
    public int ArchiveMaxFileEntriesPerJob { get; init; } = 10_000;
    public long ArchiveMaxInputBytesPerJob { get; init; } = 20L * 1024 * 1024 * 1024;
    public long ArchiveMaxAggregateReservedBytes { get; init; } = 32L * 1024 * 1024 * 1024;
    public long ArchiveMaxMetadataBytesPerJob { get; init; } = 64L * 1024 * 1024;
    public int ArchiveMaxDirectoryDepth { get; init; } = 32;
    public int ArchiveMaxScannedDirectories { get; init; } = 5_000;
    public int ArchiveMaxMetadataEntriesPerJob { get; init; } = 25_000;
    public int ArchiveMaxRetainedTerminalJobs { get; init; } = 128;
    public int ArchiveCleanupIntervalSeconds { get; init; } = 300;
    public int ArchiveCompletedRetentionHours { get; init; } = 24;
    public int ArchiveFailedRetentionHours { get; init; } = 1;
    public int ArchiveCancelledRetentionHours { get; init; } = 1;
    public long ArchiveMinFreeDiskBytes { get; init; } = 256L * 1024 * 1024;
    public long ArchiveMaxTempBytesPerJob { get; init; } = 20L * 1024 * 1024 * 1024;
    public int ArchiveTestCopyDelayMilliseconds { get; init; }

    public static RuntimeSettings FromEnvironment()
    {
        var root = Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(root) && OperatingSystem.IsWindows())
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetConsoleNext", "data");
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("Set NETCONSOLE_NEXT_DATA_ROOT to an isolated new directory.");
        var full = Path.GetFullPath(root);
        if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar),
            Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Data root cannot be a filesystem root.");
        var bindText = Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_SYSLOG_BIND") ?? "127.0.0.1";
        if (!IPAddress.TryParse(bindText, out var bind))
            throw new InvalidOperationException("NETCONSOLE_NEXT_SYSLOG_BIND must be a valid IP address.");
        if (!IPAddress.IsLoopback(bind) &&
            Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_ALLOW_REMOTE_SYSLOG") != "1")
            throw new InvalidOperationException("Remote UDP reception requires explicit opt-in.");
        var receiveBuffer = ParsePositiveInt("NETCONSOLE_NEXT_SYSLOG_RCVBUF_BYTES", 4 * 1024 * 1024, 64 * 1024 * 1024);
        return new RuntimeSettings
        {
            DataRoot = full,
            WebPort = ParsePort("NETCONSOLE_NEXT_WEB_PORT", 18771, false),
            WebBindAddress = ParseWebBindAddress(),
            WebLanEnabled = Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_WEB_BIND") is "0.0.0.0" or "::" ||
                Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_WEB_LAN") == "1",
            SyslogPort = ParsePort("NETCONSOLE_NEXT_SYSLOG_PORT", 0, true),
            SyslogBind = bind,
            SyslogReceiveBufferBytes = receiveBuffer,
            H3cEnabled = Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_H3C_ENABLED") == "1",
            TerminalEnabled = Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_TERMINAL_ENABLED") == "1",
            TerminalDevelopmentSimulationEnabled = IsDevelopmentEnvironment() &&
                Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_TERMINAL_DEV_SIMULATION") == "1",
            H3cDevelopmentEnvironmentCredentialsEnabled = Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_H3C_DEV_ENV_CREDENTIALS") == "1",
            ArchiveMaxRunningJobs = ParseBoundedInt("NETCONSOLE_NEXT_ARCHIVE_MAX_RUNNING_JOBS", 1, 1, 4),
            ArchiveMaxQueuedJobs = ParseBoundedInt("NETCONSOLE_NEXT_ARCHIVE_MAX_QUEUED_JOBS", 8, 1, 64),
            ArchiveMaxActiveJobsPerUser = ParseBoundedInt("NETCONSOLE_NEXT_ARCHIVE_MAX_ACTIVE_JOBS_PER_USER", 2, 1, 16),
            ArchiveMaxFileEntriesPerJob = ParseBoundedInt("NETCONSOLE_NEXT_ARCHIVE_MAX_FILE_ENTRIES", 10_000, 1, 100_000),
            ArchiveMaxInputBytesPerJob = ParseBoundedLong("NETCONSOLE_NEXT_ARCHIVE_MAX_INPUT_BYTES", 20L * 1024 * 1024 * 1024, 1L * 1024 * 1024, 100L * 1024 * 1024 * 1024),
            ArchiveMaxAggregateReservedBytes = ParseBoundedLong("NETCONSOLE_NEXT_ARCHIVE_MAX_AGGREGATE_BYTES", 32L * 1024 * 1024 * 1024, 1L * 1024 * 1024, 200L * 1024 * 1024 * 1024),
            ArchiveMaxMetadataBytesPerJob = ParseBoundedLong("NETCONSOLE_NEXT_ARCHIVE_MAX_METADATA_BYTES", 64L * 1024 * 1024, 1L * 1024 * 1024, 512L * 1024 * 1024),
            ArchiveMaxDirectoryDepth = ParseBoundedInt("NETCONSOLE_NEXT_ARCHIVE_MAX_DIRECTORY_DEPTH", 32, 1, 128),
            ArchiveMaxScannedDirectories = ParseBoundedInt("NETCONSOLE_NEXT_ARCHIVE_MAX_SCANNED_DIRECTORIES", 5_000, 1, 100_000),
            ArchiveMaxMetadataEntriesPerJob = ParseBoundedInt("NETCONSOLE_NEXT_ARCHIVE_MAX_METADATA_ENTRIES", 25_000, 1, 250_000),
            ArchiveMaxRetainedTerminalJobs = ParseBoundedInt("NETCONSOLE_NEXT_ARCHIVE_MAX_RETAINED_JOBS", 128, 1, 2_048),
            ArchiveCleanupIntervalSeconds = ParseBoundedInt("NETCONSOLE_NEXT_ARCHIVE_CLEANUP_INTERVAL_SECONDS", 300, 1, 86_400),
            ArchiveCompletedRetentionHours = ParseBoundedInt("NETCONSOLE_NEXT_ARCHIVE_COMPLETED_RETENTION_HOURS", 24, 1, 168),
            ArchiveFailedRetentionHours = ParseBoundedInt("NETCONSOLE_NEXT_ARCHIVE_FAILED_RETENTION_HOURS", 1, 1, 168),
            ArchiveCancelledRetentionHours = ParseBoundedInt("NETCONSOLE_NEXT_ARCHIVE_CANCELLED_RETENTION_HOURS", 1, 1, 168),
            ArchiveMinFreeDiskBytes = ParseBoundedLong("NETCONSOLE_NEXT_ARCHIVE_MIN_FREE_DISK_BYTES", 256L * 1024 * 1024, 1L * 1024 * 1024, 100L * 1024 * 1024 * 1024),
            ArchiveMaxTempBytesPerJob = ParseBoundedLong("NETCONSOLE_NEXT_ARCHIVE_MAX_TEMP_BYTES_PER_JOB", 20L * 1024 * 1024 * 1024, 1L * 1024 * 1024, 100L * 1024 * 1024 * 1024),
            ArchiveTestCopyDelayMilliseconds = IsDevelopmentEnvironment()
                ? ParseBoundedInt("NETCONSOLE_NEXT_ARCHIVE_TEST_COPY_DELAY_MS", 0, 0, 1_000)
                : 0
        };
    }

    private static int ParsePort(string key, int fallback, bool zeroAllowed)
    {
        var raw = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (!int.TryParse(raw, out var p) || p > 65535 || p < (zeroAllowed ? 0 : 1))
            throw new InvalidOperationException(key + " is invalid.");
        return p;
    }

    private static bool IsDevelopmentEnvironment() =>
        string.Equals(Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase);

    private static string ParseWebBindAddress()
    {
        var value = Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_WEB_BIND");
        if (string.IsNullOrWhiteSpace(value)) return "127.0.0.1";
        if (value is not ("127.0.0.1" or "localhost" or "0.0.0.0" or "::"))
            throw new InvalidOperationException("NETCONSOLE_NEXT_WEB_BIND must be 127.0.0.1, 0.0.0.0 or ::.");
        return value == "localhost" ? "127.0.0.1" : value;
    }

    private static int ParsePositiveInt(string key, int fallback, int maximum)
    {
        var raw = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (!int.TryParse(raw, out var value) || value < 64 * 1024 || value > maximum)
            throw new InvalidOperationException($"{key} must be between 65536 and {maximum}.");
        return value;
    }

    private static int ParseBoundedInt(string key, int fallback, int minimum, int maximum)
    {
        var raw = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (!int.TryParse(raw, out var value) || value < minimum || value > maximum)
            throw new InvalidOperationException($"{key} must be between {minimum} and {maximum}.");
        return value;
    }

    private static long ParseBoundedLong(string key, long fallback, long minimum, long maximum)
    {
        var raw = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (!long.TryParse(raw, out var value) || value < minimum || value > maximum)
            throw new InvalidOperationException($"{key} must be between {minimum} and {maximum}.");
        return value;
    }
}
