namespace NetConsole.Next.Service;

// Application counters only. UDP kernel drops are NOT covered by this P0 prototype.
internal sealed class IngressStats
{
    private long _received, _accepted, _dropped, _flushed;
    public long Received => Interlocked.Read(ref _received);
    public long Accepted => Interlocked.Read(ref _accepted);
    public long Dropped => Interlocked.Read(ref _dropped);
    public long Flushed => Interlocked.Read(ref _flushed);
    public void MarkReceived() => Interlocked.Increment(ref _received);
    public void MarkAccepted() => Interlocked.Increment(ref _accepted);
    public void MarkDropped() => Interlocked.Increment(ref _dropped);
    public void MarkFlushed(int count) => Interlocked.Add(ref _flushed, count);
}
