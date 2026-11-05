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
        Console.WriteLine("CONFIG_BACKUP_TESTS=1/1 PASS");
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
