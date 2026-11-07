using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using NetConsole.Next.LocalManager;

var repository = new DirectoryInfo(Directory.GetCurrentDirectory());
while (!File.Exists(Path.Combine(repository.FullName, "tools", "workspace_paths.py")))
    repository = repository.Parent ?? throw new InvalidOperationException("Repository not found.");
var parent = Path.Combine(repository.Parent!.FullName, "test-data");
var root = Path.Combine(parent, "r70-doctor-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var marker = Path.Combine(root, ".netconsole-next-root");
File.WriteAllText(marker, "NETCONSOLE_NEXT_V1");
var database = Path.Combine(root, "catalog.db");
using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
{
    db.Open();
    using var command = db.CreateCommand();
    command.CommandText = "CREATE TABLE instance_identity(id INTEGER,instance_id TEXT); INSERT INTO instance_identity VALUES(1,'test-instance');";
    command.ExecuteNonQuery();
}
const string health = "{\"instanceId\":\"test-instance\",\"processId\":123,\"healthState\":\"HEALTHY\",\"serviceState\":\"running\",\"sqliteIntegrityAtStartup\":\"ok\",\"rawWriteAvailable\":true}";
static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
try
{
    var before = File.ReadAllBytes(database);
    using var healthyClient = new HttpClient(new ReplyHandler(health));
    var good = await LocalDeploymentDoctor.RunAsync(root, 18771, healthyClient, _ => Task.FromResult(true));
    Assert(good.State == "PASS" && good.ProcessId == 123 && good.Checks.Count == 7, "healthy matching instance must pass");
    Assert(before.SequenceEqual(File.ReadAllBytes(database)), "doctor must not modify catalog bytes");
    using var otherClient = new HttpClient(new ReplyHandler(health.Replace("test-instance", "different-instance")));
    var other = await LocalDeploymentDoctor.RunAsync(root, 18771, otherClient, _ => throw new Exception("must not probe unrelated process"));
    Assert(other.State == "FAILED" && other.ProcessId is null && other.Checks.Any(c => c.Key == "INSTANCE" && c.State == "FAIL"),
        "another port owner cannot be accepted or controlled");
    using var degradedClient = new HttpClient(new ReplyHandler(health.Replace("HEALTHY", "DEGRADED").Replace("\"ok\"", "\"failed\"")));
    var degraded = await LocalDeploymentDoctor.RunAsync(root, 18771, degradedClient, _ => Task.FromResult(true));
    Assert(degraded.State == "FAILED" && degraded.Checks.Count(c => c.State == "FAIL") == 2, "SQLite/degraded signals must fail");
    var missingWorker = await LocalDeploymentDoctor.RunAsync(root, 18771, healthyClient, _ => Task.FromResult(false));
    Assert(missingWorker.State == "FAILED" && missingWorker.Checks.Single(c => c.Key == "WORKER").State == "FAIL", "worker must not be inferred from service health");
    using var offlineClient = new HttpClient(new ReplyHandler("", HttpStatusCode.ServiceUnavailable));
    var offline = await LocalDeploymentDoctor.RunAsync(root, 18771, offlineClient, _ => Task.FromResult(true));
    Assert(offline.State == "FAILED" && offline.Checks.Any(c => c.Key == "LOOPBACK_HTTP" && c.Action.Contains("status")), "offline must give actionable diagnosis");
    using var oversizedClient = new HttpClient(new ReplyHandler(new string('x', 65_537)));
    var oversized = await LocalDeploymentDoctor.RunAsync(root, 18771, oversizedClient, _ => Task.FromResult(true));
    Assert(oversized.State == "FAILED", "unbounded health response must be rejected");
    using var malformedClient = new HttpClient(new ReplyHandler("[]"));
    var malformed = await LocalDeploymentDoctor.RunAsync(root, 18771, malformedClient, _ => Task.FromResult(true));
    Assert(malformed.State == "FAILED", "valid JSON of the wrong shape must return diagnosis rather than crash");
    File.WriteAllText(marker, "wrong-marker");
    var unmarked = await LocalDeploymentDoctor.RunAsync(root, 18771, healthyClient, _ => throw new Exception("must not run"));
    Assert(unmarked.State == "FAILED", "unmarked root must fail without initialization");
    File.WriteAllText(marker, "NETCONSOLE_NEXT_V1");
    var missing = await LocalDeploymentDoctor.RunAsync(Path.Combine(root, "absent"), 18771, healthyClient);
    Assert(missing.State == "FAILED" && !Directory.Exists(Path.Combine(root, "absent")), "doctor cannot initialize missing root");
    var worker = await LocalDeploymentDoctor.RunAsync(root, 18771, healthyClient);
    Assert(worker.Checks.Single(c => c.Key == "WORKER").State == "PASS", "real worker imports and close handshake must pass without a device");
    Console.WriteLine("LOCAL_DEPLOYMENT_DOCTOR_TESTS=10/10 PASS: instance ownership, read-only catalog, health, SQLite, worker import/close, invalid/oversized/offline diagnostics");
}
finally
{
    SqliteConnection.ClearAllPools();
    if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Unsafe fixture cleanup path.");
    Directory.Delete(root, recursive: true);
}

sealed class ReplyHandler(string content, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.Host != "127.0.0.1") throw new Exception("Doctor requested a non-loopback host.");
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(content, Encoding.UTF8, "application/json") });
    }
}
