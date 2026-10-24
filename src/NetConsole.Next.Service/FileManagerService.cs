using Microsoft.AspNetCore.StaticFiles;

namespace NetConsole.Next.Service;

internal sealed record ResolvedFile(string RootId, string RelativePath, string FullPath, bool IsDirectory);

internal sealed class FileManagerService
{
    private readonly RuntimeSettings _settings;
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();
    private static readonly (string Id, string Label, string Directory)[] RootDefinitions =
    [
        ("raw", "Raw 采集", "raw"),
        ("terminal-recordings", "终端录制", "terminal-recordings"),
        ("inbox", "导入目录", "inbox"),
        ("archive", "归档目录", "archive"),
        ("backup", "备份目录", "backup"),
        ("published", "已发布文件", "published"),
        ("analysis", "分析结果", "analysis"),
        ("exports", "导出文件", "exports"),
        ("logs", "服务日志", "logs")
    ];

    public FileManagerService(RuntimeSettings settings) => _settings = settings;

    public IReadOnlyList<FileRootDefinition> Roots() => RootDefinitions.Select(root =>
        new FileRootDefinition(root.Id, root.Label, root.Directory, Directory.Exists(RootPath(root.Directory)))).ToArray();

    public FileListResult List(string rootId, string? relativePath, string? search, string? sort, string? direction, int page, int pageSize)
    {
        var resolved = Resolve(rootId, relativePath, true);
        if (!resolved.IsDirectory) throw new InvalidOperationException("The selected path is not a directory.");
        var query = (search ?? string.Empty).Trim();
        var entries = new List<FileEntry>();
        foreach (var path in Directory.EnumerateFileSystemEntries(resolved.FullPath))
        {
            var fileName = Path.GetFileName(path);
            var relative = ToRelative(resolved.RootId, path);
            if (IsProtected(path, fileName) || IsReparse(path)) continue;
            if (query.Length > 0 && fileName.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var directory = Directory.Exists(path);
            FileSystemInfo info = directory ? new DirectoryInfo(path) : new FileInfo(path);
            var size = info is FileInfo file ? file.Length : 0;
            entries.Add(new(fileName, relative, directory ? "DIRECTORY" : "FILE", size,
                new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds(), !directory, true));
        }
        var descending = string.Equals(direction, "desc", StringComparison.OrdinalIgnoreCase);
        entries = (sort?.ToLowerInvariant() switch
        {
            "size" => descending ? entries.OrderByDescending(x => x.Size) : entries.OrderBy(x => x.Size),
            "modified" => descending ? entries.OrderByDescending(x => x.ModifiedMs) : entries.OrderBy(x => x.ModifiedMs),
            _ => descending ? entries.OrderByDescending(x => x.Name, StringComparer.OrdinalIgnoreCase) : entries.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
        }).ToList();
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 500);
        return new(rootId, resolved.RelativePath, entries.Skip((page - 1) * pageSize).Take(pageSize).ToArray(), page, pageSize, entries.Count, page * pageSize < entries.Count);
    }

    public ResolvedFile Resolve(string rootId, string? relativePath, bool requireExists)
    {
        var root = RootDefinitions.FirstOrDefault(item => string.Equals(item.Id, rootId, StringComparison.OrdinalIgnoreCase));
        if (root == default) throw new KeyNotFoundException("Unknown logical file root.");
        var rootPath = RootPath(root.Directory);
        Directory.CreateDirectory(rootPath);
        RejectUnsafe(relativePath);
        var normalized = (relativePath ?? string.Empty).Replace('\\', '/').Trim('/');
        var target = Path.GetFullPath(Path.Combine(rootPath, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(rootPath, target);
        if (relative is ".") relative = string.Empty;
        if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(x => x == ".."))
            throw new UnauthorizedAccessException("Path is outside the selected logical root.");
        if (IsProtected(target, Path.GetFileName(target))) throw new UnauthorizedAccessException("Protected service data cannot be accessed.");
        EnsureNoReparse(rootPath, target);
        if (requireExists && !File.Exists(target) && !Directory.Exists(target)) throw new FileNotFoundException("File or directory was not found.");
        return new(root.Id, relative.Replace(Path.DirectorySeparatorChar, '/'), target, Directory.Exists(target));
    }

    public (FileStream Stream, string DownloadName, string ContentType) OpenDownload(string rootId, string? relativePath)
    {
        var file = Resolve(rootId, relativePath, true);
        if (file.IsDirectory) throw new InvalidOperationException("Directories must be archived before download.");
        var name = Path.GetFileName(file.FullPath);
        var contentType = ContentTypes.TryGetContentType(name, out var value) ? value : "application/octet-stream";
        return (new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan), name, contentType);
    }

