using NetConsole.Next.Service;

await SchedulerTests.RunAsync();

internal static class SchedulerTests
{
    public static async Task RunAsync()
    {
        await EnforcesQueueAndConcurrencyLimits();
        await CancelsQueuedAndRunningRequests();
        await TimesOutAndDisposesSession();
        await ReportsConnectionFailuresAndDisposesSession();
        RejectsInvalidRequests();
        Console.WriteLine("PASS: bounded collection scheduling, queue rejection, timeout, cancellation and session lifecycle");
    }

    private static async Task EnforcesQueueAndConcurrencyLimits()
    {
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeSessionFactory(target => target == "device-1"
            ? new FakeBehavior(async (command, token) =>
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(token);
                return $"echo:{command}";
            })
            : new FakeBehavior((command, _) => Task.FromResult($"echo:{command}")));

        await using var scheduler = new BoundedCollectionScheduler(factory, maxConcurrency: 1, queueCapacity: 1);
        Assert(scheduler.TrySchedule(Request("device-1", "display version"), out var firstId), "first task should be accepted");
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert(scheduler.TrySchedule(Request("device-2", "display interface brief"), out var queuedId), "one waiting task should be accepted");
        Assert(!scheduler.TrySchedule(Request("device-3", "display version"), out _), "full queue must reject instead of growing");
        Assert(scheduler.Status.Active == 1 && scheduler.Status.Queued == 1 && scheduler.Status.Rejected == 1, scheduler.Status.ToString()!);
        Assert(scheduler.Cancel(queuedId), "queued request should be cancellable");
        Assert((await WaitTerminal(scheduler, queuedId)).State == "CANCELLED", "queued request should become cancelled");

