using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Net;
using Microsoft.Data.Sqlite;

namespace NetConsole.Next.Service.Tests;

internal static class WirelessTaskTests
{
    public static void Run()
    {
        var request = new WirelessTaskCreateRequest(
            new[] { "domain-1" },
            new[] { "display version", "display wlan ap all" }, 30_000);
        Assert(request.DomainIds!.Single() == "domain-1" && request.Commands!.Count == 2 && request.TimeoutMs == 30_000,
            "wireless task request keeps domain scope and read-only command plan");
        Assert(H3cCommandPolicy.IsAllowed("display wlan ap all") && !H3cCommandPolicy.IsAllowed("system-view"),
            "wireless tasks must use the existing read-only command allowlist");
        Assert(WirelessTaskService.IsSupportedDeviceType(" ac ") &&
            !WirelessTaskService.IsSupportedDeviceType("FIT_AP") &&
            !WirelessTaskService.IsSupportedDeviceType("SWITCH") &&
            !WirelessTaskService.IsSupportedDeviceType("NETWORK_DEVICE"),
            "wireless tasks require explicitly classified AC inventory targets");
        VerifyRetrySelection();
        VerifyReportExport();
        VerifyHistoryFiltersAndRevocation();
        Console.WriteLine("WIRELESS_TASK_CONTRACT_TESTS=5/5 PASS");
    }

    private static void VerifyRetrySelection()
    {
        var commands = new[]
        {
            new TaskCommandSnapshot(0, "display version", "SUCCEEDED", null, null, "SUCCEEDED", null, 1),
            new TaskCommandSnapshot(1, "display wlan ap all", "FAILED", "failure", null, "FAILED", null, 2)
        };
        Assert(H3cBatchTaskService.SelectRetryIndexes(commands, null).SequenceEqual([1]),
            "device retry defaults to failed commands only");
        var rejected = false;
        try { H3cBatchTaskService.SelectRetryIndexes(commands, [0]); }
        catch (InvalidOperationException) { rejected = true; }
        Assert(rejected, "successful commands cannot be selected for retry");
    }

    private static void VerifyReportExport()
    {
        var id = "wireless-plan-" + new string('a', 32);
        Assert(WirelessTaskReportBuilder.IsWirelessId(id) && !WirelessTaskReportBuilder.IsWirelessId("wireless-plan-not-valid"), "wireless identifiers must be exact");
        var item = new TaskPlanItemSnapshot(0, "profile-secret", "definition", "run", "FAILED", "password-secret C:\\private");
        var plan = new TaskPlanSnapshot(id, "H3C_SSH_READONLY", "FAILED", "actor", ["display version"], "claims", 10, 20, [item]);
        var command = new TaskCommandSnapshot(0, "display version", "FAILED", "password-secret", "C:\\private", "FAILED", null, 12);
        var retry = new TaskPlanRetrySnapshot("retry-id", 0, "run", "retry-run", [0], "actor", 30);
        var retryRun = new TaskRunSnapshot("retry-run", null, "H3C_SSH_READONLY", "FAILED", 30, 31, 40, 41,
            "failure", "retry-definition", null, null, null, null, "FAILED", null);
        var retryCommand = new TaskCommandSnapshot(0, "display version", "FAILED", "failure", null, null, null, null);
        var report = WirelessTaskReportBuilder.Build(new(plan, [new(item, null, [command])],
            [new(retry, retryRun, [retryCommand])]), new Dictionary<int, TaskScopeSnapshot>
            { [0] = new(1, "actor", "ac-id", "profile-secret", "frozen-site", 10, "claims") });
        Assert(report.Summary.SiteIds!.SequenceEqual(["frozen-site"]) &&
            report.Summary.AcDeviceIds!.SequenceEqual(["ac-id"]),
            "summary source identities come from creation scope even without published observations");
        using var archive = new ZipArchive(new MemoryStream(WirelessTaskReportBuilder.Export(report)), ZipArchiveMode.Read);
        var manifestEntry = archive.GetEntry("manifest.json")!;
        using var manifestStream = manifestEntry.Open();
        using var manifest = JsonDocument.Parse(manifestStream);
        Assert(manifest.RootElement.GetProperty("generatedMs").GetInt64() > 0 &&
            manifest.RootElement.GetProperty("source").GetString() == "TASK_PLAN_REPORT", "manifest identifies generation and source");
        foreach (var name in new[] { "task-summary.json", "ac-execution-results.json", "ap-resources.json", "ap-observations.json", "parser-summary.json", "failure-details.json" })
            Assert(archive.GetEntry(name) is not null, "export includes " + name);
        using (var retryStream = archive.GetEntry("retry-history.json")!.Open())
        using (var retryHistory = JsonDocument.Parse(retryStream))
            Assert(retryHistory.RootElement.GetProperty("status").GetString() == "SUPPORTED" &&
                retryHistory.RootElement.GetProperty("items").GetArrayLength() == 1 &&
                retryHistory.RootElement.GetProperty("items")[0].GetProperty("retryRunId").GetString() == "retry-run",
                "export includes persisted retry history");
        using (var manifestStreamAgain = archive.GetEntry("manifest.json")!.Open())
        using (var manifestAgain = JsonDocument.Parse(manifestStreamAgain))
            Assert(manifestAgain.RootElement.GetProperty("statistics").GetProperty("retryCount").GetInt32() == 1,
                "manifest reports retry count");
        foreach (var file in manifest.RootElement.GetProperty("files").EnumerateArray())
        {
            var entry = archive.GetEntry(file.GetProperty("name").GetString()!)!;
            using var input = entry.Open(); using var bytes = new MemoryStream(); input.CopyTo(bytes);
            var text = Encoding.UTF8.GetString(bytes.ToArray());
            Assert(!text.Contains("password-secret") && !text.Contains("profile-secret") && !text.Contains("C:\\private"), "export excludes secrets and paths");
            Assert(Convert.ToHexString(SHA256.HashData(bytes.ToArray())).Equals(file.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase), "manifest digest matches file");
        }
        Assert(report.Items.Single().DeviceId == "ac-id" && report.ObservationStatus == "NOT_PUBLISHED", "report retains device identity and truthful observation status");
    }