    public IReadOnlyList<(ResolvedFile File, string EntryName, long Size)> Expand(
        ArchiveSelection selection,
        ArchiveExpansionBudget budget,
        ISet<string> seenFiles,
        CancellationToken cancellationToken)
    {
        var output = new List<(ResolvedFile, string, long)>();
        var pending = new Stack<(ResolvedFile File, int Depth)>();
        var paths = selection.Paths ?? Array.Empty<string>();
        if (paths.Count > 1_000)
            throw new ArchiveRejectedException("ARCHIVE_RESOURCE_LIMIT", "A single archive selection contains too many paths.", StatusCodes.Status413PayloadTooLarge);

        foreach (var requested in paths.Reverse())
            pending.Push((Resolve(selection.RootId, requested, true), 0));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (resolved, depth) = pending.Pop();
            if (!resolved.IsDirectory)
            {
                AddFile(resolved, selection.RootId, output, budget, seenFiles);
                continue;
            }

            budget.CountDirectory(depth);
            try
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(resolved.FullPath))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    budget.CountMetadata();
                    if (IsReparse(path) || IsProtected(path, Path.GetFileName(path))) continue;
                    try
                    {
                        pending.Push((Resolve(selection.RootId, ToRelative(selection.RootId, path), true), depth + 1));
                    }
                    catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                    {
                        throw new ArchiveRejectedException("ARCHIVE_INPUT_CHANGED", "A selected file or directory changed during the archive scan.", StatusCodes.Status409Conflict);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ArchiveRejectedException("ARCHIVE_SCAN_FAILED", "A selected directory could not be scanned safely.", StatusCodes.Status409Conflict);
            }
        }

        return output;
    }

    private static void AddFile(
        ResolvedFile resolved,
        string rootId,
        List<(ResolvedFile File, string EntryName, long Size)> output,
        ArchiveExpansionBudget budget,
        ISet<string> seenFiles)
    {
        if (!File.Exists(resolved.FullPath) || IsReparse(resolved.FullPath))
            throw new ArchiveRejectedException("ARCHIVE_INPUT_CHANGED", "A selected file changed during the archive scan.", StatusCodes.Status409Conflict);
        var fullPath = Path.GetFullPath(resolved.FullPath);
        if (!seenFiles.Add(fullPath)) return;
        try
        {
            var info = new FileInfo(fullPath);
            budget.CountMetadata();
            budget.CountFile(info.Length);
            output.Add((resolved, Path.Combine(rootId, resolved.RelativePath).Replace(Path.DirectorySeparatorChar, '/'), info.Length));
        }
        catch (FileNotFoundException)
        {
            throw new ArchiveRejectedException("ARCHIVE_INPUT_CHANGED", "A selected file changed during the archive scan.", StatusCodes.Status409Conflict);
        }
        catch (UnauthorizedAccessException)
        {
            throw new ArchiveRejectedException("ARCHIVE_SCAN_FAILED", "A selected file could not be inspected safely.", StatusCodes.Status409Conflict);
        }
        catch (IOException)
        {
            throw new ArchiveRejectedException("ARCHIVE_SCAN_FAILED", "A selected file could not be inspected safely.", StatusCodes.Status409Conflict);
        }
    }

    public string RootPath(string directory) => Path.Combine(_settings.DataRoot, directory);
    public string ToRelative(string rootId, string fullPath)
    {
        var root = RootDefinitions.First(item => string.Equals(item.Id, rootId, StringComparison.OrdinalIgnoreCase));
        return Path.GetRelativePath(RootPath(root.Directory), fullPath).Replace(Path.DirectorySeparatorChar, '/');
    }

    private static void RejectUnsafe(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (path.Any(c => char.IsControl(c)) || Path.IsPathRooted(path) || path.Contains(':'))
            throw new UnauthorizedAccessException("Absolute, drive-qualified and control-character paths are blocked.");
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part is "." or "..")) throw new UnauthorizedAccessException("Path traversal is blocked.");
    }

    private static void EnsureNoReparse(string root, string target)
    {
        var current = root;
        if (IsReparse(current)) throw new UnauthorizedAccessException("Reparse point roots are blocked.");
        var relative = Path.GetRelativePath(root, target);
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) && IsReparse(current))
                throw new UnauthorizedAccessException("Reparse point traversal is blocked.");
        }
    }

    private static bool IsReparse(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool IsProtected(string path, string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower is "catalog.db" or "catalog.db-wal" or "catalog.db-shm" or ".netconsole-next-root" or ".raw-recovery-in-progress" or "local-api-token") return true;
        if (lower.StartsWith("catalog.sqlite", StringComparison.Ordinal) || lower.EndsWith(".sqlite", StringComparison.Ordinal) || lower.Contains(".sqlite-", StringComparison.Ordinal) || lower.EndsWith(".open", StringComparison.Ordinal)) return true;
        if (lower.Contains("credential") || lower.Contains("password")) return true;
        var normalized = path.Replace('\\', '/').ToLowerInvariant();
        return normalized.Contains("/credentials/") || normalized.EndsWith("/credentials");
    }
}
