using Microsoft.Data.Sqlite;
using NetConsole.Next.Service;
using System.Collections.Concurrent;
using System.Net;

internal static class ConcurrencyP1Tests
{
    public static async Task RunAsync()
    {
        await SameDeviceIsExclusiveDifferentDevicesRemainParallel();
        await FailedTaskCanBeRetriedAsANewRun();
        await InterruptedTaskRequiresExplicitResume();
        await SuccessfulTaskCannotBeRetried();
        await TaskRunLookupSurvivesRecentWindow();
        Console.WriteLine("CONCURRENCY_P1_TESTS=5/5 PASS");
    }

    private static async Task SameDeviceIsExclusiveDifferentDevicesRemainParallel()
    {
        var factory = new ProbeFactory();
        await using var scheduler = new BoundedCollectionScheduler(factory, 3, 6);
        var ids = new List<string>();
        foreach (var target in new[] { "device-a", "device-a", "device-b" })
        {
            Assert(scheduler.TrySchedule(new(target, "display version", TimeSpan.FromSeconds(3)), out var id), "task admission should succeed");
            ids.Add(id);
        }
        var results = await Task.WhenAll(ids.Select(id => WaitTerminal(scheduler, id)));
        Assert(results.All(item => item.State == "SUCCEEDED"), "all independent tasks should complete");
        Assert(factory.MaximumActiveFor("device-a") == 1, "same device tasks must never hold two sessions concurrently");
        Assert(factory.MaximumActiveTotal >= 2, "different devices should still run in parallel under the global cap");
    }

    private static async Task FailedTaskCanBeRetriedAsANewRun()
    {
        using var fixture = new PersistedFixture();
        var factory = new ProbeFactory { FailFirst = true };
        await using var scheduler = fixture.Scheduler(factory);
        var first = await fixture.Run(scheduler, "retry-definition");
        Assert(first.State == "FAILED", "first run must expose the transport failure");
        Assert(scheduler.TryRetryTaskRun(first.Id, out var retryId), "failed task should accept explicit retry");
        var retry = await WaitTerminal(scheduler, retryId);
        Assert(retry.State == "SUCCEEDED" && retry.Id != first.Id, "retry must be a new independent run");
        Assert(fixture.Catalog.RecentTaskRuns(10).Count(item => item.DefinitionId == "retry-definition") == 2,
            "original failed run and retry must both remain visible");
        Assert(fixture.AuditCount("TASK_RETRY_REQUESTED") == 1, "retry intent must be auditable without output or credentials");
    }

    private static async Task InterruptedTaskRequiresExplicitResume()
    {
        using var fixture = new PersistedFixture();
        var interrupted = fixture.Catalog.CreateTaskRun("resume-definition");
        fixture.Catalog.TransitionTaskRun(interrupted, "CONNECTING");
        fixture.Catalog.TransitionTaskRun(interrupted, "RUNNING");
        var restarted = new Catalog(fixture.Settings);
        restarted.Initialize();
        var factory = new ProbeFactory();
        await using var scheduler = new BoundedCollectionScheduler(factory, 1, 2, restarted);
        Assert(scheduler.TryResumeInterruptedTaskRun(interrupted, out var resumedId), "interrupted task should require and accept explicit resume");
        var resumed = await WaitTerminal(scheduler, resumedId);
        Assert(resumed.State == "SUCCEEDED" && resumed.Id != interrupted && factory.Created == 1,
            "resume should reconnect exactly once through a new run");
        Assert(restarted.GetTaskRun(interrupted)?.State == "INTERRUPTED", "interrupted history must remain unchanged");
        Assert(CountAudit(fixture.Root, "TASK_RESUME_REQUESTED") == 1, "resume intent must be auditable");
    }

    private static async Task SuccessfulTaskCannotBeRetried()
    {
        using var fixture = new PersistedFixture();
        var factory = new ProbeFactory();
        await using var scheduler = fixture.Scheduler(factory);
        var completed = await fixture.Run(scheduler, "success-definition");
        Assert(completed.State == "SUCCEEDED", "setup task should succeed");
        Assert(!scheduler.TryRetryTaskRun(completed.Id, out _), "a successful run cannot be retried as a failure");
        Assert(fixture.AuditCount("TASK_RETRY_REQUESTED") == 0, "successful retry denial should not create an accepted audit");
    }

