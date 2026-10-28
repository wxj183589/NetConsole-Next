namespace NetConsole.Next.Service;

internal sealed record FlushLatencySnapshot(long Samples, int WindowSamples, double? P50Ms, double? P95Ms, double? P99Ms, double MaxMs);

internal sealed class LatencyWindow
{
    private readonly object _gate = new();
    private readonly double[] _values = new double[4096];
    private int _next, _count;
    private long _total;
    private double _max;

    public void Record(double milliseconds)
    {
        lock (_gate)
        {
            _values[_next] = milliseconds;
            _next = (_next + 1) % _values.Length;
            _count = Math.Min(_count + 1, _values.Length);
            _total++;
            _max = Math.Max(_max, milliseconds);
        }
    }

    public FlushLatencySnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                var values = _values.Take(_count).Order().ToArray();
                return new(_total, _count, Percentile(values, .50), Percentile(values, .95), Percentile(values, .99), _max);
            }
        }
    }

    private static double? Percentile(double[] values, double ratio) => values.Length == 0
        ? null : values[Math.Clamp((int)Math.Ceiling(values.Length * ratio) - 1, 0, values.Length - 1)];
}

// Application counters only. UDP kernel drops are not covered by these counters.
internal sealed class IngressStats
{
    private readonly int _queueCapacity;
    private readonly object _queueGate = new();
    private long _received, _accepted, _dropped, _flushed, _queueDepth, _queueHighWatermark, _queueBytes, _queueBytesHighWatermark, _lastReceivedMs, _lastPersistedMs;
    private int _requestedSocketReceiveBufferBytes, _effectiveSocketReceiveBufferBytes;
    private readonly LatencyWindow _rawSyncLatency = new();
    private readonly LatencyWindow _segmentSealLatency = new();
    private readonly LatencyWindow _catalogWriteLatency = new();
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
    public long QueueBytes => Interlocked.Read(ref _queueBytes);
    public long QueueBytesHighWatermark => Interlocked.Read(ref _queueBytesHighWatermark);
    public int RequestedSocketReceiveBufferBytes => Volatile.Read(ref _requestedSocketReceiveBufferBytes);
    public int EffectiveSocketReceiveBufferBytes => Volatile.Read(ref _effectiveSocketReceiveBufferBytes);
    public long LastReceivedMs => Interlocked.Read(ref _lastReceivedMs);
    public long LastPersistedMs => Interlocked.Read(ref _lastPersistedMs);

    public void SetSocketReceiveBuffer(int requested, int effective)
    {
        Volatile.Write(ref _requestedSocketReceiveBufferBytes, requested);
        Volatile.Write(ref _effectiveSocketReceiveBufferBytes, effective);
    }

    public void RecordRawFlushLatency(double elapsedMs)
    {
        _rawSyncLatency.Record(elapsedMs);
    }

    public void RecordSegmentSealLatency(double elapsedMs) => _segmentSealLatency.Record(elapsedMs);
    public void RecordCatalogWriteLatency(double elapsedMs) => _catalogWriteLatency.Record(elapsedMs);
    public FlushLatencySnapshot RawFlushLatency => _rawSyncLatency.Snapshot;
    public FlushLatencySnapshot SegmentSealLatency => _segmentSealLatency.Snapshot;
    public FlushLatencySnapshot CatalogWriteLatency => _catalogWriteLatency.Snapshot;

    public void MarkReceived(long atMs)
    {
        Interlocked.Increment(ref _received);
        Interlocked.Exchange(ref _lastReceivedMs, atMs);
    }

    public void MarkAccepted() => Interlocked.Increment(ref _accepted);
    public void MarkDropped() => Interlocked.Increment(ref _dropped);
    public void MarkDequeued(int payloadBytes)
    {
        lock (_queueGate)
        {
            Interlocked.Decrement(ref _queueDepth);
            Interlocked.Add(ref _queueBytes, -payloadBytes);
        }
    }
    public void MarkFlushed(int count)
    {
        Interlocked.Add(ref _flushed, count);
        Interlocked.Exchange(ref _lastPersistedMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public bool TryQueue(System.Threading.Channels.ChannelWriter<SyslogPacket> writer, SyslogPacket packet,
        long maximumQueuedBytes = 32 * 1024 * 1024)
    {
        lock (_queueGate)
        {
            var depth = Interlocked.Read(ref _queueDepth);
            var bytes = Interlocked.Read(ref _queueBytes);
            if (depth >= _queueCapacity || packet.Payload.Length > maximumQueuedBytes - bytes || !writer.TryWrite(packet))
            {
                MarkDropped();
                return false;
            }
            Interlocked.Increment(ref _queueDepth);
            var currentBytes = Interlocked.Add(ref _queueBytes, packet.Payload.Length);
            Interlocked.Increment(ref _accepted);
            UpdateHighWatermark(ref _queueHighWatermark, depth + 1);
            UpdateHighWatermark(ref _queueBytesHighWatermark, currentBytes);
            return true;
        }
    }

    private static void UpdateHighWatermark(ref long watermark, long value)
    {
        var current = Interlocked.Read(ref watermark);
        while (value > current && Interlocked.CompareExchange(ref watermark, value, current) != current)
            current = Interlocked.Read(ref watermark);
    }

}
