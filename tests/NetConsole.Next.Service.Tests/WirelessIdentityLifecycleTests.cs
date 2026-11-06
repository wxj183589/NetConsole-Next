using System.Net;
using Microsoft.Data.Sqlite;

namespace NetConsole.Next.Service.Tests;

internal static class WirelessIdentityLifecycleTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "nc-lc-" + Guid.NewGuid().ToString("N"));
        var previous = new Dictionary<string, string?>
        {
            [P2BFeatureGate.EnvironmentVariableName] = Environment.GetEnvironmentVariable(P2BFeatureGate.EnvironmentVariableName),
            [P2B4WriteFeatureGate.WriteEnvironmentVariable] = Environment.GetEnvironmentVariable(P2B4WriteFeatureGate.WriteEnvironmentVariable),
            [P2B4WriteFeatureGate.TestAuthEnvironmentVariable] = Environment.GetEnvironmentVariable(P2B4WriteFeatureGate.TestAuthEnvironmentVariable),
            [P2B4WriteFeatureGate.MigrationAuthorizedEnvironmentVariable] = Environment.GetEnvironmentVariable(P2B4WriteFeatureGate.MigrationAuthorizedEnvironmentVariable)
        };
        try
        {
            Directory.CreateDirectory(root);
            foreach (var key in previous.Keys) Environment.SetEnvironmentVariable(key, "1");
            var settings = new RuntimeSettings
            {
                DataRoot = root, WebPort = 18771, SyslogPort = 0,
                SyslogBind = IPAddress.Loopback, SyslogReceiveBufferBytes = 65536
            };
            var catalog = new Catalog(settings);
            catalog.Initialize();
            var actor = new WebUserIdentity("user-1", "superadmin", "SUPERADMIN", "session-1");
            var resource = Resource("resource-1", "CANDIDATE", "SERIAL-1", "AA:BB:CC:00:00:01");
            var review = WirelessIdentityContract.Describe(resource);
            var service = new WirelessIdentityLifecycleService(settings, catalog);
            var scope = P2B5SiteScopeDecision.Global;

            var accepted = service.Decide(actor,
                new(resource.Id, review.IdentityRevision, "ACCEPT", "verified against two exact observations", null, review.EvidenceRefs),
                review, new[] { resource }, scope);
            Assert(accepted.State == WirelessIdentityLifecycleStates.Accepted && accepted.Revision != review.IdentityRevision,
                "accepted identity must receive a new optimistic revision");
            Assert(accepted.History?.Count == 1 && accepted.History[0].FromState == WirelessIdentityLifecycleStates.PendingReview,
                "accepted identity must retain its original state transition");
            using var auditDb = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db"), Pooling = false }.ToString());
            auditDb.Open();
            using var auditCommand = auditDb.CreateCommand();
            auditCommand.CommandText = "SELECT COUNT(*) FROM audit_events WHERE action='WIRELESS_IDENTITY_DECISION'";
            Assert(Convert.ToInt32(auditCommand.ExecuteScalar()) == 1,
                "identity decision must write an audit record without credentials");

            AssertThrows("IDENTITY_STALE_REVISION", () => service.Decide(actor,
                new(resource.Id, review.IdentityRevision, "REJECT", "stale client", null, review.EvidenceRefs),
                review, new[] { resource }, scope));

            var restarted = new WirelessIdentityLifecycleService(settings, catalog);
            var persisted = restarted.Get(resource.Id, review, scope);
            Assert(persisted.State == WirelessIdentityLifecycleStates.Accepted && persisted.History?.Count == 1,
                "identity decision and history must survive service restart");

            var conflicting = Resource("resource-2", "CANDIDATE", "SERIAL-1", "AA:BB:CC:00:00:01");
            var conflictReview = WirelessIdentityContract.Describe(conflicting);
            AssertThrows("IDENTITY_SERIAL_MAC_CONFLICT", () => restarted.Decide(actor,
                new(conflicting.Id, conflictReview.IdentityRevision, "ACCEPT", "duplicate hardware", null, conflictReview.EvidenceRefs),
                conflictReview, new[] { resource, conflicting }, scope));

            var rejectedResource = Resource("resource-3", "CONFLICT", "SERIAL-3", "AA:BB:CC:00:00:03") with
            { ConflictReason = "SERIAL_MAC_EVIDENCE_CONFLICT" };
            var rejectedReview = WirelessIdentityContract.Describe(rejectedResource);
            var rejected = restarted.Decide(actor,
                new(rejectedResource.Id, rejectedReview.IdentityRevision, "REJECT", "evidence conflict retained", null, rejectedReview.EvidenceRefs),
                rejectedReview, new[] { rejectedResource }, scope);
            Assert(rejected.State == WirelessIdentityLifecycleStates.Rejected && rejected.History?.Count == 1,
                "conflicting identity evidence must support an auditable rejection");
            Console.WriteLine("WIRELESS_IDENTITY_LIFECYCLE_TESTS=5/5 PASS");
        }
        finally
        {
            foreach (var pair in previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    private static WirelessApResourceView Resource(string id, string status, string serial, string mac)
    {
        var observation = new WirelessApObservationView(
            id + ":obs", id, "ac-1", "domain-1", "site-1", "AP-" + id, serial,
            new[] { mac }, "WA", "group", "R/M", "RUNNING_MASTER", "HIGH", 1, 1,
            "raw-" + id, "parser-1", "source-" + id, "OBSERVED");
        return new(id, "AP-" + id, status, serial, new[] { mac }, new[] { observation.Id },
            new[] { "domain-1" }, "FULL", 1, 1, "source-" + id, "projection-" + id,
            status == "CONFLICT" ? "SERIAL_MAC_EVIDENCE_CONFLICT" : null, new[] { observation }, null);
    }

    private static void AssertThrows(string code, Action action)
    {
        try { action(); }
        catch (WirelessIdentityLifecycleException ex) when (ex.Code == code) { return; }
        throw new InvalidOperationException("Expected identity lifecycle error " + code + ".");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