    private static Task TaskRunLookupSurvivesRecentWindow()
    {
        using var fixture = new PersistedFixture();
        var created = Enumerable.Range(0, 125)
            .Select(_ => fixture.Catalog.CreateTaskRun("success-definition"))
            .ToArray();
        var recentIds = fixture.Catalog.RecentTaskRuns(100).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var olderId = created.First(id => !recentIds.Contains(id));
        Assert(fixture.Catalog.GetTaskRun(olderId)?.Id == olderId,
            "direct task lookup must find a run outside the recent task window");
        return Task.CompletedTask;
    }

    private static async Task<DeviceCollectionSnapshot> WaitTerminal(BoundedCollectionScheduler scheduler, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (true)
        {
            var snapshot = scheduler.Get(id) ?? throw new InvalidOperationException("scheduled task disappeared");
            if (snapshot.State is "SUCCEEDED" or "FAILED" or "TIMED_OUT" or "CANCELLED") return snapshot;
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class ProbeFactory : IDeviceSessionFactory
    {
        private readonly ConcurrentDictionary<string, int> _activeByTarget = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, int> _maximumByTarget = new(StringComparer.OrdinalIgnoreCase);
        private int _activeTotal;
        private int _maximumTotal;
        private int _created;
        private int _calls;
        public bool FailFirst { get; init; }
        public int Created => Volatile.Read(ref _created);
        public int MaximumActiveTotal => Volatile.Read(ref _maximumTotal);
        public int MaximumActiveFor(string target) => _maximumByTarget.TryGetValue(target, out var value) ? value : 0;
        public IDeviceSession Create(string target)
        {
            Interlocked.Increment(ref _created);
            return new Session(this, target);
        }
        private void Connected(string target)
        {
            var local = _activeByTarget.AddOrUpdate(target, 1, (_, value) => value + 1);
            _maximumByTarget.AddOrUpdate(target, local, (_, previous) => Math.Max(previous, local));
            var total = Interlocked.Increment(ref _activeTotal);
            while (total > Volatile.Read(ref _maximumTotal))
            {
                var current = Volatile.Read(ref _maximumTotal);
                if (Interlocked.CompareExchange(ref _maximumTotal, total, current) == current) break;
            }
        }
        private void Disconnected(string target)
        { _activeByTarget.AddOrUpdate(target, 0, (_, value) => Math.Max(0, value - 1)); Interlocked.Decrement(ref _activeTotal); }
        private sealed class Session(ProbeFactory owner, string target) : IDeviceSession
        {
            private bool _connected;
            public async Task ConnectAsync(CancellationToken cancellationToken)
            { owner.Connected(target); _connected = true; await Task.Delay(60, cancellationToken); }
            public async Task<string> ExecuteAsync(string command, CancellationToken cancellationToken)
            { if (owner.FailFirst && Interlocked.Increment(ref owner._calls) == 1) throw new IOException("synthetic first failure"); await Task.Delay(60, cancellationToken); return "ok"; }
            public ValueTask DisposeAsync() { if (_connected) owner.Disconnected(target); return ValueTask.CompletedTask; }
        }
    }

    private sealed class PersistedFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "next-concurrency-p1-" + Guid.NewGuid().ToString("N"));
        public RuntimeSettings Settings { get; }
        public Catalog Catalog { get; }
        public PersistedFixture()
        {
            Settings = new() { DataRoot = Root, WebPort = 18771, SyslogPort = 0, SyslogBind = IPAddress.Loopback, SyslogReceiveBufferBytes = 4 * 1024 * 1024 };
            Catalog = new(Settings); Catalog.Initialize();
            Catalog.CreateTaskDefinition("retry-definition", "SIMULATED_DEVICE_ECHO", "device-retry", "display version", TimeSpan.FromSeconds(3));
            Catalog.CreateTaskDefinition("resume-definition", "SIMULATED_DEVICE_ECHO", "device-resume", "display version", TimeSpan.FromSeconds(3));
            Catalog.CreateTaskDefinition("success-definition", "SIMULATED_DEVICE_ECHO", "device-success", "display version", TimeSpan.FromSeconds(3));
        }
        public BoundedCollectionScheduler Scheduler(ProbeFactory factory) => new(factory, 1, 4, Catalog);
        public async Task<DeviceCollectionSnapshot> Run(BoundedCollectionScheduler scheduler, string definition)
        {
            Assert(scheduler.TryScheduleDefinition(definition, out var id), "persisted definition should be admitted");
            return await WaitTerminal(scheduler, id);
        }
        public int AuditCount(string action) => CountAudit(Root, action);
        public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    private static int CountAudit(string root, string action)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db"), Pooling = false }.ToString());
        db.Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM audit_events WHERE action=$action"; command.Parameters.AddWithValue("$action", action); return Convert.ToInt32(command.ExecuteScalar());
    }
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
