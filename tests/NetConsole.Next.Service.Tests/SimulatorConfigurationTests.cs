using Microsoft.Data.Sqlite;
using NetConsole.Next.Service;
using System.Net;

internal static class SimulatorConfigurationTests
{
    private static readonly WebUserIdentity Actor = new("local-api-token", "local-api-token", "SUPERADMIN", "test");
    private const string Interface = "GigabitEthernet1/0/1";
    private const string Original = "#\n version 7.1.070, Release 3507P26\n#\n sysname fixture\n#\ninterface GigabitEthernet1/0/1\n description original\n#\nlocal-user admin class manage\n password hash SYNTHETIC_SECRET\n#\nreturn\n";

    public static async Task RunAsync()
    {
        await CaptureAndRedact();
        await RejectIncomplete();
        await RejectTampering();
        await VerifyChange();
        await VerifyUndo();
        await VerifyRollback();
        await RejectDrift();
        await RejectTargetsAndInput();
        await RejectChangedIdentity();
        await VerifyLiveRevocation();
        await VerifySerialization();
        await VerifyCaptureErrors();
        Console.WriteLine("SIMULATOR_CONFIGURATION_TESTS=12/12 PASS");
    }

    private static async Task CaptureAndRedact()
    {
        using var fixture = new Fixture();
        var backup = await fixture.Capture();
        Assert(backup.Restorable && backup.RawByteCount > 0 && backup.RawSha256.Length == 64 &&
            backup.Model == "WA6638" && backup.SoftwareVersion.Contains("3507P26"), "complete backup must retain integrity and verified device identity");
        Assert(File.ReadAllText(fixture.Raw(backup)).Contains("SYNTHETIC_SECRET"), "private original must retain required restore bytes");
        var view = fixture.Service.View(backup.Id, Actor, P2B5SiteScopeDecision.Global)!;
        Assert(!view.RedactedConfiguration.Contains("SYNTHETIC_SECRET") && view.RedactedConfiguration.Contains("<REDACTED>") &&
            view.RedactedConfiguration.Contains("description original"), "public view must redact secrets and retain ordinary configuration");
        Assert(fixture.Service.List(fixture.Profile, Actor, P2B5SiteScopeDecision.Global).Single().Id == backup.Id,
            "complete backup must be discoverable independently from sanitized backup history");
        Assert(fixture.Catalog.QueryH3cFacts(fixture.Profile, history: true).Count == 0, "private original must not enter public Facts");
        var second = await fixture.Capture();
        Assert(backup.Version == 1 && second.Version == 2 && second.FormatVersion == 1,
            "configuration version must advance independently of manifest format version");
    }

    private static async Task RejectIncomplete()
    {
        using var fixture = new Fixture();
        foreach (var output in new[] { Original.Replace("return\n", ""), Original.Replace("SYNTHETIC_SECRET", "<REDACTED>"),
            Original + "unknown trailing bytes", Original.Replace("#\nreturn", "---- More ----\n#\nreturn"), Original + "\u001b" })
        {
            fixture.Factory.Text = output;
            await Reject(() => fixture.Capture(), "CONFIG_COMPLETE_CAPTURE_INCOMPLETE");
        }
        Assert(fixture.Service.List(fixture.Profile, Actor, P2B5SiteScopeDecision.Global).Count == 0, "partial or redacted captures must not publish restorable metadata");
    }

    private static async Task RejectTampering()
    {
        using var fixture = new Fixture();
        var backup = await fixture.Capture();
        var original = File.ReadAllText(fixture.Raw(backup));
        File.WriteAllText(fixture.Raw(backup), original.Replace("original", "tampered"));
        Assert(fixture.Service.View(backup.Id, Actor, P2B5SiteScopeDecision.Global) is null, "same-length private tamper must invalidate view");
        Assert(fixture.Service.List(fixture.Profile, Actor, P2B5SiteScopeDecision.Global).Count == 0, "tampered original must not be advertised as restorable");
        await Reject(() => fixture.Change(backup, "next"), "CONFIG_COMPLETE_SOURCE_UNAVAILABLE");
        Assert(fixture.Factory.Writes == 0 && fixture.Factory.Opens == 1, "invalid backup must reject before connection or Writer");
        File.WriteAllText(fixture.Raw(backup), original);
        var publicPath = fixture.Raw(backup).Replace(".cfg", ".view.cfg");
        File.WriteAllText(publicPath, File.ReadAllText(publicPath).Replace("original", "tampered"));
        Assert(fixture.Service.View(backup.Id, Actor, P2B5SiteScopeDecision.Global) is null, "same-length public tamper must also fail integrity");
    }

