using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using NetConsole.Next.Service;

var tests = new (string Name, Func<Task> Run)[]
{
    ("normal seal and repeated seal", NormalSealAsync),
    ("catalog failure after rename and retry", CatalogFailureRetryAsync),
    ("worker finally repeated seal", WorkerFinallyAsync),
    ("concurrent seal is serialized", ConcurrentSealAsync),
    ("preserved segment rejects further writes", PreservedSegmentStateAsync),
    ("recovery crash after rename remains incomplete", RecoveryCrashAfterRenameAsync),
    ("recovery crash after tail repair remains incomplete", RecoveryCrashAfterTailRepairAsync),
    ("recovery catalog failure is retryable", RecoveryCatalogFailureAsync),
    ("empty and binary boundaries", BoundaryAsync),
    ("4MiB segment rolling and unique catalog rows", SegmentRollingAsync)
};

var failed = 0;
foreach (var (name, run) in tests)
{
    try
    {
        await run();
        Console.WriteLine($"PASS: {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"FAIL: {name}: {ex}");
    }
}

return failed == 0 ? 0 : 1;

static async Task NormalSealAsync()
{
    await WithRootAsync(async root =>
    {
        var catalog = CreateCatalog(root);
        var writer = new RawSegmentWriter(catalog);
        await writer.AppendAsync(new SyslogPacket(1000, "127.0.0.1", 5514, new byte[] { 0, 0xff, 0x0d, 0x0a }));
        await writer.SealAsync();
        await writer.SealAsync();

        var files = RawFiles(catalog);
        Assert(files.Length == 1, "normal seal should publish one file");
        Assert(!OpenFiles(catalog).Any(), "normal seal should leave no .open file");
        var rows = Rows(root);
        Assert(rows.Count == 1 && rows[0].RecordCount == 1, "normal seal should create one catalog row");
        Assert(rows[0].RelativePath.Replace('/', Path.DirectorySeparatorChar) is var relative
            && Path.GetFullPath(Path.Combine(root, relative)) == Path.GetFullPath(files[0]),
            "catalog path should point at the published file");
        Assert(rows[0].Sha256 == Sha256(files[0]), "catalog checksum should match disk");
        Assert(rows[0].ByteCount == new FileInfo(files[0]).Length,
            "catalog byte_count must equal the published file length on the active newline convention");
        await AssertThrowsAsync<InvalidOperationException>(() => writer.AppendAsync(
            new SyslogPacket(1001, "127.0.0.1", 5514, new byte[] { 1 })));
    });
}

static async Task CatalogFailureRetryAsync()
{
    await WithRootAsync(async root =>
    {
        var catalog = CreateCatalog(root);
        var attempts = 0;
        var writer = new RawSegmentWriter(catalog.RawRoot,
            (relative, count, first, last, sha, bytes) =>
            {
                attempts++;
                if (attempts == 1) throw new InvalidOperationException("injected catalog failure");
                catalog.RegisterFinalizedSegment(relative, count, first, last, sha, bytes);
            });
        var payload = new byte[] { 0, 0xff, 0x0d, 0x0a, 0x7f };
        await writer.AppendAsync(new SyslogPacket(2000, "127.0.0.1", 5514, payload));

        await AssertThrowsAsync<InvalidOperationException>(() => writer.SealAsync());
        var published = RawFiles(catalog);
        Assert(published.Length == 1, "catalog failure must retain one published file");
        Assert(!OpenFiles(catalog).Any(), "catalog failure must not recreate .open");
        var bytesBeforeRetry = File.ReadAllBytes(published[0]);
        var hashBeforeRetry = Sha256(published[0]);
        Assert(Rows(root).Count == 0, "failed catalog registration must not leave a row");

        await writer.SealAsync();
        await writer.SealAsync();
        var rows = Rows(root);
        Assert(attempts == 2, "retry must register once after the injected failure");
        Assert(rows.Count == 1, "retry and repeated seal must leave one row");
        Assert(bytesBeforeRetry.SequenceEqual(File.ReadAllBytes(published[0])), "retry must not change Raw bytes");
        Assert(hashBeforeRetry == Sha256(published[0]), "retry must preserve the checksum");
        Assert(rows[0].Sha256 == hashBeforeRetry && rows[0].ByteCount == bytesBeforeRetry.Length,
            "retry metadata must match the published file");
        catalog.RegisterFinalizedSegment(rows[0].RelativePath, rows[0].RecordCount, 2000, 2000,
            rows[0].Sha256, rows[0].ByteCount);
        Assert(Rows(root).Count == 1, "same catalog metadata must be idempotent");
        AssertThrows<InvalidOperationException>(() => catalog.RegisterFinalizedSegment(
            rows[0].RelativePath, rows[0].RecordCount + 1, 2000, 2000, rows[0].Sha256, rows[0].ByteCount));

        const string rollbackPath = "raw/syslog/rollback-injection.ndjson";
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "catalog.db"),
            Pooling = false
        }.ToString()))
        {
            db.Open();
            using var trigger = db.CreateCommand();
            trigger.CommandText = "CREATE TRIGGER fail_raw_insert AFTER INSERT ON raw_segments BEGIN SELECT RAISE(ABORT, 'injected rollback'); END;";
            trigger.ExecuteNonQuery();
        }

        AssertThrows<SqliteException>(() => catalog.RegisterFinalizedSegment(
            rollbackPath, 1, 2100, 2100, rows[0].Sha256, rows[0].ByteCount));
        Assert(!Rows(root).Any(row => row.RelativePath == rollbackPath),
            "an aborted Catalog transaction must not leave a partial row");

        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "catalog.db"),
            Pooling = false
        }.ToString()))
        {
            db.Open();
            using var trigger = db.CreateCommand();
            trigger.CommandText = "DROP TRIGGER fail_raw_insert";
            trigger.ExecuteNonQuery();
        }

        catalog.RegisterFinalizedSegment(rollbackPath, 1, 2100, 2100, rows[0].Sha256, rows[0].ByteCount);
        Assert(Rows(root).Count == 2, "a later Catalog retry must succeed after rollback");
    });
}

