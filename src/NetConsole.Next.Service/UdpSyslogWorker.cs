using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace NetConsole.Next.Service;

internal sealed class UdpSyslogWorker(
    RuntimeSettings settings, Catalog catalog, IngressStats stats, ILogger<UdpSyslogWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (settings.SyslogPort == 0)
        {
            logger.LogInformation("P0 UDP Syslog is disabled by default.");
            return;
        }

        using var udp = new UdpClient(new IPEndPoint(settings.SyslogBind, settings.SyslogPort));
        var queue = Channel.CreateBounded<SyslogPacket>(new BoundedChannelOptions(2048)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait
        });
        var sink = WriteQueue(queue.Reader);
        logger.LogInformation("P0 UDP Syslog listening on {Address}:{Port}", settings.SyslogBind, settings.SyslogPort);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var datagram = await udp.ReceiveAsync(stoppingToken);
                stats.MarkReceived();
                var record = new SyslogPacket(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    datagram.RemoteEndPoint.Address.ToString(), datagram.RemoteEndPoint.Port, datagram.Buffer);
                if (queue.Writer.TryWrite(record)) stats.MarkAccepted();
                else stats.MarkDropped();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            queue.Writer.TryComplete();
            await sink; // Drain and fsync accepted messages on cooperative shutdown.
        }
    }

    private async Task WriteQueue(ChannelReader<SyslogPacket> queue)
    {
        RawSegmentWriter? segment = null;
        try
        {
            while (await queue.WaitToReadAsync())
            {
                int inBatch = 0;
                while (inBatch < 128 && queue.TryRead(out var datagram))
                {
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
                if (segment != null) await segment.FlushAsync();
                stats.MarkFlushed(inBatch);
            }
        }
        finally
        {
            if (segment != null) await segment.SealAsync();
        }
    }
}
