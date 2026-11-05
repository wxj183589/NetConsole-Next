using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using NetConsole.Next.Service;
using System.Net;
using System.Text.Json;

internal static class InteractiveScopeTests
{
    public static async Task RunAsync()
    {
        ReadsCurrentPrincipalWithoutWritingSessionActivity();
        RoleDisableLogoutAndExpiryInvalidateCachedIdentity();
        GlobalFileDenialPrecedesExpansionAndAdmission();
        await QueuedArchiveRechecksOwner();
        await RunningArchiveCannotPublishAfterRevocation(false);
        await RunningArchiveCannotPublishAfterRevocation(true);
        Console.WriteLine("INTERACTIVE_SCOPE_TESTS=6/6 PASS");
    }

    private static void ReadsCurrentPrincipalWithoutWritingSessionActivity()
    {
        using var f = new Fixture();
        f.Execute("UPDATE web_sessions SET last_seen_ms=1");
        Assert(f.Authorizer.Current(f.Owner)?.Role == "SUPERADMIN" && f.Authorizer.CanUseTerminal(f.Owner, f.TerminalId),
            "valid session retains current global and terminal access");
        Assert(f.Scalar("SELECT MIN(last_seen_ms) FROM web_sessions") == "1", "stream checks must not turn each output chunk into a session database write");
        Assert(f.Authorizer.Current(f.Owner with { UserId = "another-user" }) is null &&
            f.Authorizer.Current(f.Owner with { SessionId = "unknown-session" }) is null, "user/session IDs must match the same live authority row");
    }

    private static void RoleDisableLogoutAndExpiryInvalidateCachedIdentity()
    {
        using var f = new Fixture();
        foreach (var mutation in new[] { "UPDATE web_users SET role='ADMIN' WHERE username='superadmin'",
            "UPDATE web_users SET disabled=1 WHERE username='superadmin'", "UPDATE web_sessions SET revoked_ms=1", "UPDATE web_sessions SET expires_ms=1" })
        {
            f.Execute(mutation);
            Assert(!f.Authorizer.CanUseGlobalFiles(f.Owner.UserId, f.Owner.SessionId) && !f.Authorizer.CanUseTerminal(f.Owner, f.TerminalId),
                "a cached SUPERADMIN identity cannot survive role downgrade, disable, logout or expiry");
            f.Execute("UPDATE web_users SET role='SUPERADMIN',disabled=0 WHERE username='superadmin'");
            f.Execute("UPDATE web_sessions SET revoked_ms=NULL,expires_ms=9999999999999");
        }
    }

    private static void GlobalFileDenialPrecedesExpansionAndAdmission()
    {
        using var f = new Fixture();
        f.Execute("UPDATE web_users SET role='ADMIN' WHERE username='superadmin'");
        var context = new DefaultHttpContext();
        context.Items[WebCookieNames.IdentityItem] = f.Owner;
        var denied = f.Authorizer.RequireGlobalFiles(context);
        Assert(denied is IStatusCodeHttpResult { StatusCode: 403 }, "machine-wide roots require a current global authority");
        var denial = JsonSerializer.Serialize(((IValueHttpResult)denied!).Value);
        Assert(!denial.Contains("relativePath") && !denial.Contains("selectedCount") && !denial.Contains("canDownload"), "denial cannot return file metadata/counts/job existence");
        var files = new FileManagerService(f.Settings);
        using var service = f.Archives(files);
        try { service.Create(Request(), f.Owner); throw new InvalidOperationException("revoked owner admitted"); }
        catch (ArchiveRejectedException ex) { Assert(ex.StatusCode == 403 && ex.Code == "ARCHIVE_SCOPE_REVOKED", "scope rejection must be explicit"); }
        Assert(files.ExpansionCallCount == 0 && service.AdmissionState.ActiveJobs == 0 && service.AdmissionState.ReservedInputBytes == 0,
            "global file rejection must happen before scanning, quota reservation or job creation");
    }