static async Task WorkerFinallyAsync()
{
    await WithRootAsync(async root =>
    {
        var catalog = CreateCatalog(root);
        var attempts = 0;
        var writer = new RawSegmentWriter(catalog.RawRoot,
            (relative, count, first, last, sha, bytes) =>
            {
                attempts++;
                if (attempts == 1) throw new InvalidOperationException("injected catalog failure");
                catalog.RegisterFinalizedSegment(relative, count, first, last, sha, bytes);
            });
        await writer.AppendAsync(new SyslogPacket(3000, "127.0.0.1", 5514, new byte[] { 3, 4, 5 }));

        Exception? observed = null;
        try
        {
            try
            {
                await writer.SealAsync();
            }
            finally
            {
                await writer.SealAsync();
            }
        }
        catch (Exception ex)
        {
            observed = ex;
        }

        Assert(observed is InvalidOperationException && observed.Message == "injected catalog failure",
            "worker-style finally must preserve the diagnostic catalog error, not emit FileNotFoundException");
        Assert(attempts == 2 && Rows(root).Count == 1, "worker-style finally must complete the retry");
        Assert(RawFiles(catalog).Length == 1 && !OpenFiles(catalog).Any(),
            "worker-style finally must retain one published Raw file");
    });
}

static async Task ConcurrentSealAsync()
{
    await WithRootAsync(async root =>
    {
        var catalog = CreateCatalog(root);
        var writer = new RawSegmentWriter(catalog);
        var payload = Enumerable.Range(0, 64 * 1024).Select(index => (byte)(index % 251)).ToArray();
        for (var index = 0; index < 32; index++)
            await writer.AppendAsync(new SyslogPacket(3500 + index, "127.0.0.1", 5514, payload));

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seals = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            await writer.SealAsync();
        })).ToArray();
        start.SetResult();
        await Task.WhenAll(seals);

        Assert(RawFiles(catalog).Length == 1 && !OpenFiles(catalog).Any(),
            "concurrent SealAsync calls must publish one file without a stale .open path");
        Assert(Rows(root).Count == 1 && Rows(root)[0].RecordCount == 32,
            "concurrent SealAsync calls must create one catalog row");
    });
}

static async Task PreservedSegmentStateAsync()
{
    await WithRootAsync(async root =>
    {
        var catalog = CreateCatalog(root);
        var writer = new RawSegmentWriter(catalog);
        await writer.AppendAsync(new SyslogPacket(3600, "127.0.0.1", 5514, Encoding.UTF8.GetBytes("preserved")));
        await writer.FlushAsync();
        await writer.PreserveOpenAsync();

        var open = OpenFiles(catalog).Single();
        var bytes = File.ReadAllBytes(open);
        await AssertThrowsAsync<InvalidOperationException>(() => writer.AppendAsync(
            new SyslogPacket(3601, "127.0.0.1", 5514, Encoding.UTF8.GetBytes("rejected"))));
        await AssertThrowsAsync<InvalidOperationException>(() => writer.FlushAsync());
        await AssertThrowsAsync<InvalidOperationException>(() => writer.SealAsync());
        Assert(bytes.SequenceEqual(File.ReadAllBytes(open)), "closed Raw writer must not mutate preserved bytes");
        Assert(Rows(root).Count == 0, "preserving an open segment must not create a FINALIZED row");

        var recovery = catalog.RecoverRawSegments();
        Assert(recovery.RecoveredOpenFiles == 1 && catalog.GetRawCounts().Incomplete == 1 &&
               catalog.GetRawCounts().Finalized == 0, $"preserved segment recovery was unsafe: {recovery}");
    });
}

