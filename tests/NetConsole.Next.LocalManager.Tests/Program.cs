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
    if (OperatingSystem.IsWindows())
    {
        using var forbiddenClient = new HttpClient(new RejectHandler());
        foreach (var path in new[] { @"D:\NetConsoleData", @"d:\netconsoledata-dev\site\catalog.db",
                     @"D:/NetConsoleData/child", @"D:\unused\..\NetConsoleData-dev\child",
                     @"D:\NetConsoleData.\child", @"D:\NetConsoleData-dev \child", @"\\?\D:\NetConsoleData" })
        {
            var rejected = false;
            try { await LocalDeploymentDoctor.RunAsync(path, 18771, forbiddenClient, _ => throw new Exception("Forbidden worker probe.")); }
            catch (ArgumentException error) when (error.ParamName == "requestedRoot") { rejected = true; }
            Assert(rejected, "legacy data-root strings must be rejected before filesystem, HTTP or worker probing");
        }
        Assert(ManagedDataRootPath.Normalize(@"D:\NetConsoleData-next").EndsWith("NetConsoleData-next", StringComparison.Ordinal),
            "legacy prefix alone must not reject a distinct Next root");
    }
    var before = File.ReadAllBytes(database);
    Assert(ManagedDataRootPath.ResolveExistingDirectory(root) == Path.GetFullPath(root), "ordinary managed directories remain usable");
    var realParent = Path.Combine(root, "reparse-target");
    var managedChild = Path.Combine(realParent, "managed");
    var alias = Path.Combine(root, "reparse-alias");
    Directory.CreateDirectory(managedChild);
    File.WriteAllText(Path.Combine(managedChild, ".netconsole-next-root"), "NETCONSOLE_NEXT_V1");
    if (OperatingSystem.IsWindows())
    {
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            Arguments = $"/c mklink /J \"{alias}\" \"{realParent}\""
        };
        using var link = System.Diagnostics.Process.Start(start) ?? throw new Exception("Junction fixture did not start.");
        var output = link.StandardOutput.ReadToEnd();
        var error = link.StandardError.ReadToEnd();
        Assert(link.WaitForExit(5_000) && link.ExitCode == 0, "junction fixture creation: " + output + error);
    }
    else Directory.CreateSymbolicLink(alias, realParent);
    try
    {
        var aliasedChild = Path.Combine(alias, "managed");
        Assert((File.GetAttributes(aliasedChild) & FileAttributes.ReparsePoint) == 0,
            "regression fixture leaf is ordinary; only its parent is a reparse point");
        foreach (var path in new[] { alias, aliasedChild, Path.Combine(aliasedChild, "missing") })
        {
            var rejected = false;
            try { ManagedDataRootPath.ResolveExistingDirectory(path); }
            catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, "reject direct/ancestor junction before inspecting children or managed marker");
            using var rejectedClient = new HttpClient(new RejectHandler());
            var rejectedDiagnosis = await LocalDeploymentDoctor.RunAsync(path, 18771, rejectedClient,
                _ => throw new Exception("Worker probe must not run through a junction."));
            Assert(rejectedDiagnosis.State == "FAILED" && rejectedDiagnosis.ProcessId is null &&
                rejectedDiagnosis.Checks is [{ Key: "DATA_ROOT", State: "FAIL" }],
                "doctor returns a bounded root diagnosis before marker, HTTP or worker access through a junction");
        }
        Assert(File.ReadAllText(Path.Combine(managedChild, ".netconsole-next-root")) == "NETCONSOLE_NEXT_V1",
            "junction rejection preserves the isolated target fixture");
    }
    finally { Directory.Delete(alias); }
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
    using var absentClient = new HttpClient(new RejectHandler());
    var missing = await LocalDeploymentDoctor.RunAsync(Path.Combine(root, "absent"), 18771, absentClient,
        _ => throw new Exception("Worker probe must not run for an absent root."));
    Assert(missing.State == "FAILED" && missing.Checks is [{ Key: "DATA_ROOT", State: "FAIL" }] &&
        !Directory.Exists(Path.Combine(root, "absent")), "doctor cannot initialize missing root or probe HTTP/worker");
    var fileRoot = await LocalDeploymentDoctor.RunAsync(marker, 18771, absentClient,
        _ => throw new Exception("Worker probe must not run for a file root."));
    Assert(fileRoot.State == "FAILED" && fileRoot.Checks is [{ Key: "DATA_ROOT", State: "FAIL" }],
        "a file root must return a root diagnosis rather than crash");
    if (OperatingSystem.IsWindows())
    {
        var markerInfo = new FileInfo(marker);
        var originalSddl = System.IO.FileSystemAclExtensions.GetAccessControl(markerInfo)
            .GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access);
        try
        {
            var deniedAcl = System.IO.FileSystemAclExtensions.GetAccessControl(markerInfo);
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            deniedAcl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(identity.User!,
                System.Security.AccessControl.FileSystemRights.ReadData, System.Security.AccessControl.AccessControlType.Deny));
            System.IO.FileSystemAclExtensions.SetAccessControl(markerInfo, deniedAcl);
            var denied = await LocalDeploymentDoctor.RunAsync(root, 18771, absentClient,
                _ => throw new Exception("Worker probe must not run when the marker is inaccessible."));
            Assert(denied.State == "FAILED" && denied.ProcessId is null &&
                denied.Checks is [{ Key: "CATALOG", State: "FAIL" }],
                "real synthetic marker access denial returns a bounded diagnosis before HTTP/worker probing");
        }
        finally
        {
            var restoredAcl = new System.Security.AccessControl.FileSecurity();
            restoredAcl.SetSecurityDescriptorSddlForm(originalSddl, System.Security.AccessControl.AccessControlSections.Access);
            System.IO.FileSystemAclExtensions.SetAccessControl(markerInfo, restoredAcl);
        }
        Assert(File.ReadAllText(marker) == "NETCONSOLE_NEXT_V1" && before.SequenceEqual(File.ReadAllBytes(database)),
            "permission failure leaves marker/catalog unchanged and fixture ACL is restored");
    }
    var worker = await LocalDeploymentDoctor.RunAsync(root, 18771, healthyClient);
    Assert(worker.Checks.Single(c => c.Key == "WORKER").State == "PASS", "real worker imports and close handshake must pass without a device");
    Console.WriteLine("LOCAL_DEPLOYMENT_DOCTOR_TESTS=13/13 PASS: legacy roots rejected before IO, doctor/root ancestor junction rejection, missing/file/inaccessible-marker boundaries, instance ownership, read-only catalog, health, SQLite, worker import/close, invalid/oversized/offline diagnostics");
}
finally
{
    SqliteConnection.ClearAllPools();
    if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Unsafe fixture cleanup path.");
    Directory.Delete(root, recursive: true);
}

sealed class RejectHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new Exception("Forbidden HTTP request.");
}

sealed class ReplyHandler(string content, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.Host != "127.0.0.1") throw new Exception("Doctor requested a non-loopback host.");
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(content, Encoding.UTF8, "application/json") });
    }
}