    private static async Task VerifyChange()
    {
        using var fixture = new Fixture();
        var backup = await fixture.Capture();
        var change = await fixture.Change(backup, "verified-description");
        Assert(change.State == "VERIFIED" && change.BeforeBackupId is not null && change.AfterBackupId is not null &&
            change.RollbackBackupId is null && fixture.Factory.Writes == 1, "Writer success requires persisted before and verified after snapshots");
        Assert(SimulatorConfigurationService.Description(fixture.Factory.Text, Interface) == "verified-description", "readback must contain desired description");
        var next = fixture.Service.List(fixture.Profile, Actor, P2B5SiteScopeDecision.Global).Single(item => item.Id == change.AfterBackupId);
        var noChange = await fixture.Change(next, "verified-description");
        Assert(noChange.State == "NO_CHANGE" && fixture.Factory.Writes == 1, "unchanged description must avoid a device write");
        Assert(fixture.Service.Changes(fixture.Profile, Actor, P2B5SiteScopeDecision.Global).Count == 2, "operations must persist into scoped history");
    }

    private static async Task VerifyUndo()
    {
        using var fixture = new Fixture();
        var backup = await fixture.Capture();
        var change = await fixture.Change(backup, null);
        Assert(change.State == "VERIFIED" && SimulatorConfigurationService.Description(fixture.Factory.Text, Interface) is null,
            "null description must perform and verify undo description");
    }

    private static async Task VerifyRollback()
    {
        foreach (var mode in new[] { "throw-after-write", "ignore-write", "fail-readback", "rollback-fails", "unrelated-drift" })
        {
            using var fixture = new Fixture();
            var backup = await fixture.Capture();
            fixture.Factory.Mode = mode;
            var change = await fixture.Change(backup, "new-description");
            var failureExpected = mode is "rollback-fails" or "unrelated-drift";
            Assert(change.State == (failureExpected ? "ROLLBACK_FAILED" : "ROLLBACK_VERIFIED") && change.Error is not null,
                $"{mode}: uncertain write or readback mismatch must attempt independent recovery and report evidence");
            Assert(fixture.Factory.Opens == 3 && fixture.Factory.Writes == 2, "recovery must use a new bounded session and exactly one rollback write");
            if (!failureExpected)
                Assert(fixture.Factory.Text == Original && change.RollbackBackupId is not null, "successful rollback requires full original configuration equality");
        }
    }

    private static async Task RejectDrift()
    {
        using var fixture = new Fixture();
        var backup = await fixture.Capture();
        fixture.Factory.Text = Original.Replace("sysname fixture", "sysname changed");
        var change = await fixture.Change(backup, "new-description");
        Assert(change.State == "FAILED" && change.Error == "CONFIG_CHANGE_CURRENT_CHANGED" && fixture.Factory.Writes == 0,
            "a stale baseline must fail before writing and must not run rollback");
    }