    private static async Task QueuedArchiveRechecksOwner()
    {
        using var f = new Fixture();
        using var service = f.Archives();
        var job = service.Create(Request(), f.Owner);
        f.Execute("UPDATE web_users SET role='ADMIN' WHERE username='superadmin'");
        await service.StartAsync(CancellationToken.None);
        try
        {
            await Wait(() => service.AdmissionState.ActiveJobs == 0);
            Assert(service.Get(job.Id, f.Owner) is null && service.OpenDownload(job.Id, f.Owner) is null, "revoked owner cannot read or download a previous global job");
            f.Execute("UPDATE web_users SET role='SUPERADMIN' WHERE username='superadmin'");
            var result = service.Get(job.Id, f.Owner)!;
            Assert(result.State == "FAILED" && result.ErrorCode == "ARCHIVE_SCOPE_REVOKED" && result.ProcessedCount == 0 && !result.CanDownload,
                "a revoked queued job must not copy a single file or publish an archive");
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    private static async Task RunningArchiveCannotPublishAfterRevocation(bool revokeSession)
    {
        using var f = new Fixture();
        File.WriteAllBytes(Path.Combine(f.Root, "raw", "a-large.bin"), new byte[2 * 1024 * 1024]);
        using var service = f.Archives();
        await service.StartAsync(CancellationToken.None);
        try
        {
            var job = service.Create(new(new[] { new ArchiveSelection("raw", new[] { "a-large.bin", "hello.txt" }) }), f.Owner);
            await Wait(() => service.Get(job.Id, f.Owner)?.State == "RUNNING");
            f.Execute(revokeSession ? "UPDATE web_sessions SET revoked_ms=1" : "UPDATE web_users SET role='ADMIN' WHERE username='superadmin'");
            await Wait(() => service.AdmissionState.ActiveJobs == 0);
            Assert(service.Get(job.Id, f.Owner) is null && service.OpenDownload(job.Id, f.Owner) is null, "revocation also applies to job metadata and downloads");
            f.Execute("UPDATE web_sessions SET revoked_ms=NULL");
            f.Execute("UPDATE web_users SET role='SUPERADMIN' WHERE username='superadmin'");
            var result = service.Get(job.Id, f.Owner)!;
            Assert(result.State == "FAILED" && result.ErrorCode == "ARCHIVE_SCOPE_REVOKED" && result.ProcessedCount <= 1 && !result.CanDownload &&
                !File.Exists(Path.Combine(f.Root, "archive-tmp", job.Id + ".zip")) && !File.Exists(Path.Combine(f.Root, "archive-tmp", job.Id + ".zip.part")),
                "only an already-accepted bounded copy may finish; later files and archive publication must stop and temporary bytes must retire");
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    private static ArchiveRequest Request() => new(new[] { new ArchiveSelection("raw", new[] { "hello.txt" }) });

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "netconsole-next-interactive-scope-" + Guid.NewGuid().ToString("N"));
        public RuntimeSettings Settings { get; }
        public Catalog Catalog { get; }
        public InteractiveScopeAuthorizer Authorizer { get; }
        public WebUserIdentity Owner { get; }
        public string TerminalId { get; } = "fixture-terminal";
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Settings = new() { DataRoot = Root, WebPort = 18771, SyslogPort = 0, SyslogBind = IPAddress.Loopback,
                SyslogReceiveBufferBytes = 4 * 1024 * 1024, ArchiveTestCopyDelayMilliseconds = 20,
                ArchiveMinFreeDiskBytes = 1024 * 1024 };
            Catalog = new(Settings); Catalog.Initialize();
            var auth = new WebAuthStore(Settings);
            // Built-in isolated Web fixtures, unrelated to simulator credentials.
            Owner = auth.TryLogin("superadmin", "superadmin", "127.0.0.1", out _)!.User;
            Authorizer = new(auth, new P2B5SiteScopeStore(Settings), Catalog);
            var profile = Guid.NewGuid().ToString("N");
            Catalog.CreateH3cDeviceProfile(new(profile, "192.0.2.81", 22, "fixture", "FIXTURE_REFERENCE",
                "SHA256:" + Convert.ToBase64String(new byte[32]).TrimEnd('='), true, 0, 0));
            Catalog.CreateTerminalSession(new(TerminalId, profile, "fixture", "SSH", "RESTRICTED_READONLY", "192.0.2.81", 22,
                null, null, "CONNECTING", 1, null, 1, null, null, null, null, null, 0, null, false));
            Directory.CreateDirectory(Path.Combine(Root, "raw"));
            File.WriteAllText(Path.Combine(Root, "raw", "hello.txt"), "fixture");
        }
        public FileArchiveService Archives(FileManagerService? files = null) => new(files ?? new(Settings), Settings, new Disk(), Authorizer.CanUseGlobalFiles);
        public void Execute(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        public string Scalar(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToString(command.ExecuteScalar())!; }
        private SqliteConnection Open() { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Root, "catalog.db"), Pooling = false }.ToString()); db.Open(); return db; }
        public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(Root, true); }
    }
    private sealed class Disk : IArchiveDiskSpaceProvider { public long? GetAvailableFreeSpace(string path) => 1024L * 1024 * 1024; }
    private static async Task Wait(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
