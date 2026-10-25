namespace NetConsole.Next.Service;

internal sealed record HostToolDefinition(
    string Id,
    string DisplayName,
    string Product,
    string? ExecutablePath,
    string Status,
    bool Configured,
    bool IsWindowsTool,
    string? Detail);

internal sealed record HostToolConfiguration(string? ExecutablePath);
internal sealed record HostToolLaunchRequest(string DeviceId);
internal sealed record HostDirectoryRequest(string RootId, string? RelativePath);

internal sealed record FileRootDefinition(string Id, string Label, string Path, bool Exists);
internal sealed record FileEntry(
    string Name,
    string RelativePath,
    string Kind,
    long Size,
    long ModifiedMs,
    bool CanDownload,
    bool CanArchive);
internal sealed record FileListResult(
    string RootId,
    string RelativePath,
    IReadOnlyList<FileEntry> Items,
    int Page,
    int PageSize,
    int Total,
    bool HasMore);
internal sealed record ArchiveSelection(string RootId, IReadOnlyList<string> Paths);
internal sealed record ArchiveRequest(IReadOnlyList<ArchiveSelection> Selections);
internal sealed record ArchiveJobSnapshot(
    string Id,
    string State,
    int SelectedCount,
    int ProcessedCount,
    long ProcessedBytes,
    long? TotalBytes,
    string? Current,
    string? ErrorCode,
    string? Error,
    long CreatedMs,
    long? CompletedMs,
    string? DownloadName,
    bool CanDownload,
    bool CanCancel);
internal sealed record ArchiveDownload(Stream Stream, string Name);

internal sealed class ArchiveRejectedException : Exception
{
    public ArchiveRejectedException(string code, string message, int statusCode = StatusCodes.Status429TooManyRequests)
        : base(message)
    {
        Code = code;
        StatusCode = statusCode;
    }

    public string Code { get; }
    public int StatusCode { get; }
}

internal sealed class ArchiveExpansionBudget
{
    private readonly int _maxFileEntries;
    private readonly long _maxInputBytes;
    private readonly int _maxDirectoryDepth;
    private readonly int _maxDirectories;
    private readonly int _maxMetadataEntries;
    private readonly long _maxMetadataBytes;
    private readonly int _metadataBytesPerEntry;

    public ArchiveExpansionBudget(RuntimeSettings settings)
    {
        _maxFileEntries = settings.ArchiveMaxFileEntriesPerJob;
        _maxInputBytes = settings.ArchiveMaxInputBytesPerJob;
        _maxDirectoryDepth = settings.ArchiveMaxDirectoryDepth;
        _maxDirectories = settings.ArchiveMaxScannedDirectories;
        _maxMetadataEntries = settings.ArchiveMaxMetadataEntriesPerJob;
        _maxMetadataBytes = settings.ArchiveMaxMetadataBytesPerJob;
        _metadataBytesPerEntry = 512;
    }

    public int FileEntries { get; private set; }
    public long InputBytes { get; private set; }
    public int ScannedDirectories { get; private set; }
    public int MetadataEntries { get; private set; }
    public long MetadataBytes { get; private set; }

    public void CountMetadata()
    {
        if (++MetadataEntries > _maxMetadataEntries ||
            (MetadataBytes += _metadataBytesPerEntry) > _maxMetadataBytes)
            throw new ArchiveRejectedException("ARCHIVE_METADATA_LIMIT", "The archive directory scan exceeded its metadata budget.", StatusCodes.Status413PayloadTooLarge);
    }

    public void CountDirectory(int depth)
    {
        if (depth > _maxDirectoryDepth)
            throw new ArchiveRejectedException("ARCHIVE_DIRECTORY_DEPTH_LIMIT", "The selected directory is deeper than the archive scan limit.", StatusCodes.Status413PayloadTooLarge);
        if (++ScannedDirectories > _maxDirectories)
            throw new ArchiveRejectedException("ARCHIVE_DIRECTORY_SCAN_LIMIT", "The archive directory scan exceeded its directory budget.", StatusCodes.Status413PayloadTooLarge);
    }

    public void CountFile(long size)
    {
        if (++FileEntries > _maxFileEntries)
            throw new ArchiveRejectedException("ARCHIVE_RESOURCE_LIMIT", "The archive contains too many files.", StatusCodes.Status413PayloadTooLarge);
        if (size < 0 || InputBytes > _maxInputBytes - size)
            throw new ArchiveRejectedException("ARCHIVE_RESOURCE_LIMIT", "The archive input exceeds its size budget.", StatusCodes.Status413PayloadTooLarge);
        InputBytes += size;
    }
}

internal sealed record DesktopHelperResponse(string Status, string? Detail, string? RequestId);