static async Task RecoveryCrashAfterRenameAsync()
{
    await WithRootAsync(async root =>
    {
        var catalog = CreateCatalog(root);
        var writer = new RawSegmentWriter(catalog);
        await writer.AppendAsync(new SyslogPacket(3700, "127.0.0.1", 5514, Encoding.UTF8.GetBytes("rename-crash")));
        await writer.FlushAsync();
        await writer.PreserveOpenAsync();
        catalog.RecoveryAfterRenameHook = _ => throw new SimulatedRecoveryCrashException();
        AssertThrows<SimulatedRecoveryCrashException>(() => catalog.RecoverRawSegments());
        Assert(!OpenFiles(catalog).Any() && RawFiles(catalog).Length == 1,
            "simulated crash after rename must leave the published Raw bytes in place");
        Assert(Rows(root).Count == 0 && RecoveryIntentCount(root) == 1,
            "rename crash must leave a durable pending recovery intent without a Catalog row");

        var restarted = CreateCatalog(root);
        var recovery = restarted.RecoverRawSegments();
        Assert(recovery.FailedFiles == 0 && restarted.GetRawCounts().Finalized == 0 &&
               restarted.GetRawCounts().Incomplete == 1 && RecoveryIntentCount(root) == 0,
            $"second startup must conservatively finish the recovery as INCOMPLETE: {recovery}");
        var repeated = restarted.RecoverRawSegments();
        Assert(repeated.FailedFiles == 0 && Rows(root).Count == 1 && restarted.GetRawCounts().Finalized == 0,
            "repeated recovery must remain idempotent and must not promote the segment");
    });
}

static async Task RecoveryCrashAfterTailRepairAsync()
{
    await WithRootAsync(async root =>
    {
        var catalog = CreateCatalog(root);
        var writer = new RawSegmentWriter(catalog);
        await writer.AppendAsync(new SyslogPacket(3800, "127.0.0.1", 5514, Encoding.UTF8.GetBytes("valid-prefix")));
        await writer.FlushAsync();
        await writer.PreserveOpenAsync();
        var open = OpenFiles(catalog).Single();
        var validLength = new FileInfo(open).Length;
        await File.AppendAllTextAsync(open, "{\"broken\":\n");
        catalog.RecoveryBeforeRenameHook = _ => throw new SimulatedRecoveryCrashException();
        AssertThrows<SimulatedRecoveryCrashException>(() => catalog.RecoverRawSegments());
        Assert(OpenFiles(catalog).Length == 1 && new FileInfo(open).Length == validLength && Rows(root).Count == 0,
            "crash after tail repair must retain the repaired .open and avoid Catalog finalization");
        Assert(Directory.GetFiles(Path.Combine(root, "recovery-evidence"), "*.tail.bin").Length == 1,
            "tail repair must retain the discarded bytes as evidence");

        var restarted = CreateCatalog(root);
        var recovery = restarted.RecoverRawSegments();
        Assert(recovery.FailedFiles == 0 && restarted.GetRawCounts().Finalized == 0 &&
               restarted.GetRawCounts().Incomplete == 1 && RecoveryIntentCount(root) == 0,
            $"recovery after a tail-repair crash must remain incomplete: {recovery}");
    });
}

static async Task RecoveryCatalogFailureAsync()
{
    await WithRootAsync(async root =>
    {
        var catalog = CreateCatalog(root);
        var writer = new RawSegmentWriter(catalog);
        await writer.AppendAsync(new SyslogPacket(3900, "127.0.0.1", 5514, Encoding.UTF8.GetBytes("sqlite-failure")));
        await writer.FlushAsync();
        await writer.PreserveOpenAsync();
        using (var db = OpenDb(root))
        using (var trigger = db.CreateCommand())
        {
            trigger.CommandText = "CREATE TRIGGER fail_recovery_insert BEFORE INSERT ON raw_segments BEGIN SELECT RAISE(ABORT, 'injected recovery failure'); END";
            trigger.ExecuteNonQuery();
        }

        var first = catalog.RecoverRawSegments();
        Assert(first.FailedFiles == 1 && !OpenFiles(catalog).Any() && RawFiles(catalog).Length == 1 &&
               Rows(root).Count == 0 && RecoveryIntentCount(root) == 1,
            $"SQLite failure after rename must retain intent and Raw bytes: {first}");
        using (var db = OpenDb(root))
        using (var drop = db.CreateCommand())
        {
            drop.CommandText = "DROP TRIGGER fail_recovery_insert";
            drop.ExecuteNonQuery();
        }

        var restarted = CreateCatalog(root);
        var second = restarted.RecoverRawSegments();
        Assert(second.FailedFiles == 0 && restarted.GetRawCounts().Finalized == 0 &&
               restarted.GetRawCounts().Incomplete == 1 && RecoveryIntentCount(root) == 0 && Rows(root).Count == 1,
            $"SQLite recovery retry must reconcile exactly once: {second}");
    });
}

