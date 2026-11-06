using Microsoft.Data.Sqlite;
using NetConsole.Next.Service;
using System.Net;
using System.Text.Json;

internal static class ConfigBackupTests
{
    public static async Task RunAsync()
    {
        using var fixture = new Fixture();
        var actor = new WebUserIdentity("local-api-token", "local-api-token", "SUPERADMIN", "test");
        var first = await fixture.Backup(actor);
        Assert(first.State == "SUCCEEDED" && first.Succeeded == 1, "configuration backup batch must complete");
        var firstSnapshot = fixture.Backups.List(fixture.Profile, P2B5SiteScopeDecision.Global).Single();
        Assert(firstSnapshot.Version == 1 && firstSnapshot.CanDownload && firstSnapshot.ByteCount > 0,
            "first backup must persist a downloadable versioned snapshot");
        var firstDownload = fixture.Backups.OpenDownload(firstSnapshot.Id, P2B5SiteScopeDecision.Global)
            ?? throw new InvalidOperationException("first backup download must open");
        using (var reader = new StreamReader(firstDownload.Stream))
            Assert(!reader.ReadToEnd().Contains("super-secret", StringComparison.Ordinal),
                "configuration backup must redact secret values before persistence");

        var forged = firstSnapshot with
        {
            Id = Guid.NewGuid().ToString("N"), DownloadName = "..\\..\\catalog.db", ByteCount = 1,
            Sha256 = new string('0', 64)
        };
        var manifestPath = Path.Combine(fixture.Root, "backup", "config", fixture.Profile, forged.Id + ".json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(forged, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert(fixture.Backups.OpenDownload(forged.Id, P2B5SiteScopeDecision.Global) is null,
            "forged backup manifests must not allow download path traversal");

        fixture.Factory.Output = "<H3C>sysname changed\ninterface GigabitEthernet1/0/1\n description changed\n</H3C>\n";
        var second = await fixture.Backup(actor);
        Assert(second.State == "SUCCEEDED" && second.Succeeded == 1, "second configuration backup must complete");
        var history = fixture.Backups.List(fixture.Profile, P2B5SiteScopeDecision.Global);
        Assert(history.Count == 2 && history.Any(item => item.Version == 2), "backup history must retain both versions");
        var latest = history.Single(item => item.Version == 2);
        var diff = fixture.Backups.Diff(firstSnapshot.Id, latest.Id, P2B5SiteScopeDecision.Global);
        Assert(diff is { Identical: false, AddedLines: > 0, RemovedLines: > 0 },
            "configuration diff must expose changed lines without returning full secrets");
        var download = fixture.Backups.OpenDownload(latest.Id, P2B5SiteScopeDecision.Global)
            ?? throw new InvalidOperationException("authorized backup download must open");
        download.Stream.Dispose();
        Assert(fixture.Catalog.QueryH3cFacts(fixture.Profile, history: true)
            .All(item => item.Kind != "command.display.current-configuration"),
            "configuration backups must not publish device configuration into the Facts UI projection");
        RejectUnsupportedTargets(actor);
        RejectManagedFitApTargets(actor);
        await VerifyLineDiffs(actor);
        Console.WriteLine("CONFIG_BACKUP_TESTS=6/6 PASS");
    }

