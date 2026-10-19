namespace NetConsole.Next.Service;

internal sealed record FlushLatencySnapshot(long Samples, double? P50Ms, double? P95Ms, double MaxMs);

// Application counters only. UDP kernel drops are not covered by these counters.
internal sealed class IngressStats
{
    private readonly int _queueCapacity;
    private long _received, _accepted, _dropped, _flushed, _queueDepth, _queueHighWatermark, _lastReceivedMs, _lastPersistedMs;
    private readonly long[] _flushLatencyBuckets = new long[9];
    private long _maxFlushLatencyMicros;
    public IngressStats(int queueCapacity = UdpSyslogWorker.QueueCapacity)
    {
        if (queueCapacity < 1) throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        _queueCapacity = queueCapacity;
    }

    public long Received => Interlocked.Read(ref _received);
    public long Accepted => Interlocked.Read(ref _accepted);
    public long Dropped => Interlocked.Read(ref _dropped);
    public long Flushed => Interlocked.Read(ref _flushed);
    public long QueueDepth => Math.Min(Interlocked.Read(ref _queueDepth), _queueCapacity);
    public long QueueHighWatermark => Math.Min(Interlocked.Read(ref _queueHighWatermark), _queueCapacity);
    public long LastReceivedMs => Interlocked.Read(ref _lastReceivedMs);
    public long LastPersistedMs => Interlocked.Read(ref _lastPersistedMs);

    public void RecordRawFlushLatency(double elapsedMs)
    {
        var bucket = elapsedMs switch
        {
            <= 1 => 0, <= 2 => 1, <= 5 => 2, <= 10 => 3, <= 25 => 4,
            <= 50 => 5, <= 100 => 6, <= 500 => 7, _ => 8
        };
        Interlocked.Increment(ref _flushLatencyBuckets[bucket]);
        var micros = (long)Math.Max(0, elapsedMs * 1000);
        var current = Interlocked.Read(ref _maxFlushLatencyMicros);
        while (micros > current && Interlocked.CompareExchange(ref _maxFlushLatencyMicros, micros, current) != current)
            current = Interlocked.Read(ref _maxFlushLatencyMicros);
    }

    public FlushLatencySnapshot RawFlushLatency
    {
        get
        {
            var counts = _flushLatencyBuckets.Select(bucket => Interlocked.Read(ref bucket)).ToArray();
            var total = counts.Sum();
            return new(total, Percentile(counts, total, .50), Percentile(counts, total, .95),
                Interlocked.Read(ref _maxFlushLatencyMicros) / 1000d);
        }
    }

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
        // The counter is incremented before TryWrite to prevent a fast reader from
        // decrementing first. A failed/full write can briefly make it exceed capacity.
        var depth = Math.Min(Interlocked.Read(ref _queueDepth), _queueCapacity);
        var high = Interlocked.Read(ref _queueHighWatermark);
        while (depth > high && Interlocked.CompareExchange(ref _queueHighWatermark, depth, high) != high)
            high = Interlocked.Read(ref _queueHighWatermark);
        return true;
    }

    private static double? Percentile(long[] counts, long total, double percentile)
    {
        if (total == 0) return null;
        var threshold = (long)Math.Ceiling(total * percentile);
        long observed = 0;
        double[] upperBounds = [1, 2, 5, 10, 25, 50, 100, 500, double.PositiveInfinity];
        for (var i = 0; i < counts.Length; i++)
        {
            observed += counts[i];
            if (observed >= threshold) return double.IsPositiveInfinity(upperBounds[i]) ? null : upperBounds[i];
        }
        return null;
    }
}
