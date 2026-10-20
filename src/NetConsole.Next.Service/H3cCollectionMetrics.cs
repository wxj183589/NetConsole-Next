using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;

namespace NetConsole.Next.Service;

internal sealed record H3cCollectionMetricsSnapshot(int ActiveSshSessions,
    long ConnectAuthenticationCount, double ConnectAuthenticationTotalMs,
    long CommandCount, double CommandTotalMs,
    long RawCommitCount, double RawFlushSyncTotalMs, double RawSealTotalMs,
    long SqliteCommitCount, double SqliteCommitTotalMs,
    long CompletedTaskCount, double TaskTotalMs,
    IReadOnlyDictionary<string, long> FailuresByCategory);

internal static class H3cCollectionMetrics
{
    private static readonly ConcurrentDictionary<string, long> Failures = new(StringComparer.Ordinal);
    private static int _activeSessions;
    private static long _connectCount, _connectTicks;
    private static long _commandCount, _commandTicks;
    private static long _rawCommitCount, _rawFlushMicroseconds, _rawSealMicroseconds;
    private static long _sqliteCount, _sqliteTicks;
    private static long _taskCount, _taskTicks;

    public static void SessionOpened() => Interlocked.Increment(ref _activeSessions);
    public static void SessionClosed() => Interlocked.Decrement(ref _activeSessions);
    public static void RecordConnectAuthentication(TimeSpan elapsed)
    {
        Interlocked.Increment(ref _connectCount);
        Interlocked.Add(ref _connectTicks, elapsed.Ticks);
    }
    public static void RecordCommand(TimeSpan elapsed)
    {
        Interlocked.Increment(ref _commandCount);
        Interlocked.Add(ref _commandTicks, elapsed.Ticks);
    }
    public static void RecordRawCommit(double flushSyncMs, double sealMs)
    {
        Interlocked.Increment(ref _rawCommitCount);
        Interlocked.Add(ref _rawFlushMicroseconds, ToMicroseconds(flushSyncMs));
        Interlocked.Add(ref _rawSealMicroseconds, ToMicroseconds(sealMs));
    }
    public static void RecordSqliteCommit(TimeSpan elapsed)
    {
        Interlocked.Increment(ref _sqliteCount);
        Interlocked.Add(ref _sqliteTicks, elapsed.Ticks);
    }
    public static void RecordTask(TimeSpan elapsed)
    {
        Interlocked.Increment(ref _taskCount);
        Interlocked.Add(ref _taskTicks, elapsed.Ticks);
    }
    public static void RecordFailure(Exception exception) => Failures.AddOrUpdate(Classify(exception), 1, (_, count) => count + 1);

    public static H3cCollectionMetricsSnapshot Snapshot() => new(
        Volatile.Read(ref _activeSessions), Interlocked.Read(ref _connectCount), Milliseconds(Interlocked.Read(ref _connectTicks)),
        Interlocked.Read(ref _commandCount), Milliseconds(Interlocked.Read(ref _commandTicks)),
        Interlocked.Read(ref _rawCommitCount), Interlocked.Read(ref _rawFlushMicroseconds) / 1000d,
        Interlocked.Read(ref _rawSealMicroseconds) / 1000d,
        Interlocked.Read(ref _sqliteCount), Milliseconds(Interlocked.Read(ref _sqliteTicks)),
        Interlocked.Read(ref _taskCount), Milliseconds(Interlocked.Read(ref _taskTicks)),
        new Dictionary<string, long>(Failures, StringComparer.Ordinal));

    private static long ToMicroseconds(double milliseconds) => (long)Math.Round(milliseconds * 1000d);
    private static double Milliseconds(long ticks) => TimeSpan.FromTicks(ticks).TotalMilliseconds;

    private static string Classify(Exception exception) => exception switch
    {
        H3cCredentialUnavailableException => "CREDENTIAL_UNAVAILABLE",
        H3cHostKeyMismatchException => "HOST_KEY_MISMATCH",
        H3cHostKeyMissingException => "HOST_KEY_MISSING",
        H3cAuthenticationFailedException => "AUTHENTICATION_FAILED",
        H3cConnectionTimeoutException => "CONNECT_TIMEOUT",
        TimeoutException => "COMMAND_TIMEOUT",
        H3cUnsupportedAlgorithmException => "ALGORITHM_UNSUPPORTED",
        H3cOutputLimitException => "OUTPUT_LIMIT",
        H3cPartialOutputException partial => partial.FailureCode,
        H3cPaginationDetectedException => "PAGINATION_DETECTED",
        H3cInvalidEncodingException => "INVALID_UTF8",
        H3cCommandRejectedException => "COMMAND_REJECTED",
        OperationCanceledException => "CANCELLED_OR_TIMED_OUT",
        DbException => "SQLITE_FAILURE",
        UnauthorizedAccessException => "ACCESS_DENIED",
        IOException => "SSH_OR_RAW_IO_FAILURE",
        _ => "OTHER_FAILURE"
    };
}
