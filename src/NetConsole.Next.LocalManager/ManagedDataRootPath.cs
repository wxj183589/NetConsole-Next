namespace NetConsole.Next.LocalManager;

internal static class ManagedDataRootPath
{
    internal static string ResolveExistingDirectory(string requestedRoot)
    {
        var full = Normalize(requestedRoot);
        var root = Path.GetPathRoot(full) ?? throw new ArgumentException("An absolute data path is required.", nameof(requestedRoot));
        // Inspect from the filesystem root down: never inspect a child through a reparse ancestor.
        var current = root;
        CheckDirectory(current);
        foreach (var part in full[root.Length..].Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            CheckDirectory(current);
        }
        return full;
    }

    private static void CheckDirectory(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
            throw new InvalidOperationException("Managed directories cannot traverse reparse points or files.");
    }

    // This check is lexical: run it before Exists, attributes, marker or SQLite
    // access. A managed marker never authorizes probing a legacy data root.
    internal static string Normalize(string requestedRoot)
    {
        var full = Path.GetFullPath(requestedRoot);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new ArgumentException("Device namespace paths cannot be managed data roots.", nameof(requestedRoot));
        var comparison = string.Join('\\', full.Replace('/', '\\').Split('\\')
            .Select(part => part.TrimEnd(' ', '.'))).TrimEnd('\\');
        foreach (var legacy in new[] { @"D:\NetConsoleData", @"D:\NetConsoleData-dev" })
            if (comparison.Equals(legacy, StringComparison.OrdinalIgnoreCase) ||
                comparison.StartsWith(legacy + "\\", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Original NetConsole data roots cannot be accessed by Local Manager.", nameof(requestedRoot));
        return full;
    }
}
