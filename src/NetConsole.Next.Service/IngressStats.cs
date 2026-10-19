namespace NetConsole.Next.Service;

// Application counters only. UDP kernel drops are not covered by these counters.
internal sealed class IngressStats
{
    private long _received, _accepted, _dropped, _flushed, _queueDepth, _lastReceivedMs, _lastPersistedMs;
    public long Received => Interlocked.Read(ref _received);
    public long Accepted => Interlocked.Read(ref _accepted);
    public long Dropped => Interlocked.Read(ref _dropped);
    public long Flushed => Interlocked.Read(ref _flushed);
    public long QueueDepth => Interlocked.Read(ref _queueDepth);
    public long LastReceivedMs => Interlocked.Read(ref _lastReceivedMs);
    public long LastPersistedMs => Interlocked.Read(ref _lastPersistedMs);

    public void MarkReceived(long atMs)
    {
        Interlocked.Increment(ref _received);
        Interlocked.Exchange(ref _lastReceivedMs, atMs);
    }

    public void MarkAccepted() => Interlocked.Increment(ref _accepted);
    public void MarkDropped() => Interlocked.Increment(ref _dropped);
    public void MarkDequeued() => Interlocked.Decrement(ref _queueDepth);
    public void MarkFlushed(int count)
    {
        Interlocked.Add(ref _flushed, count);
        Interlocked.Exchange(ref _lastPersistedMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public bool TryQueue(System.Threading.Channels.ChannelWriter<SyslogPacket> writer, SyslogPacket packet)
    {
        Interlocked.Increment(ref _queueDepth);
        if (!writer.TryWrite(packet))
        {
            Interlocked.Decrement(ref _queueDepth);
            MarkDropped();
            return false;
        }
        MarkAccepted();
        return true;
    }
}
