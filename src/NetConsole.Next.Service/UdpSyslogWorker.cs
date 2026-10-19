using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using System.Diagnostics;

namespace NetConsole.Next.Service;

internal sealed class UdpSyslogWorker(
    RuntimeSettings settings,
    Catalog catalog,
    IRawSegmentWriterFactory writerFactory,
    IngressStats stats,
    RuntimeStatus runtimeStatus,
    ILogger<UdpSyslogWorker> logger) : BackgroundService
{
    // The independent byte budget remains the hard payload-memory bound. This count limit
    // lets small datagrams ride out the measured sub-second to ~1 s segment-seal stalls.
    internal const int QueueCapacity = 8192;
    internal const long QueueByteCapacity = 32 * 1024 * 1024;
    internal const int FlushBatchRecords = 256;
    internal const long FlushBatchBytes = 256 * 1024;
    internal static readonly TimeSpan MaximumUnflushedDelay = TimeSpan.FromMilliseconds(100);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!catalog.Available || !catalog.RawWriteAvailable || !runtimeStatus.CollectionAllowed)
        {
            runtimeStatus.SetCollection("faulted", catalog.StorageFailure ?? "Raw storage is unavailable.");
            runtimeStatus.SetFaulted(catalog.StorageFailure ?? "Raw storage is unavailable; collection is disabled.");
            logger.LogCritical("UDP collection is disabled because Raw storage is not safe for new writes.");
            return;
        }

        if (settings.SyslogPort == 0)
        {
            runtimeStatus.SetCollection("disabled");
            logger.LogInformation("UDP Syslog is disabled by default.");
            return;
        }

        runtimeStatus.SetCollection("starting");
        string? taskRunId = null;
        var failed = false;
        try
        {
            taskRunId = catalog.StartTaskRun("syslog-local", "SYSLOG_UDP");
            using var udp = new UdpClient(new IPEndPoint(settings.SyslogBind, settings.SyslogPort));
            udp.Client.ReceiveBufferSize = settings.SyslogReceiveBufferBytes;
            stats.SetSocketReceiveBuffer(settings.SyslogReceiveBufferBytes, udp.Client.ReceiveBufferSize);
            var queue = Channel.CreateBounded<SyslogPacket>(new BoundedChannelOptions(QueueCapacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });
            var sink = WriteQueue(queue.Reader);
            using var listenerCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _ = sink.ContinueWith(_ => listenerCancellation.Cancel(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            runtimeStatus.SetCollection("listening");
            logger.LogInformation("UDP Syslog listening on {Address}:{Port}", settings.SyslogBind, settings.SyslogPort);
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    if (!runtimeStatus.CollectionAllowed) break;
                    using var receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(listenerCancellation.Token);
                    receiveTimeout.CancelAfter(TimeSpan.FromMilliseconds(500));
                    UdpReceiveResult datagram;
                    try { datagram = await udp.ReceiveAsync(receiveTimeout.Token); }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested &&
                                                               !listenerCancellation.IsCancellationRequested)
                    {
                        continue;
                    }
                    var receivedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    stats.MarkReceived(receivedMs);
                    var record = new SyslogPacket(receivedMs,
                        datagram.RemoteEndPoint.Address.ToString(), datagram.RemoteEndPoint.Port, datagram.Buffer);
                    stats.TryQueue(queue.Writer, record, QueueByteCapacity);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Host is stopping; queued packets are drained and synced below.
            }
            finally
            {
                runtimeStatus.SetCollection("stopping");
                queue.Writer.TryComplete();
                try
                {
                    await sink;
                }
                catch (Exception ex)
                {
                    failed = true;
                    runtimeStatus.SetCollection("faulted", ex.GetType().Name);
                    logger.LogError(ex, "Raw writer failed while draining the Syslog queue.");
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host stop.
        }
        catch (Exception ex)
        {
            failed = true;
            runtimeStatus.SetFaulted($"RAW_OR_CATALOG_WRITE_FAILED: {ex.GetType().Name}");
            logger.LogError(ex, "UDP Syslog collection failed.");
        }
        finally
        {
            if (taskRunId is not null)
            {
                try { catalog.EndTaskRun(taskRunId, failed ? "FAILED" : "STOPPED"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
                {
                    runtimeStatus.SetFaulted($"SQLITE_WRITE_FAILED: {ex.GetType().Name}");
                    logger.LogCritical(ex, "Could not persist the Syslog task terminal state.");
                }
            }
            if (!failed && runtimeStatus.CollectionAllowed) runtimeStatus.SetCollection("stopped");
        }
    }

    internal static TimeSpan RemainingFlushDelay(long pendingSinceTimestamp)
    {
        var remaining = MaximumUnflushedDelay - Stopwatch.GetElapsedTime(pendingSinceTimestamp);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private async Task WriteQueue(ChannelReader<SyslogPacket> queue)
    {
        RawSegmentWriter? segment = null;
        var drained = false;
        var pendingRecords = 0;
        long pendingBytes = 0;
        var pendingSince = 0L;
        try
        {
            while (true)
            {
                while (pendingRecords < FlushBatchRecords && queue.TryRead(out var datagram))
                {
                    stats.MarkDequeued(datagram.Payload.Length);
                    segment ??= writerFactory.Create(catalog);
                    var beforeBytes = segment.Bytes;
                    await segment.AppendAsync(datagram);
                    pendingRecords++;
                    pendingBytes += segment.Bytes - beforeBytes;
                    if (pendingSince == 0) pendingSince = Stopwatch.GetTimestamp();
                    if (segment.Bytes >= 4 * 1024 * 1024)
                    {
                        await FlushPendingAsync(sealSegment: true);
                        break;
                    }
                    if (pendingBytes >= FlushBatchBytes) break;
                }

                if (pendingRecords > 0 && (pendingRecords >= FlushBatchRecords || pendingBytes >= FlushBatchBytes ||
                    Stopwatch.GetElapsedTime(pendingSince) >= MaximumUnflushedDelay))
                {
                    await FlushPendingAsync(sealSegment: false);
                    continue;
                }

                if (pendingRecords == 0)
                {
                    if (!await queue.WaitToReadAsync()) break;
                    continue;
                }

                using var waitTimeout = new CancellationTokenSource(RemainingFlushDelay(pendingSince));
                var ready = queue.WaitToReadAsync(waitTimeout.Token).AsTask();
                try
                {
                    if (!await ready)
                    {
                        await FlushPendingAsync(sealSegment: true);
                        break;
                    }
                }
                catch (OperationCanceledException) when (waitTimeout.IsCancellationRequested)
                {
                    await FlushPendingAsync(sealSegment: false);
                }
            }
            drained = true;

            if (pendingRecords > 0)
                await FlushPendingAsync(sealSegment: true);
            else if (segment is not null)
            {
                await segment.SealAsync();
                stats.RecordSegmentSealLatency(segment.LastSealDurationMs);
                stats.RecordCatalogWriteLatency(segment.LastCatalogWriteDurationMs);
                segment = null;
            }
        }
        finally
        {
            if (segment is not null)
            {
                if (drained) await segment.SealAsync();
                else await segment.PreserveOpenAsync();
            }
        }

        async Task FlushPendingAsync(bool sealSegment)
        {
            if (segment is null || pendingRecords == 0) return;
            if (sealSegment)
            {
                await segment.SealAsync();
                stats.RecordRawFlushLatency(segment.LastSyncDurationMs);
                stats.RecordSegmentSealLatency(segment.LastSealDurationMs);
                stats.RecordCatalogWriteLatency(segment.LastCatalogWriteDurationMs);
                segment = null;
            }
            else
            {
                await segment.FlushAsync();
                stats.RecordRawFlushLatency(segment.LastSyncDurationMs);
            }
            stats.MarkFlushed(pendingRecords);
            pendingRecords = 0;
            pendingBytes = 0;
            pendingSince = 0;
        }
    }
}