    private static async Task VerifyLineDiffs(WebUserIdentity actor)
    {
        using var fixture = new Fixture();
        var failures = new List<string>();
        void Check(bool condition, string message)
        {
            if (!condition) failures.Add(message);
        }

        async Task<ConfigBackupDiff> Compare(string baseline, string target)
        {
            fixture.Factory.Output = baseline;
            var first = await fixture.Backup(actor);
            fixture.Factory.Output = target;
            var second = await fixture.Backup(actor);
            Assert(first.State == "SUCCEEDED" && second.State == "SUCCEEDED", "diff fixtures must back up successfully");
            return fixture.Backups.Diff(first.Items.Single().SnapshotId!, second.Items.Single().SnapshotId!,
                P2B5SiteScopeDecision.Global) ?? throw new InvalidOperationException("diff fixture must be readable");
        }

        var reordered = await Compare("alpha\nbeta\ngamma\n", "gamma\nbeta\nalpha\n");
        Check(!reordered.Identical && reordered.Added.SequenceEqual(new[] { "beta", "alpha" }) &&
            reordered.Removed.SequenceEqual(new[] { "alpha", "beta" }),
            "reordered lines must produce differences in their original sequence");

        var removedDuplicate = await Compare("head\nrepeat\nrepeat\ntail\n", "head\nrepeat\ntail\n");
        Check(!removedDuplicate.Identical && removedDuplicate.AddedLines == 0 && removedDuplicate.RemovedLines == 1 &&
            removedDuplicate.Removed.SequenceEqual(new[] { "repeat" }), "removing one duplicate occurrence must be visible");
        var addedDuplicates = await Compare("head\ntail\n", "head\nrepeat\nrepeat\ntail\n");
        Check(!addedDuplicates.Identical && addedDuplicates.AddedLines == 2 && addedDuplicates.RemovedLines == 0 &&
            addedDuplicates.Added.SequenceEqual(new[] { "repeat", "repeat" }), "added duplicate occurrences must not collapse");
        var same = await Compare("head\nrepeat\nrepeat\ntail\n", "head\nrepeat\nrepeat\ntail\n");
        Check(same.Identical && same.AddedLines == 0 && same.RemovedLines == 0 && same.ChangedLines == 0,
            "identical sequences with duplicates must remain identical");
        var replaced = await Compare("head\nbefore\ntail\n", "head\nafter\ntail\n");
        Check(replaced is { Identical: false, AddedLines: 1, RemovedLines: 1, ChangedLines: 1 },
            "equal-length replacements must count changed lines");

        var prefix = string.Concat(Enumerable.Repeat("x\n", 17_000));
        var lateChange = await Compare(prefix + "before\n", prefix + "after\n");
        Check(!lateChange.Identical && lateChange.Added.SequenceEqual(new[] { "after" }) &&
            lateChange.Removed.SequenceEqual(new[] { "before" }), "changes after line 16,384 must not be hidden");

        const int lineCount = 12_000;
        var baseline = string.Join('\n', Enumerable.Range(0, lineCount).Select(index => $"old-{index:D5}")) + "\n";
        var target = string.Join('\n', Enumerable.Range(0, lineCount).Select(index => $"new-{index:D5}")) + "\n";
        fixture.Factory.Output = baseline;
        var largeFirst = await fixture.Backup(actor);
        fixture.Factory.Output = target;
        var largeSecond = await fixture.Backup(actor);
        Assert(largeFirst.State == "SUCCEEDED" && largeSecond.State == "SUCCEEDED", "large diff fixtures must back up successfully");
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var large = fixture.Backups.Diff(largeFirst.Items.Single().SnapshotId!, largeSecond.Items.Single().SnapshotId!,
            P2B5SiteScopeDecision.Global) ?? throw new InvalidOperationException("large diff fixture must be readable");
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Check(large is { Identical: false, AddedLines: lineCount, RemovedLines: lineCount, ChangedLines: lineCount } &&
            large.Added.Count == 256 && large.Removed.Count == 256 &&
            large.Added.SequenceEqual(Enumerable.Range(0, 256).Select(index => $"new-{index:D5}")) &&
            large.Removed.SequenceEqual(Enumerable.Range(0, 256).Select(index => $"old-{index:D5}")),
            "large differences must retain total counts and cap each ordered preview at 256 lines");
        // A full 12,000 x 12,000 integer LCS table would allocate over 500 MiB.
        Check(allocatedBytes < 32 * 1024 * 1024, "large comparisons must use bounded memory instead of a full quadratic table");
        Assert(failures.Count == 0, string.Join("; ", failures));
    }

