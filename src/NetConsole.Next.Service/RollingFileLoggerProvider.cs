using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace NetConsole.Next.Service;

internal sealed class FileLogDiagnostics
{
    private long _writeFailures;
    public long WriteFailures => Interlocked.Read(ref _writeFailures);
    public void RecordWriteFailure() => Interlocked.Increment(ref _writeFailures);
}

// Small framework-only logger for service diagnostics. Entries are size bounded and retained by file count.
internal sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private const int MaximumEntryCharacters = 32 * 1024;
    private readonly string _logRoot;
    private readonly long _maximumFileBytes;
    private readonly int _retainedFileCount;
    private readonly FileLogDiagnostics _diagnostics;
    private readonly object _gate = new();
    private FileStream? _stream;
    private StreamWriter? _writer;
    private DateOnly? _activeDate;
    private int _activeIndex;
    private bool _disposed;

    public RollingFileLoggerProvider(string dataRoot, FileLogDiagnostics diagnostics,
        long maximumFileBytes = 5 * 1024 * 1024, int retainedFileCount = 14)
    {
        if (maximumFileBytes < 1024) throw new ArgumentOutOfRangeException(nameof(maximumFileBytes));
        if (retainedFileCount is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(retainedFileCount));
        _logRoot = Path.Combine(dataRoot, "logs");
        _diagnostics = diagnostics;
        _maximumFileBytes = maximumFileBytes;
        _retainedFileCount = retainedFileCount;
        Directory.CreateDirectory(_logRoot);
        EnsureNotReparsePoint(_logRoot);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            CloseWriter();
        }
    }

    private void Write<TState>(string categoryName, LogLevel level, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (level == LogLevel.None) return;
        try
        {
            var message = formatter(state, exception);
            if (exception is not null) message = $"{message}{Environment.NewLine}{exception}";
            if (message.Length > MaximumEntryCharacters)
                message = message[..MaximumEntryCharacters] + " [entry truncated]";
            var now = DateTimeOffset.UtcNow;
            var line = $"{now:O} [{level}] {categoryName} ({eventId.Id}) {message}{Environment.NewLine}";
            var lineBytes = Encoding.UTF8.GetByteCount(line);

            lock (_gate)
            {
                if (_disposed) return;
                EnsureWriter(DateOnly.FromDateTime(now.UtcDateTime), lineBytes);
                _writer!.Write(line);
                _writer.Flush();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or ObjectDisposedException)
        {
            _diagnostics.RecordWriteFailure();
        }
    }

    private void EnsureWriter(DateOnly date, int nextEntryBytes)
    {
        if (_activeDate != date) CloseWriter();
        if (_writer is not null && _stream!.Length + nextEntryBytes <= _maximumFileBytes) return;

        var firstIndex = _activeDate == date ? _activeIndex + 1 : 0;
        CloseWriter();
        for (var index = firstIndex; index < firstIndex + 1000; index++)
        {
            var path = LogPath(date, index);
            if (File.Exists(path))
            {
                EnsureNotReparsePoint(path);
                if (new FileInfo(path).Length + nextEntryBytes > _maximumFileBytes) continue;
            }

            _stream = new FileStream(path, FileMode.Append, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            _writer = new StreamWriter(_stream, new UTF8Encoding(false)) { AutoFlush = true };
            _activeDate = date;
            _activeIndex = index;
            PruneOldFiles();
            return;
        }
        throw new IOException("No available rolling log file slot remains for the current date.");
    }

    private string LogPath(DateOnly date, int index)
    {
        var suffix = index == 0 ? string.Empty : $"-{index:000}";
        return Path.Combine(_logRoot, $"service-{date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}{suffix}.log");
    }

    private void PruneOldFiles()
    {
        string[] paths;
        try { paths = Directory.GetFiles(_logRoot, "service-*.log"); }
        catch (IOException) { _diagnostics.RecordWriteFailure(); return; }
        var activePath = _stream?.Name;
        var oldFiles = paths
            .Where(path => !string.Equals(path, activePath,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
            .Skip(Math.Max(0, _retainedFileCount - 1)).ToArray();
        foreach (var path in oldFiles)
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _diagnostics.RecordWriteFailure();
            }
        }
    }

    private static void EnsureNotReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Log directory and files cannot be symbolic links or reparse points.");
    }

    private void CloseWriter()
    {
        if (_writer is null)
        {
            _stream = null;
            _activeDate = null;
            return;
        }
        try
        {
            _writer.Flush();
            _stream!.Flush(flushToDisk: true);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _diagnostics.RecordWriteFailure();
        }
        finally
        {
            try { _writer.Dispose(); }
            catch (IOException) { _diagnostics.RecordWriteFailure(); }
            _writer = null;
            _stream = null;
            _activeDate = null;
        }
    }

    private sealed class FileLogger(RollingFileLoggerProvider owner, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) owner.Write(categoryName, logLevel, eventId, state, exception, formatter);
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();
        public void Dispose() { }
    }
}
