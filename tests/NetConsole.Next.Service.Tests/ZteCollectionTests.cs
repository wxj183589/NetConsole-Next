using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NetConsole.Next.Service;

namespace NetConsole.Next.Service.Tests;

internal static class ZteCollectionTests
{
    public static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "netconsole-next-zte-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var catalog = new Catalog(new RuntimeSettings
            {
                DataRoot = root, WebPort = 18771, SyslogPort = 0, SyslogBind = IPAddress.Loopback,
                SyslogReceiveBufferBytes = 65536
            });
            catalog.Initialize();
            var profileId = Guid.NewGuid().ToString("N");
            catalog.CreateZteDeviceProfile(new ZteDeviceProfileSnapshot(profileId, "192.0.2.22", 22,
                "reader", "TEST_CREDENTIAL", "", true, 0, 0), "synthetic");
            Assert(catalog.GetZteDeviceProfile(profileId)?.Host == "192.0.2.22", "ZTE profile must round-trip through SQLite");
            var initial = catalog.GetZteDeviceProfile(profileId)!;
            catalog.CreateZteDeviceProfile(initial, "");
            Assert(catalog.GetZteCredentialPassword(profileId) == "synthetic", "blank password must retain the stored password");
            await using (var rejected = new ZteSshDeviceSession(initial, new UnavailableH3cCredentialProvider(), new FixtureTransportFactory(true), catalog))
            {
                try { await rejected.ConnectAsync(CancellationToken.None); throw new InvalidOperationException("authentication failure must propagate"); }
                catch (H3cAuthenticationFailedException) { }
            }
            Assert(catalog.GetZteDeviceProfile(profileId)!.HostKeySha256 == "", "failed authentication must not persist a host key");
            try
            {
                catalog.CreateZteDeviceProfile(new ZteDeviceProfileSnapshot(profileId, "192.0.2.23", 22,
                    "reader", "TEST_CREDENTIAL", "SHA256:AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA", true, 0, 0));
                throw new InvalidOperationException("ZTE profile identity changes must be rejected");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("identity is immutable", StringComparison.Ordinal)) { }
            var service = new ZteCollectionService(catalog, new UnavailableH3cCredentialProvider(), new FixtureTransportFactory());
            var visibleProfile = service.Profiles(P2B5SiteScopeDecision.Global).Single();
            var visibleJson = JsonSerializer.Serialize(visibleProfile);
            using var visibleDocument = JsonDocument.Parse(visibleJson);
            var fieldNames = visibleDocument.RootElement.EnumerateObject().Select(field => field.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert(!fieldNames.Contains("CredentialReference") && !fieldNames.Contains("Password") &&
                   !fieldNames.Contains("CredentialPassword") && !fieldNames.Contains("HostKeySha256"),
                "ZTE profile API view must not expose credential references or host-key pins");
            Assert(visibleProfile.CredentialConfigured && !visibleJson.Contains("synthetic", StringComparison.Ordinal),
                "ZTE profile view must expose only whether a password is configured");
            Assert(service.Profiles(new P2B5SiteScopeDecision("AUTHORIZED", false,
                       new HashSet<string>(["other-site"], StringComparer.Ordinal), "test scope")).Count == 0,
                "ZTE profile reads must exclude sites outside the authenticated scope");
            Assert(!service.ProfileIsInScope(profileId, new P2B5SiteScopeDecision("AUTHORIZED", false,
                       new HashSet<string>(["other-site"], StringComparer.Ordinal), "test scope")),
                "ZTE credential operations must exclude profiles outside the authenticated scope");

            var interfaces = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "zte", "phase9_c89e4_show_interface_brief.txt"));
            var parsed = ZteZxr10Parser.ParseInterfaces(interfaces);
            Assert(parsed.Status == "PARSED" && parsed.Value.Count > 0, "ZTE interface fixture must parse through the production parser");
            var run = Guid.NewGuid().ToString("N");
            catalog.StartZteCollectionRun(run, profileId, 1000);
            catalog.CommitZteCollection(run, profileId, "SUCCEEDED", null,
                new Dictionary<string, string> { [ZteCommandPolicy.ShowInterfaceBrief] = "PARSED" },
                new[] { ("interfaces", "PARSED", JsonSerializer.Serialize(parsed.Value)) });
            var current = catalog.QueryZteFacts(profileId, "interfaces", false, 10);
            var history = catalog.QueryZteFacts(profileId, "interfaces", true, 10);
            Assert(current.Count == 1 && current[0].IsCurrent, "successful ZTE collection must publish one current fact");
            Assert(history.Count == 0, "the current fact must not be duplicated into history");

            var repeatRun = Guid.NewGuid().ToString("N");
            catalog.StartZteCollectionRun(repeatRun, profileId, 1500);
            catalog.CommitZteCollection(repeatRun, profileId, "SUCCEEDED", null,
                new Dictionary<string, string> { [ZteCommandPolicy.ShowInterfaceBrief] = "PARSED" },
                new[] { ("interfaces", "PARSED", JsonSerializer.Serialize(parsed.Value)) });
            Assert(catalog.QueryZteFacts(profileId, "interfaces", true, 10).Count == 1,
                "repeated identical collection must retain one prior observation, not duplicate rows");

            var partialRun = Guid.NewGuid().ToString("N");
            catalog.StartZteCollectionRun(partialRun, profileId, 1750);
            catalog.CommitZteCollection(partialRun, profileId, "FAILED", "PAGED",
                new Dictionary<string, string> { [ZteCommandPolicy.ShowOpticalInfoBrief] = "PAGED" },
                new[] { ("raw:" + ZteCommandPolicy.ShowOpticalInfoBrief, "PAGED", "{\"captureStatus\":\"PARTIAL\",\"raw\":\"fixture\"}") });
            Assert(catalog.QueryZteFacts(profileId, "raw:" + ZteCommandPolicy.ShowOpticalInfoBrief, true, 10).Count == 1,
                "partial command output must remain available as history evidence");

            var failedRun = Guid.NewGuid().ToString("N");
            catalog.StartZteCollectionRun(failedRun, profileId, 2000);
            catalog.CommitZteCollection(failedRun, profileId, "FAILED", "PERMISSIONDENIED",
                new Dictionary<string, string> { [ZteCommandPolicy.ShowOpticalInfoBrief] = "PERMISSIONDENIED" },
                Array.Empty<(string, string, string)>());
            Assert(catalog.QueryZteFacts(profileId, "interfaces", false, 10).Count == 1,
                "failed or empty ZTE collection must not clear the previous current fact");

            var queuedRun = Guid.NewGuid().ToString("N");
            catalog.StartZteCollectionRun(queuedRun, profileId, 2500, "RUNNING");
            catalog.RecoverInterruptedZteCollectionRuns();
            Assert(catalog.GetZteCollectionRun(queuedRun)?.State == "INTERRUPTED",
                "startup recovery must reconcile orphaned ZTE collection runs");

            var liveRunId = service.Start(profileId);
            ZteCollectionRunSnapshot? liveRun = null;
            for (var attempt = 0; attempt < 40; attempt++)
            {
                await Task.Delay(10);
                liveRun = service.GetRun(liveRunId);
                if (liveRun is not null && liveRun.State is not ("QUEUED" or "RUNNING")) break;
            }
            Assert(liveRun?.State == "SUCCEEDED", "ZTE runtime fixture session must complete all five read-only commands");
            var trusted = catalog.GetZteDeviceProfile(profileId)!;
            Assert(trusted.HostKeySha256 == "SHA256:AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA", "first authenticated connection must store the host key");
            const string rotated = "SHA256:AgMEBQYHCAkKCwwNDg8QERITFBUWFxgZGhscHR4fICE";
            Assert(catalog.CommitZteHostKey(trusted, "ssh-ed25519", rotated), "changed key must update automatically");
            Assert(!catalog.CommitZteHostKey(initial, "ssh-ed25519", trusted.HostKeySha256), "stale session must not overwrite a newer key");
            Assert(catalog.GetZteDeviceProfile(profileId)!.HostKeySha256 == rotated, "rotated key must persist");
            var beforeCredentialUpdate = catalog.GetZteDeviceProfile(profileId)!;
            catalog.UpdateZteCredential(profileId, "updated-synthetic", false);
            var afterCredentialUpdate = catalog.GetZteDeviceProfile(profileId)!;
            Assert(catalog.GetZteCredentialPassword(profileId) == "updated-synthetic" && service.Profile(profileId)!.CredentialConfigured,
                "credential-only updates must save the password and report its presence");
            Assert(afterCredentialUpdate.UpdatedMs > beforeCredentialUpdate.UpdatedMs &&
                   afterCredentialUpdate with { UpdatedMs = beforeCredentialUpdate.UpdatedMs } == beforeCredentialUpdate,
                "credential-only updates must advance revision without changing identity, Site, host key or compatibility");
            catalog.UpdateZteCredential(profileId, "", false);
            catalog.UpdateZteCredential(profileId, null, false);
            Assert(catalog.GetZteCredentialPassword(profileId) == "updated-synthetic",
                "empty and omitted credential updates must preserve the saved password");
            var beforeInvalidCredential = catalog.GetZteDeviceProfile(profileId)!;
            try
            {
                catalog.UpdateZteCredential(profileId, new string('x', 4097), false);
                throw new InvalidOperationException("oversized credentials must be rejected");
            }
            catch (ArgumentException) { }
            Assert(catalog.GetZteDeviceProfile(profileId) == beforeInvalidCredential &&
                   catalog.GetZteCredentialPassword(profileId) == "updated-synthetic",
                "invalid credentials must leave both profile and password unchanged");
            try
            {
                catalog.UpdateZteCredential(Guid.NewGuid().ToString("N"), "unused", false);
                throw new InvalidOperationException("missing profile credential updates must be rejected");
            }
            catch (KeyNotFoundException) { }
            catalog.UpdateZteCredential(profileId, "ignored-on-explicit-clear", true);
            Assert(catalog.GetZteCredentialPassword(profileId) is null, "explicit clear must remove the stored password");
            Assert(!service.Profile(profileId)!.CredentialConfigured,
                "credential status must immediately reflect an explicit clear");
            Assert(catalog.QueryZteFacts(profileId, "optical", false, 10).Count == 1 &&
                   catalog.QueryZteFacts(profileId, "lldp", false, 10).Count == 1,
                "ZTE runtime fixture session must persist optical and LLDP current facts");
            await VerifyOperationBoundaries(catalog, root);
            Console.WriteLine("PASS: ZTE profile, parser replay, current/history atomic facts and failed-run preservation");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task VerifyOperationBoundaries(Catalog catalog, string root)
    {
        void Execute(string sql, string id = "")
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(root, "catalog.db"), Pooling = false, ForeignKeys = true }.ToString());
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
        string Scalar(string sql)
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(root, "catalog.db"), Pooling = false }.ToString());
            db.Open();
            using var command = db.CreateCommand(); command.CommandText = sql;
            return Convert.ToString(command.ExecuteScalar())!;
        }
        (string Profile, string Baseline) CreateTarget()
        {
            var id = Guid.NewGuid().ToString("N");
            catalog.CreateZteDeviceProfile(new(id, "192.0.2.44", 22, "reader", "FIXTURE", "", true, 0, 0), "synthetic");
            var baseline = Guid.NewGuid().ToString("N");
            catalog.StartZteCollectionRun(baseline, id, 1);
            catalog.CommitZteCollection(baseline, id, "SUCCEEDED", null, new Dictionary<string, string>(),
                [("identity", "PARSED", "{\"previous\":true}")]);
            return (id, baseline);
        }
        void AssertFailed(ZteCollectionRunSnapshot run, string profile, string baseline, int completed)
        {
            Assert(run.State == "FAILED" && run.FailureReason!.Contains("REVOKED", StringComparison.Ordinal),
                "a rejected live boundary must persist a failed run and its reason");
            var current = catalog.QueryZteFacts(profile, null, false, 100);
            Assert(current.Count == 1 && current[0].RunId == baseline,
                "revocation must preserve the previous Current without publishing any new kind");
            var retained = catalog.QueryZteFacts(profile, null, true, 100).Where(fact => fact.RunId == run.Id).ToArray();
            Assert(retained.Length == completed && retained.All(fact => fact.Status == "PARTIAL" && !fact.IsCurrent),
                "completed output must be sealed as partial history after a failed boundary");
        }

        var denied = CreateTarget();
        var deniedFactory = new BoundaryTransportFactory();
        await using (var service = new ZteCollectionService(catalog, new UnavailableH3cCredentialProvider(), deniedFactory))
        {
            var before = Scalar("SELECT COUNT(*) FROM zte_collection_runs");
            try { service.Start(denied.Profile, () => throw new TaskScopeRevokedException("ZTE_ACTOR_SCOPE_REVOKED")); throw new Exception("admission must reject"); }
            catch (TaskScopeRevokedException) { }
            Assert(deniedFactory.Created == 0 && Scalar("SELECT COUNT(*) FROM zte_collection_runs") == before,
                "denied admission must neither create a run nor a transport");
        }

        var actorId = Scalar("SELECT id FROM web_users WHERE username='superadmin'");
        void RequireActor()
        {
            if (catalog.GetActiveTaskActor(actorId) is not { Role: "SUPERADMIN" })
                throw new TaskScopeRevokedException("ZTE_ACTOR_SCOPE_REVOKED");
        }
        var duringConnect = CreateTarget();
        var connectFactory = new BoundaryTransportFactory
        { Connected = () => Execute("UPDATE web_users SET disabled=1 WHERE id=$id", actorId) };
        try
        {
            await using var service = new ZteCollectionService(catalog, new UnavailableH3cCredentialProvider(), connectFactory);
            var result = await Terminal(service, service.Start(duringConnect.Profile, RequireActor));
            AssertFailed(result, duringConnect.Profile, duringConnect.Baseline, 0);
            Assert(connectFactory.Commands == 0 && connectFactory.Disposals == 1,
                "actor disabled during connection must prevent the first command and release transport");
        }
        finally { Execute("UPDATE web_users SET disabled=0 WHERE id=$id", actorId); }

        var betweenCommands = CreateTarget();
        var roleFactory = new BoundaryTransportFactory
        { Command = _ => Execute("UPDATE web_users SET role='ADMIN' WHERE id=$id", actorId) };
        try
        {
            await using var service = new ZteCollectionService(catalog, new UnavailableH3cCredentialProvider(), roleFactory);
            var result = await Terminal(service, service.Start(betweenCommands.Profile, RequireActor));
            AssertFailed(result, betweenCommands.Profile, betweenCommands.Baseline, 1);
            Assert(roleFactory.Commands == 1 && roleFactory.Disposals == 1,
                "role revocation must stop the next command and release transport");
        }
        finally { Execute("UPDATE web_users SET role='SUPERADMIN' WHERE id=$id", actorId); }

        Execute("INSERT OR IGNORE INTO sites(id,name) VALUES('zte-boundary-other','Other fixture')");
        foreach (var assignment in new[] { "enabled=0", "host='192.0.2.45'", "port=2222", "username='changed-reader'", "site_id='zte-boundary-other'" })
        {
            var target = CreateTarget();
            var factory = new BoundaryTransportFactory
            { Command = _ => Execute("UPDATE zte_device_profiles SET " + assignment + " WHERE id=$id", target.Profile) };
            await using var service = new ZteCollectionService(catalog, new UnavailableH3cCredentialProvider(), factory);
            var result = await Terminal(service, service.Start(target.Profile));
            AssertFailed(result, target.Profile, target.Baseline, 1);
            Assert(factory.Commands == 1, "disabled or changed frozen endpoint must stop the next command: " + assignment);
        }

        var beforePublish = CreateTarget();
        var publishFactory = new BoundaryTransportFactory
        { Command = command => { if (command == ZteCommandPolicy.ShowLldpEntry) Execute("UPDATE web_users SET disabled=1 WHERE id=$id", actorId); } };
        try
        {
            await using var service = new ZteCollectionService(catalog, new UnavailableH3cCredentialProvider(), publishFactory);
            var result = await Terminal(service, service.Start(beforePublish.Profile, RequireActor));
            AssertFailed(result, beforePublish.Profile, beforePublish.Baseline, 5);
            Assert(publishFactory.Commands == 5, "revocation after the last command must be caught before publication");
        }
        finally { Execute("UPDATE web_users SET disabled=0 WHERE id=$id", actorId); }

        var queued = CreateTarget();
        var blockerA = CreateTarget();
        var blockerB = CreateTarget();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queueFactory = new BoundaryTransportFactory { ConnectWait = token => release.Task.WaitAsync(token) };
        await using (var service = new ZteCollectionService(catalog, new UnavailableH3cCredentialProvider(), queueFactory))
        {
            var first = service.Start(blockerA.Profile);
            var second = service.Start(blockerB.Profile);
            var last = service.Start(queued.Profile);
            try { Execute("UPDATE zte_device_profiles SET enabled=0 WHERE id=$id", queued.Profile); }
            finally { release.TrySetResult(); }
            await Terminal(service, first); await Terminal(service, second);
            AssertFailed(await Terminal(service, last), queued.Profile, queued.Baseline, 0);
            Assert(queueFactory.Created == 2, "target disabled while queued must not create a third transport");
        }

        var reconnect = CreateTarget();
        var reconnectFactory = new BoundaryTransportFactory { Retry = true, LoseConnection = true,
            Command = _ => Execute("UPDATE web_users SET disabled=1 WHERE id=$id", actorId) };
        try
        {
            await using var service = new ZteCollectionService(catalog, new UnavailableH3cCredentialProvider(), reconnectFactory);
            var result = await Terminal(service, service.Start(reconnect.Profile, RequireActor));
            AssertFailed(result, reconnect.Profile, reconnect.Baseline, 0);
            Assert(reconnectFactory.Created == 1 && reconnectFactory.Commands == 1 && reconnectFactory.Disposals == 1,
                "live actor revocation during connection loss must stop before opening a replacement transport");
        }
        finally { Execute("UPDATE web_users SET disabled=0 WHERE id=$id", actorId); }

        var metadata = CreateTarget();
        var metadataFactory = new BoundaryTransportFactory { Command = _ => catalog.UpdateZteCredential(metadata.Profile, "synthetic", false) };
        await using (var service = new ZteCollectionService(catalog, new UnavailableH3cCredentialProvider(), metadataFactory))
        {
            var result = await Terminal(service, service.Start(metadata.Profile, RequireActor));
            Assert(result.State == "SUCCEEDED" && metadataFactory.Commands == 5 &&
                catalog.QueryZteFacts(metadata.Profile, null, false, 100).All(fact => fact.RunId == result.Id),
                "automatic host-key trust and credential revision updates must not revoke an unchanged endpoint");
        }
        Console.WriteLine("ZTE_OPERATION_BOUNDARY_TESTS=12/12 PASS");
    }

    private static async Task<ZteCollectionRunSnapshot> Terminal(ZteCollectionService service, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var run = service.GetRun(id)!;
            if (run.State is not ("QUEUED" or "RUNNING")) return run;
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class BoundaryTransportFactory : IH3cSshTransportFactory
    {
        public Action? Connected { get; init; }
        public Action<string>? Command { get; init; }
        public Func<CancellationToken, Task>? ConnectWait { get; init; }
        public bool Retry { get; init; }
        public bool LoseConnection { get; init; }
        public int Created, Commands, Disposals;
        public IH3cSshTransport Create(H3cDeviceProfileSnapshot profile, string password)
        {
            Assert(password == "synthetic", "boundary fixture must use its saved device credential");
            IH3cSshTransport CreatePhysical() { Interlocked.Increment(ref Created); return new BoundaryTransport(this); }
            return Retry ? new NetmikoRetryTransport(CreatePhysical, ZteCommandPolicy.IsAllowed, (_, _) => Task.CompletedTask) : CreatePhysical();
        }
    }

    private sealed class BoundaryTransport(BoundaryTransportFactory owner) : IH3cSshTransport
    {
        private readonly FixtureTransport _fixture = new(false);
        public bool IsConnected => _fixture.IsConnected;
        public async Task ConnectAsync(Func<string?, string?, bool> verifyHostKey, CancellationToken cancellationToken)
        {
            if (owner.ConnectWait is not null) await owner.ConnectWait(cancellationToken);
            await _fixture.ConnectAsync(verifyHostKey, cancellationToken);
            owner.Connected?.Invoke();
        }
        public Task<string> ExecuteAsync(string command, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref owner.Commands);
            owner.Command?.Invoke(command);
            if (owner.LoseConnection) { _fixture.Abort(); throw new H3cConnectionLostException(); }
            return _fixture.ExecuteAsync(command, cancellationToken);
        }
        public void Abort() => _fixture.Abort();
        public void Dispose() { Interlocked.Increment(ref owner.Disposals); _fixture.Dispose(); }
    }

    private sealed class FixtureTransportFactory(bool failAuthentication = false) : IH3cSshTransportFactory
    {
        public IH3cSshTransport Create(H3cDeviceProfileSnapshot profile, string password)
        {
            Assert(password == "synthetic", "session must use its device-bound password");
            return new FixtureTransport(failAuthentication);
        }
    }

    private sealed class FixtureTransport(bool failAuthentication) : IH3cSshTransport
    {
        public bool IsConnected { get; private set; }
        public Task ConnectAsync(Func<string?, string?, bool> verifyHostKey, CancellationToken cancellationToken)
        {
            if (!verifyHostKey("ssh-ed25519", "SHA256:AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA"))
                throw new H3cHostKeyMismatchException();
            if (failAuthentication) throw new H3cAuthenticationFailedException();
            IsConnected = true;
            return Task.CompletedTask;
        }
        public Task<string> ExecuteAsync(string command, CancellationToken cancellationToken)
        {
            var relative = command switch
            {
                ZteCommandPolicy.ShowVersion => "phase9_c89e4_show_version.txt",
                ZteCommandPolicy.ShowInterfaceBrief => "phase9_c89e4_show_interface_brief.txt",
                ZteCommandPolicy.ShowOpticalInfoBrief => "phase9_c89e4_show_opticalinfo_brief.txt",
                ZteCommandPolicy.ShowLldpNeighborBrief => "phase9_hzdt10_lldp_brief.txt",
                ZteCommandPolicy.ShowLldpEntry => "phase9_hzdt10_lldp_entry.txt",
                _ => throw new ArgumentException(command)
            };
            return Task.FromResult(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "zte", relative)));
        }
        public void Abort() => IsConnected = false;
        public void Dispose() => IsConnected = false;
    }
}