static async Task BoundaryAsync()
{
    await WithRootAsync(async root =>
    {
        var catalog = CreateCatalog(root);
        var writer = new RawSegmentWriter(catalog);
        await writer.SealAsync();
        var empty = Rows(root);
        Assert(empty.Count == 1 && empty[0].RecordCount == 0 && empty[0].ByteCount == 0,
            "empty segments must seal with zero records and bytes");

        var binaryWriter = new RawSegmentWriter(catalog);
        await binaryWriter.AppendAsync(new SyslogPacket(4000, "127.0.0.1", 5514,
            new byte[] { 0, 1, 2, 0xff, 0x0d, 0x0a }));
        await binaryWriter.AppendAsync(new SyslogPacket(4001, "127.0.0.1", 5514, Array.Empty<byte>()));
        await binaryWriter.SealAsync();
        Assert(RawFiles(catalog).Length == 2 && Rows(root).Sum(row => row.RecordCount) == 2,
            "single and multiple binary Syslog records must remain bounded and indexed");
    });
}

static async Task SegmentRollingAsync()
{
    await WithRootAsync(async root =>
    {
        var catalog = CreateCatalog(root);
        RawSegmentWriter? segment = null;
        var totalRecords = 70;
        var payload = Enumerable.Range(0, 60000).Select(index => (byte)(index % 256)).ToArray();
        for (var index = 0; index < totalRecords; index++)
        {
            segment ??= new RawSegmentWriter(catalog);
            await segment.AppendAsync(new SyslogPacket(5000 + index, "127.0.0.1", 5514, payload));
            if (segment.Bytes < 4 * 1024 * 1024) continue;
            await segment.SealAsync();
            segment = null;
        }
        if (segment != null) await segment.SealAsync();

        var files = RawFiles(catalog);
        var rows = Rows(root);
        Assert(files.Length >= 2, "4MiB rolling must publish multiple segments");
        Assert(rows.Count == files.Length, "each rolled segment must have one catalog row");
        Assert(rows.Sum(row => row.RecordCount) == totalRecords, "rolled record count must be preserved");
        Assert(rows.Select(row => row.RelativePath).Distinct(StringComparer.Ordinal).Count() == rows.Count,
            "rolled segments must not create duplicate catalog identities");
        Assert(!OpenFiles(catalog).Any(), "rolled segments must not leave .open files");
    });
}

static Catalog CreateCatalog(string root)
{
    var settings = new RuntimeSettings
    {
        DataRoot = root,
        WebPort = 18771,
        SyslogPort = 0,
        SyslogBind = IPAddress.Loopback
    };
    var catalog = new Catalog(settings);
    catalog.Initialize();
    return catalog;
}

static async Task WithRootAsync(Func<string, Task> test)
{
    var root = Path.Combine(Path.GetTempPath(), "netconsole-next-raw-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        await test(root);
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

static string[] RawFiles(Catalog catalog) => Directory.Exists(catalog.RawRoot)
    ? Directory.GetFiles(catalog.RawRoot, "*.ndjson", SearchOption.AllDirectories)
    : Array.Empty<string>();

static string[] OpenFiles(Catalog catalog) => Directory.Exists(catalog.RawRoot)
    ? Directory.GetFiles(catalog.RawRoot, "*.open", SearchOption.AllDirectories)
    : Array.Empty<string>();

static string Sha256(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
}

static SqliteConnection OpenDb(string root)
{
    var db = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(root, "catalog.db"),
        Pooling = false
    }.ToString());
    db.Open();
    return db;
}

static int RecoveryIntentCount(string root)
{
    using var db = OpenDb(root);
    using var command = db.CreateCommand();
    command.CommandText = "SELECT COUNT(*) FROM raw_recovery_intents";
    return Convert.ToInt32(command.ExecuteScalar());
}

static List<Row> Rows(string root)
{
    using var db = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(root, "catalog.db"),
        Mode = SqliteOpenMode.ReadOnly,
        Pooling = false
    }.ToString());
    db.Open();
    using var command = db.CreateCommand();
    command.CommandText = @"SELECT relative_path,record_count,byte_count,sha256
        FROM raw_segments ORDER BY relative_path";
    using var reader = command.ExecuteReader();
    var rows = new List<Row>();
    while (reader.Read())
        rows.Add(new Row(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3)));
    return rows;
}

static async Task AssertThrowsAsync<TException>(Func<Task> action) where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static void AssertThrows<TException>(Action action) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

record Row(string RelativePath, long RecordCount, long ByteCount, string Sha256);

sealed class SimulatedRecoveryCrashException : Exception { }