        releaseFirst.TrySetResult();
        var first = await WaitTerminal(scheduler, firstId);
        Assert(first.State == "SUCCEEDED" && first.Result == "echo:display version", first.ToString()!);
        await WaitUntil(() => scheduler.Status.Active == 0 && scheduler.Status.Queued == 0);
        Assert(factory.CreatedCount == 1, $"cancelled queued work must not open a device session (created {factory.CreatedCount})");
        Assert(factory.DisposedCount == 1, $"completed session should be disposed (disposed {factory.DisposedCount})");
        Assert(factory.MaximumActiveSessions == 1, $"concurrency exceeded one session ({factory.MaximumActiveSessions})");
    }

    private static async Task CancelsQueuedAndRunningRequests()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeSessionFactory(_ => new FakeBehavior(async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "unreachable";
        }));
        await using var scheduler = new BoundedCollectionScheduler(factory, maxConcurrency: 1, queueCapacity: 2);
        Assert(scheduler.TrySchedule(Request("device-cancel", "display version"), out var id), "cancellable request should be accepted");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert(scheduler.Cancel(id), "running request should be cancellable");
        var cancelled = await WaitTerminal(scheduler, id);
        Assert(cancelled.State == "CANCELLED", cancelled.ToString()!);
        await WaitUntil(() => scheduler.Status.Active == 0);
        Assert(factory.DisposedCount == 1, "cancelled session should be disposed");
    }

    private static async Task TimesOutAndDisposesSession()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeSessionFactory(_ => new FakeBehavior(async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "unreachable";
        }));
        await using var scheduler = new BoundedCollectionScheduler(factory, maxConcurrency: 1, queueCapacity: 1);
        Assert(scheduler.TrySchedule(new("device-timeout", "display version", TimeSpan.FromMilliseconds(150)), out var id), "timed request should be accepted");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var timedOut = await WaitTerminal(scheduler, id);
        Assert(timedOut.State == "TIMED_OUT", timedOut.ToString()!);
        Assert(timedOut.Error?.Contains("timeout", StringComparison.OrdinalIgnoreCase) == true, timedOut.ToString()!);
        Assert(factory.DisposedCount == 1, "timed-out session should be disposed");
    }

    private static async Task ReportsConnectionFailuresAndDisposesSession()
    {
        var factory = new FakeSessionFactory(_ => new FakeBehavior((_, _) => Task.FromResult("unused"), FailConnect: true));
        await using var scheduler = new BoundedCollectionScheduler(factory, maxConcurrency: 1, queueCapacity: 1);
        Assert(scheduler.TrySchedule(Request("device-connect-failure", "display version"), out var id), "connection request should be accepted");
        var failed = await WaitTerminal(scheduler, id);
        Assert(failed.State == "FAILED" && failed.Error == "IOException", failed.ToString()!);
        Assert(factory.DisposedCount == 1, "failed connection session should be disposed");
    }

    private static void RejectsInvalidRequests()
    {
        var factory = new FakeSessionFactory(_ => new FakeBehavior((_, _) => Task.FromResult("unused")));
        using var scheduler = new SynchronousSchedulerScope(factory);
        AssertThrows<ArgumentException>(() => scheduler.Scheduler.TrySchedule(new("device", "display version\nreboot", TimeSpan.FromSeconds(1)), out _));
        AssertThrows<ArgumentOutOfRangeException>(() => scheduler.Scheduler.TrySchedule(new("device", "display version", TimeSpan.FromMilliseconds(10)), out _));
    }

    private static DeviceCollectionRequest Request(string target, string command) => new(target, command, TimeSpan.FromSeconds(5));

    private static async Task<DeviceCollectionSnapshot> WaitTerminal(BoundedCollectionScheduler scheduler, string id)
    {
        DeviceCollectionSnapshot? snapshot = null;
        await WaitUntil(() =>
        {
            snapshot = scheduler.Get(id);
            return snapshot is not null && snapshot.State is "SUCCEEDED" or "FAILED" or "TIMED_OUT" or "CANCELLED";
        });
        return snapshot!;
    }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(10);
        }
        throw new TimeoutException("Condition was not reached in the test deadline.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertThrows<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class FakeSessionFactory(Func<string, FakeBehavior> behaviorFactory) : IDeviceSessionFactory
    {
        private int _active;
        private int _maximumActive;
        private int _created;
        private int _disposed;

        public int CreatedCount => Volatile.Read(ref _created);
        public int DisposedCount => Volatile.Read(ref _disposed);
        public int MaximumActiveSessions => Volatile.Read(ref _maximumActive);

        public IDeviceSession Create(string target)
        {
            Interlocked.Increment(ref _created);
            return new FakeSession(this, behaviorFactory(target));
        }

        private void Connected()
        {
            var active = Interlocked.Increment(ref _active);
            int current;
            while (active > (current = Volatile.Read(ref _maximumActive)) &&
                   Interlocked.CompareExchange(ref _maximumActive, active, current) != current) { }
        }

        private void Disconnected() => Interlocked.Decrement(ref _active);
        private void Disposed() => Interlocked.Increment(ref _disposed);

        private sealed class FakeSession(FakeSessionFactory owner, FakeBehavior behavior) : IDeviceSession
        {
            private bool _connected;

            public Task ConnectAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (behavior.FailConnect) throw new IOException("simulated connect failure");
                owner.Connected();
                _connected = true;
                return Task.CompletedTask;
            }

            public Task<string> ExecuteAsync(string command, CancellationToken cancellationToken)
            {
                if (!_connected) throw new InvalidOperationException("session is not connected");
                return behavior.Execute(command, cancellationToken);
            }

            public ValueTask DisposeAsync()
            {
                if (_connected) owner.Disconnected();
                owner.Disposed();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed record FakeBehavior(Func<string, CancellationToken, Task<string>> Execute, bool FailConnect = false);

    private sealed class SynchronousSchedulerScope(FakeSessionFactory factory) : IDisposable
    {
        public BoundedCollectionScheduler Scheduler { get; } = new(factory, 1, 1);
        public void Dispose() => Scheduler.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