    private static void RejectUnsupportedTargets(WebUserIdentity actor)
    {
        using var fixture = new Fixture();
        var unsupported = Guid.NewGuid().ToString("N");
        fixture.Catalog.CreateH3cDeviceProfile(new(unsupported, "192.0.2.62", 22, "fixture", "FIXTURE_REFERENCE",
            string.Empty, true, 0, 0));
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(fixture.Root, "catalog.db"), Pooling = false
        }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "UPDATE device_inventory SET vendor=$vendor WHERE terminal_profile_id=$profile";
        command.Parameters.AddWithValue("$profile", unsupported);
        var vendor = command.Parameters.Add("$vendor", SqliteType.Text);
        foreach (var name in new[] { "Cisco", "Huawei", "ZTE", "unknown-vendor", "" })
        {
            vendor.Value = name;
            command.ExecuteNonQuery();
            try
            {
                fixture.Backups.Start(new[] { fixture.Profile, unsupported }, actor, P2B5SiteScopeDecision.Global);
                throw new InvalidOperationException("mixed-vendor backup must be rejected before scheduling");
            }
            catch (ConfigBackupException ex)
            {
                Assert(ex.Code == "CONFIG_BACKUP_TARGET_UNSUPPORTED" && ex.StatusCode == 409,
                    "unsupported vendors must return a specific compatibility error");
            }
        }
        var hiddenScope = new P2B5SiteScopeDecision("AUTHORIZED", false, new HashSet<string> { "another-site" }, "fixture");
        try
        {
            fixture.Backups.Start(new[] { unsupported }, actor, hiddenScope);
            throw new InvalidOperationException("hidden target must be rejected");
        }
        catch (ConfigBackupException ex)
        {
            Assert(ex.Code == "CONFIG_BACKUP_TARGET_NOT_VISIBLE" && ex.StatusCode == 404,
                "target visibility must be checked before vendor compatibility");
        }
        command.Parameters.Clear();
        command.CommandText = "SELECT COUNT(*) FROM task_definitions";
        Assert(Convert.ToInt64(command.ExecuteScalar()) == 0,
            "a rejected mixed batch must not create definitions or connect even its first H3C target");
    }

    private static void RejectManagedFitApTargets(WebUserIdentity actor)
    {
        using var fixture = new Fixture();
        var fitAp = Guid.NewGuid().ToString("N");
        fixture.Catalog.CreateH3cDeviceProfile(new(fitAp, "192.0.2.63", 22, "fixture", "FIXTURE_REFERENCE",
            string.Empty, true, 0, 0));
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(fixture.Root, "catalog.db"), Pooling = false
        }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "UPDATE device_inventory SET vendor='H3C',device_type=$type WHERE terminal_profile_id=$profile";
        command.Parameters.AddWithValue("$profile", fitAp);
        var type = command.Parameters.Add("$type", SqliteType.Text);
        foreach (var deviceType in new[] { "FIT_AP", "fit_ap", "FiT_aP" })
        {
            type.Value = deviceType;
            command.ExecuteNonQuery();
            foreach (var profiles in new[] { new[] { fitAp }, new[] { fixture.Profile, fitAp } })
            {
                try
                {
                    fixture.Backups.Start(profiles, actor, P2B5SiteScopeDecision.Global);
                    throw new InvalidOperationException("direct service calls must not back up a FIT-AP as a standalone H3C target");
                }
                catch (ConfigBackupException ex)
                {
                    Assert(ex.Code == "CONFIG_BACKUP_TARGET_UNSUPPORTED" && ex.StatusCode == 409,
                        "FIT-AP targets must return the unsupported-target compatibility error");
                }
            }
        }
        command.Parameters.Clear();
        command.CommandText = "SELECT COUNT(*) FROM task_definitions";
        Assert(Convert.ToInt64(command.ExecuteScalar()) == 0,
            "single and mixed FIT-AP requests must fail before creating any task definitions");
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "next-config-backup-" + Guid.NewGuid().ToString("N"));
        public string Profile { get; } = Guid.NewGuid().ToString("N");
        public RuntimeSettings Settings { get; }
        public Catalog Catalog { get; }
        public Factory Factory { get; } = new();
        public ConfigBackupService Backups { get; }
        private readonly BoundedCollectionScheduler _scheduler;

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Settings = new() { DataRoot = Root, WebPort = 18771, SyslogPort = 0,
                SyslogBind = IPAddress.Loopback, SyslogReceiveBufferBytes = 4 * 1024 * 1024 };
            Catalog = new(Settings);
            Catalog.Initialize();
            Catalog.CreateH3cDeviceProfile(new(Profile, "192.0.2.61", 22, "fixture", "FIXTURE_REFERENCE",
                string.Empty, true, 0, 0));
            var scopes = new P2B5SiteScopeStore(Settings);
            var authorizer = new TaskSiteScopeAuthorizer(Catalog, scopes);
            _scheduler = new BoundedCollectionScheduler(Factory, 2, 8, Catalog,
                resultSink: new H3cTaskRawResultSink(Catalog), scopeAuthorizer: authorizer);
            Backups = new ConfigBackupService(Catalog, _scheduler, authorizer, Settings);
        }

        public async Task<ConfigBackupBatch> Backup(WebUserIdentity actor)
        {
            var batch = Backups.Start(new[] { Profile }, actor, P2B5SiteScopeDecision.Global);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                var current = Backups.GetBatch(batch.Id, actor, P2B5SiteScopeDecision.Global) ?? throw new InvalidOperationException("backup batch disappeared");
                if (current.State is "SUCCEEDED" or "PARTIAL") return current;
                await Task.Delay(20, timeout.Token);
            }
        }

        public void Dispose()
        {
            _scheduler.DisposeAsync().AsTask().GetAwaiter().GetResult();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    private sealed class Factory : IDeviceSessionFactory
    {
        public string Output { get; set; } = "<H3C>sysname initial\nlocal-user admin password hash super-secret\ninterface GigabitEthernet1/0/1\n description initial\n</H3C>\n";
        public IDeviceSession Create(string target) => new Session(this);
        private sealed class Session(Factory owner) : IDeviceSession
        {
            public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public Task<string> ExecuteAsync(string command, CancellationToken cancellationToken) =>
                Task.FromResult(owner.Output);
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static void Assert(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
