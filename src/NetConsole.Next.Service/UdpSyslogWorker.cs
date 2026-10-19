using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace NetConsole.Next.Service;

internal sealed class UdpSyslogWorker(
    RuntimeSettings settings,
    Catalog catalog,
    IngressStats stats,
    RuntimeStatus runtimeStatus,
    ILogger<UdpSyslogWorker> logger) : BackgroundService
{
    internal const int QueueCapacity = 2048;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
                    var datagram = await udp.ReceiveAsync(listenerCancellation.Token);
                    var receivedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    stats.MarkReceived(receivedMs);
                    var record = new SyslogPacket(receivedMs,
                        datagram.RemoteEndPoint.Address.ToString(), datagram.RemoteEndPoint.Port, datagram.Buffer);
                    stats.TryQueue(queue.Writer, record);
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
            runtimeStatus.SetCollection("faulted", ex.GetType().Name);
            logger.LogError(ex, "UDP Syslog collection failed.");
            throw;
        }
        finally
        {
            if (taskRunId is not null) catalog.EndTaskRun(taskRunId, failed ? "FAILED" : "STOPPED");
            if (!failed) runtimeStatus.SetCollection("stopped");
        }
    }

    private async Task WriteQueue(ChannelReader<SyslogPacket> queue)
    {
        RawSegmentWriter? segment = null;
        var drained = false;
        try
        {
            while (await queue.WaitToReadAsync())
            {
                var inBatch = 0;
                while (inBatch < 128 && queue.TryRead(out var datagram))
                {
                    stats.MarkDequeued();
                    segment ??= new RawSegmentWriter(catalog);
                    await segment.AppendAsync(datagram);
                    inBatch++;
                    if (segment.Bytes >= 4 * 1024 * 1024)
                    {
                        await segment.SealAsync();
                        segment = null;
                    }
                }
                if (inBatch == 0) continue;
                if (segment is not null) await segment.FlushAsync();
                stats.MarkFlushed(inBatch);
            }
            drained = true;
        }
        finally
        {
            if (segment is not null)
            {
                if (drained) await segment.SealAsync();
                else await segment.PreserveOpenAsync();
            }
        }
    }
}