    private static async Task RejectTargetsAndInput()
    {
        using var fixture = new Fixture();
        var backup = await fixture.Capture();
        foreach (var input in new[] { (Interface + "\nsave", "safe"), (Interface + "\n", "safe"),
            ("Vlan-interface1", "safe"), (Interface, "evil;save"), (Interface, "evil\nundo shutdown"), (Interface, " trailing ") })
            await Reject(() => fixture.Service.ChangeDescriptionAsync(new(fixture.Profile, backup.Id, input.Item1, input.Item2), Actor,
                P2B5SiteScopeDecision.Global, default), "CONFIG_CHANGE_INPUT_INVALID");
        Assert(fixture.Factory.Opens == 1 && fixture.Factory.Writes == 0, "injection input must reject before any connection");
        var denied = Actor with { Role = "ADMIN" };
        await Reject(() => fixture.Service.CaptureAsync(fixture.Profile, denied, P2B5SiteScopeDecision.Global, default), "CONFIG_CHANGE_PERMISSION_DENIED");
        fixture.Sql("UPDATE h3c_device_profiles SET host='192.168.56.9'");
        await Reject(() => fixture.Capture(), "CONFIG_CHANGE_TARGET_NOT_AUTHORIZED");
        Assert(fixture.Factory.Opens == 1, "non-whitelisted target must not open a session");
    }

    private static async Task RejectChangedIdentity()
    {
        using var fixture = new Fixture();
        var backup = await fixture.Capture();
        fixture.Sql("UPDATE device_inventory SET id='replacement-device'");
        await Reject(() => fixture.Change(backup, "safe"), "CONFIG_COMPLETE_IDENTITY_MISMATCH");
        Assert(fixture.Factory.Writes == 0, "backup of replaced inventory identity must not authorize a write");
        Assert(!fixture.Service.List(fixture.Profile, Actor, P2B5SiteScopeDecision.Global).Any(item => item.Restorable),
            "list must not advertise an old inventory identity as restorable");
    }

    private static async Task VerifyLiveRevocation()
    {
        using var fixture = new Fixture();
        var backup = await fixture.Capture();
        fixture.Factory.OnConfigurationRead = () => fixture.Sql("UPDATE h3c_device_profiles SET enabled=0");
        var change = await fixture.Change(backup, "safe");
        Assert(change.State == "FAILED" && fixture.Factory.Writes == 0 && change.Error!.Contains("REVOKED"),
            "target disabled during preflight read must fail before Writer admission");
    }

    private static async Task VerifySerialization()
    {
        using var fixture = new Fixture();
        var backup = await fixture.Capture();
        fixture.Factory.DelayReads = true;
        var changes = await Task.WhenAll(fixture.Change(backup, "first"), fixture.Change(backup, "second"));
        Assert(changes.Count(item => item.State == "VERIFIED") == 1 && changes.Count(item => item.Error == "CONFIG_CHANGE_CURRENT_CHANGED") == 1 &&
            fixture.Factory.Writes == 1, "simultaneous stale baselines must serialize by endpoint and prevent a second write");
    }

    private static async Task VerifyCaptureErrors()
    {
        using var fixture = new Fixture();
        foreach (var pair in new (Exception Error, string Code, int Status)[] {
            (new H3cCredentialUnavailableException(), "CONFIG_DEVICE_CREDENTIAL_UNAVAILABLE", 409),
            (new H3cAuthenticationFailedException(), "CONFIG_DEVICE_AUTHENTICATION_FAILED", 502),
            (new H3cCommandTimeoutException(), "CONFIG_DEVICE_TIMEOUT", 504),
            (new H3cConnectionLostException(), "CONFIG_DEVICE_CONNECTION_LOST", 502),
            (new H3cHostKeyMismatchException(), "CONFIG_DEVICE_HOST_KEY_FAILED", 409) })
        {
            fixture.Factory.OpenError = pair.Error;
            try { await fixture.Capture(); throw new InvalidOperationException("Transport error must reject"); }
            catch (ConfigRestoreException error)
            { Assert(error.Code == pair.Code && error.StatusCode == pair.Status, "transport error must use fixed safe domain code and status"); }
        }
        fixture.Factory.OpenError = new OperationCanceledException();
        try { await fixture.Capture(); throw new InvalidOperationException("Cancellation must propagate"); }
        catch (OperationCanceledException) { }
    }