    private static void VerifyHistoryFiltersAndRevocation()
    {
        DirectoryInfo? repository = null;
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "tools", "workspace_paths.py")))
                { repository = directory; break; }
        if (repository is null) throw new InvalidOperationException("Repository root unavailable.");
        var root = Path.Combine(repository.Parent!.FullName, "test-data", "wireless-history-" + Guid.NewGuid().ToString("N"));
        BoundedCollectionScheduler? scheduler = null;
        try
        {
            var settings = new RuntimeSettings { DataRoot = root, WebPort = 18772, SyslogPort = 0,
                SyslogBind = IPAddress.Loopback, SyslogReceiveBufferBytes = 1024 * 1024 };
            var catalog = new Catalog(settings);
            catalog.Initialize();
            var scopes = new P2B5SiteScopeStore(settings);
            var authorizer = new TaskSiteScopeAuthorizer(catalog, scopes);
            scheduler = new(new NoConnectionFactory(), 1, 4, catalog, scopeAuthorizer: authorizer);
            var plans = new H3cBatchTaskService(catalog, authorizer, scopes, scheduler);
            var service = new WirelessTaskService(new(catalog), catalog, plans, scopes);
            var profileA = Guid.NewGuid().ToString("N");
            var profileB = Guid.NewGuid().ToString("N");
            foreach (var profile in new[] { profileA, profileB })
                catalog.CreateH3cDeviceProfile(new(profile, profile == profileA ? "192.0.2.41" : "192.0.2.42", 22, "fixture", "FIXTURE_REFERENCE",
                    string.Empty, true, 0, 0));
            Execute("INSERT INTO sites(id,name) VALUES('other','Other');" +
                $"UPDATE device_inventory SET site_id='other' WHERE terminal_profile_id='{profileB}';" +
                $"UPDATE collection_sources SET site_id='other' WHERE id=(SELECT source_id FROM h3c_device_profiles WHERE id='{profileB}');");
            var idA = "wireless-plan-" + new string('a', 32);
            var idB = "wireless-plan-" + new string('b', 32);
            var snapshotA = authorizer.CaptureDefinition(profileA, "local-api-token");
            var snapshotB = authorizer.CaptureDefinition(profileB, "local-api-token");
            foreach (var (id, profile, snapshot) in new[] { (idA, profileA, snapshotA), (idB, profileB, snapshotB) })
            {
                catalog.CreateTaskDefinition(id + ":d:0", "H3C_SSH_READONLY", profile, "display version",
                    TimeSpan.FromSeconds(2), scopeSnapshot: snapshot);
                catalog.CreateTaskPlan(id, "H3C_SSH_READONLY", "local-api-token", ["display version"], "GLOBAL",
                    [(0, profile, id + ":d:0")]);
            }
            catalog.FailTaskPlanItem(idA, 0, "fixture failure");
            Execute($"UPDATE task_plans SET created_ms=10 WHERE id='{idA}';UPDATE task_plans SET created_ms=20 WHERE id='{idB}';");
            Assert(service.List("local-api-token", 1).Single().Id == idB,
                "unfiltered task history retains newest-first limit");
            var filtered = service.List("local-api-token", 1, "local", snapshotA.DeviceId, "failed");
            Assert(filtered.Single().Id == idA && filtered.Single().SiteIds!.SequenceEqual(["local"]),
                "combined filters find an older matching task before applying result limit without facts");
            Assert(service.List("local-api-token", 200, "other", snapshotA.DeviceId).Count == 0 &&
                service.List("local-api-token", 200, acDeviceId: snapshotB.DeviceId).Single().Id == idB,
                "Site and frozen AC filters use exact identities and AND semantics");
            Execute($"UPDATE device_inventory SET enabled=0 WHERE terminal_profile_id='{profileB}'");
            Assert(service.List("local-api-token", 200).Single().Id == idA &&
                service.List("local-api-token", 200, "other", snapshotB.DeviceId).Count == 0,
                "revoked target is excluded by current scope before applying matching history filters");

            void Execute(string sql)
            {
                using var db = new SqliteConnection(new SqliteConnectionStringBuilder
                    { DataSource = Path.Combine(root, "catalog.db"), Pooling = false }.ToString());
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
        }
        finally
        {
            scheduler?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class NoConnectionFactory : IDeviceSessionFactory
    {
        public IDeviceSession Create(string target) => throw new InvalidOperationException("History reads must not connect.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
