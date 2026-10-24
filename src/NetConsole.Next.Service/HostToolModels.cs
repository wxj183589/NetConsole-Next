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
    string? Error,
    long CreatedMs,
    long? CompletedMs,
    string? DownloadName,
    bool CanDownload,
    bool CanCancel);

internal sealed record DesktopHelperResponse(string Status, string? Detail, string? RequestId);