    private static async Task Reject<T>(Func<Task<T>> action, string code)
    {
        try { await action(); }
        catch (ConfigRestoreException error) when (error.Code == code) { return; }
        throw new InvalidOperationException("Expected rejection: " + code);
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; }
        public string Profile { get; } = Guid.NewGuid().ToString("N");
        public Catalog Catalog { get; }
        public Factory Factory { get; } = new();
        public SimulatorConfigurationService Service { get; }
        public Fixture()
        {
            Root = Path.Combine(Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_WORKSPACE_ROOT") ??
                @"D:\study\NetConsole-Next-Workspace", "test-data", "r69-config-unit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            var settings = new RuntimeSettings { DataRoot = Root, WebPort = 18771, SyslogPort = 0,
                SyslogBind = IPAddress.Loopback, SyslogReceiveBufferBytes = 4 * 1024 * 1024 };
            Catalog = new(settings);
            Catalog.Initialize();
            Catalog.CreateH3cDeviceProfile(new(Profile, "192.168.56.3", 22, "fixture", "FIXTURE_REFERENCE", "", true, 0, 0,
                TerminalProtocol: "SSH"));
            Service = new(Catalog, settings, Factory);
        }
        public Task<CompleteConfigurationBackup> Capture() => Service.CaptureAsync(Profile, Actor, P2B5SiteScopeDecision.Global, default);
        public Task<SimulatorConfigurationChange> Change(CompleteConfigurationBackup backup, string? description) =>
            Service.ChangeDescriptionAsync(new(Profile, backup.Id, Interface, description), Actor, P2B5SiteScopeDecision.Global, default);
        public string Raw(CompleteConfigurationBackup backup) => Path.Combine(Root, "backup", "config-complete", Profile, backup.Id + ".cfg");
        public void Sql(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + Path.Combine(Root, "catalog.db"));
            db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    private sealed class Factory : ISimulatorConfigurationSessionFactory
    {
        public string Text = Original;
        public string Mode = "normal";
        public int Opens;
        public int Writes;
        public bool DelayReads;
        public Action? OnConfigurationRead;
        public Exception? OpenError;
        public Task<ISimulatorConfigurationSession> OpenAsync(H3cDeviceProfileSnapshot profile, CancellationToken cancellationToken)
        { Opens++; if (OpenError is not null) throw OpenError; return Task.FromResult<ISimulatorConfigurationSession>(new Session(this)); }
        private sealed class Session(Factory owner) : ISimulatorConfigurationSession
        {
            public async Task<string> ReadAsync(string command, CancellationToken cancellationToken)
            {
                if (owner.DelayReads) await Task.Delay(15, cancellationToken);
                if (command == H3cCommandPolicy.DisplayVersion)
                    return "H3C Comware Software, Version 7.1.070, Release 3507P26\nH3C WA6638 uptime is 1 day\n";
                Assert(command == H3cCommandPolicy.DisplayCurrentConfiguration, "only two read commands are expected");
                if (owner.Mode == "fail-readback" && owner.Writes == 1) throw new IOException("synthetic readback interruption");
                owner.OnConfigurationRead?.Invoke();
                return owner.Text;
            }
            public Task SetDescriptionAsync(string interfaceName, string? description, CancellationToken cancellationToken)
            {
                owner.Writes++;
                if (owner.Mode == "rollback-fails" && owner.Writes == 2) throw new IOException("synthetic recovery interruption");
                if (!(owner.Mode == "ignore-write" && owner.Writes == 1))
                {
                    var previous = SimulatorConfigurationService.Description(owner.Text, interfaceName);
                    if (previous is not null) owner.Text = owner.Text.Replace(" description " + previous + "\n", "");
                    if (description is not null) owner.Text = owner.Text.Replace("interface " + interfaceName + "\n", "interface " + interfaceName + "\n description " + description + "\n");
                }
                if (owner.Writes == 1 && owner.Mode == "unrelated-drift") owner.Text = owner.Text.Replace("sysname fixture", "sysname drifted");
                if (owner.Writes == 1 && owner.Mode is "throw-after-write" or "rollback-fails") throw new IOException("synthetic uncertain write");
                return Task.CompletedTask;
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
