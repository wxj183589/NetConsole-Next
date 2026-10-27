using Microsoft.Data.Sqlite;
using System.Net;

namespace NetConsole.Next.Service.Tests;

internal static class P2B5RailReferenceTests
{
    public static void Run()
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
