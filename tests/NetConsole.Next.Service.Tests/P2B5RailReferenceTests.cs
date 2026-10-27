using Microsoft.Data.Sqlite;
using System.Net;

namespace NetConsole.Next.Service.Tests;

internal static class P2B5RailReferenceTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "netconsole-next-p2b5-rail-reference-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "catalog.db");
        var previousV13 = Environment.GetEnvironmentVariable(P2B2MigrationContext.ExplicitMigrationEnvironmentVariable);
        var previousV14 = Environment.GetEnvironmentVariable(P2B4MigrationContext.AllowedEnvironmentVariable);
        var previousAuth = Environment.GetEnvironmentVariable(P2B4MigrationContext.TestAuthEnvironmentVariable);
        var previousAuthorized = Environment.GetEnvironmentVariable(P2B4WriteFeatureGate.MigrationAuthorizedEnvironmentVariable);
        var previousWrite = Environment.GetEnvironmentVariable(P2B4WriteFeatureGate.WriteEnvironmentVariable);
        var previousFeature = Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_P2B_ENABLED");
        var previousRail = Environment.GetEnvironmentVariable(P2B5RailReferenceMigrationContext.ExplicitMigrationEnvironmentVariable);
        try
        {
            var settings = new RuntimeSettings { DataRoot = root, WebPort = 18771, SyslogPort = 0, SyslogBind = IPAddress.Loopback, SyslogReceiveBufferBytes = 65536 };
            new Catalog(settings).Initialize();
            Environment.SetEnvironmentVariable(P2B2MigrationContext.ExplicitMigrationEnvironmentVariable, "1");
            using (var connection = Open(databasePath)) P2B2SchemaV13Migration.Apply(connection, P2B2MigrationContext.ForIsolatedTest(databasePath));
            Environment.SetEnvironmentVariable(P2B4MigrationContext.AllowedEnvironmentVariable, "1");
            Environment.SetEnvironmentVariable(P2B4MigrationContext.TestAuthEnvironmentVariable, "1");
            Environment.SetEnvironmentVariable(P2B4WriteFeatureGate.MigrationAuthorizedEnvironmentVariable, "1");
            Environment.SetEnvironmentVariable(P2B4WriteFeatureGate.WriteEnvironmentVariable, "1");
            Environment.SetEnvironmentVariable("NETCONSOLE_NEXT_P2B_ENABLED", "1");
            using (var connection = Open(databasePath)) P2B4SchemaV14Migration.Apply(connection, P2B4MigrationContext.ForIsolatedTest(databasePath, root));
            Environment.SetEnvironmentVariable(P2B5RailReferenceMigrationContext.ExplicitMigrationEnvironmentVariable, "1");
            using (var connection = Open(databasePath))
            {
                var result = P2B5RailReferenceMigration.Apply(connection, P2B5RailReferenceMigrationContext.ForIsolatedTest(databasePath, root));
                Assert(!result.AlreadyApplied && result.BaseSchemaVersion == 14, "rail reference migration must be additive to v14");
            }
            var service = new P2B5RailReferenceService(settings);
            var actor = new WebUserIdentity("root", "superadmin", "SUPERADMIN", "isolated");
            var request = Fixture("unit-1", 1, "line-1");
            var preview = service.Preview(actor, request);
            Assert(preview.State == "PREVIEWED" && preview.Preview.NewCount == 5 && preview.Preview.ConflictCount == 0, "rail fixture preview must be explicit and conflict-free");
            var confirmed = service.Confirm(actor, preview.Id);
            Assert(confirmed.State == "SUCCEEDED", "rail fixture confirm must persist only after preview");
            Assert(service.ListEntities("line-1", "STATION").Count == 2, "line/station relation fixture must preserve the many-to-many reference set");
            var hashA = service.Preview(actor, Fixture("hash-a", 1, "hash-scope"));
            var hashB = service.Preview(actor, Fixture("hash-b", 1, "hash-scope"));
            Assert(hashA.SourceHash == hashB.SourceHash,
                "the same normalized rail source must have the same SourceHash across idempotency keys");
            var revisionTwo = service.Preview(actor, FixtureWithPrefix("revision-two", 2, "revision-scope", "v2-"));
            Assert(service.Confirm(actor, revisionTwo.Id).State == "SUCCEEDED", "the newer source revision must be confirmable");
            var staleDifferentIds = service.Preview(actor, FixtureWithPrefix("revision-one-old", 1, "revision-scope", "old-"));
            Assert(staleDifferentIds.State == "CONFLICT" && staleDifferentIds.Preview.Conflicts.Any(item => item.StartsWith("STALE_SOURCE_REVISION:", StringComparison.Ordinal)),
                "an older revision with different stable IDs must not replace the authoritative source graph");
            var concurrentBase = service.Preview(actor, FixtureWithPrefix("concurrent-base", 1, "concurrent-scope", "c1-"));
            Assert(service.Confirm(actor, concurrentBase.Id).State == "SUCCEEDED", "the concurrent authority fixture must establish its base revision");
            var concurrentRevisionThree = service.Preview(actor, FixtureWithPrefix("concurrent-three", 3, "concurrent-scope", "c3-"));
            var concurrentRevisionTwo = service.Preview(actor, FixtureWithPrefix("concurrent-two", 2, "concurrent-scope", "c2-"));
            var confirmEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseConfirm = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pauseFirstConfirm = 1;
            RailReferenceTestHooks.BeforeConfirmTransaction = () =>
            {
                if (Interlocked.Exchange(ref pauseFirstConfirm, 0) != 1) return;
                confirmEntered.TrySetResult(true);
                releaseConfirm.Task.GetAwaiter().GetResult();
            };
            try
            {
                var staleConfirm = Task.Run(() => service.Confirm(actor, concurrentRevisionTwo.Id));
                await confirmEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert(service.Confirm(actor, concurrentRevisionThree.Id).State == "SUCCEEDED",
                    "the higher rail source revision must commit while the older confirm is paused");
                releaseConfirm.TrySetResult(true);
                P2B5RailReferenceException? staleError = null;
                try { await staleConfirm; }
                catch (P2B5RailReferenceException exception) { staleError = exception; }
                Assert(staleError?.Code == "RAIL_IMPORT_STALE_PREVIEW",
                    $"the older concurrent confirm must be rejected after revision three commits: {staleError?.Code}");
            }
            finally
            {
                releaseConfirm.TrySetResult(true);
                RailReferenceTestHooks.BeforeConfirmTransaction = null;
            }
            var concurrentEntities = service.ListEntities("concurrent-scope", null);
            Assert(concurrentEntities.Count == 5 && concurrentEntities.All(entity => entity.SourceRevision == 3 && entity.Id.StartsWith("c3-", StringComparison.Ordinal)),
                "a rejected older concurrent confirm must not partially replace the newer entity graph");
            var unknown = service.Preview(actor, Fixture("unit-unknown", 1, "line-1") with { Relations = request.Relations!.Append(new P2B5RailRelationInput("LINE_STATION", "line-1", "station-missing")).ToArray() });
            Assert(unknown.State == "CONFLICT" && unknown.Preview.UnknownReferences.Count == 1, "unknown stable references must be rejected as a preview conflict");
            Console.WriteLine("PASS: P2B5 rail reference isolated migration, explicit stable IDs, preview/confirm, hierarchy and conflict guard");
        }
        finally
        {
            Environment.SetEnvironmentVariable(P2B2MigrationContext.ExplicitMigrationEnvironmentVariable, previousV13);
            Environment.SetEnvironmentVariable(P2B4MigrationContext.AllowedEnvironmentVariable, previousV14);
            Environment.SetEnvironmentVariable(P2B4MigrationContext.TestAuthEnvironmentVariable, previousAuth);
            Environment.SetEnvironmentVariable(P2B4WriteFeatureGate.MigrationAuthorizedEnvironmentVariable, previousAuthorized);
            Environment.SetEnvironmentVariable(P2B4WriteFeatureGate.WriteEnvironmentVariable, previousWrite);
            Environment.SetEnvironmentVariable("NETCONSOLE_NEXT_P2B_ENABLED", previousFeature);
            Environment.SetEnvironmentVariable(P2B5RailReferenceMigrationContext.ExplicitMigrationEnvironmentVariable, previousRail);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static P2B5RailReferenceImportRequest Fixture(string key, long revision, string scope) => new(key, "synthetic-rail", revision, null, scope, "1", "2026-10-27T00:00:00Z", "2026-10-27T00:00:01Z",
        new[] { new P2B5RailEntityInput("line-1", "LINE", "Line 1"), new("section-1", "SECTION", "Section 1"), new("station-a", "STATION", "Station A"), new("station-b", "STATION", "Station B"), new("site-1", "SITE", "Site 1") },
        new[] { new P2B5RailRelationInput("LINE_SECTION", "line-1", "section-1"), new("SECTION_STATION", "section-1", "station-a"), new("SECTION_STATION", "section-1", "station-b"), new("STATION_SITE", "station-a", "site-1"), new("STATION_SITE", "station-b", "site-1"), new("LINE_STATION", "line-1", "station-a"), new("LINE_STATION", "line-1", "station-b") });

    private static P2B5RailReferenceImportRequest FixtureWithPrefix(string key, long revision, string scope, string prefix)
    {
        var line = prefix + "line-1";
        var section = prefix + "section-1";
        var stationA = prefix + "station-a";
        var stationB = prefix + "station-b";
        var site = prefix + "site-1";
        return new(key, "synthetic-rail", revision, null, scope, "1", "2026-10-27T00:00:00Z", "2026-10-27T00:00:01Z",
            new[] { new P2B5RailEntityInput(line, "LINE", "Line 1"), new(section, "SECTION", "Section 1"), new(stationA, "STATION", "Station A"), new(stationB, "STATION", "Station B"), new(site, "SITE", "Site 1") },
            new[] { new P2B5RailRelationInput("LINE_SECTION", line, section), new("SECTION_STATION", section, stationA), new("SECTION_STATION", section, stationB), new("STATION_SITE", stationA, site), new("STATION_SITE", stationB, site), new("LINE_STATION", line, stationA), new("LINE_STATION", line, stationB) });
    }

    private static SqliteConnection Open(string path) { var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); connection.Open(); return connection; }
    private static void Assert(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }
}

internal static class P2B5RailReferenceMigrationChild
{
    public static Task RunAsync(string[] arguments)
    {
        if (arguments.Length != 2) throw new ArgumentException("Expected: --p2b5-rail-reference-migrate <database-path> <data-root>.");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(arguments[0]), Pooling = false }.ToString());
        connection.Open();
        var result = P2B5RailReferenceMigration.Apply(connection, P2B5RailReferenceMigrationContext.ForIsolatedTest(arguments[0], arguments[1]));
        Console.WriteLine($"P2B5RailReferenceMigration: AlreadyApplied = {result.AlreadyApplied}, BaseSchemaVersion = {result.BaseSchemaVersion}");
        return Task.CompletedTask;
    }
}
