using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NetConsole.Next.Service;

internal static class H3cAcResourceTests
{
    public static async Task RunAsync()
    {
        FixedCommandsAndTranscriptStates();
        ControllerRoleStates();
        await FullPlanPublishesEverySealedCommand();
        OptionalFailureStatesRemainEvidence();
        Console.WriteLine("H3C_AC_RESOURCE_TESTS=4/4 PASS");
    }

    private static void FixedCommandsAndTranscriptStates()
    {
        Assert(H3cAcResources.RequiredCommands.SequenceEqual(new[] { H3cCommandPolicy.DisplayWlanApAll,
            H3cCommandPolicy.DisplayWlanApAllAddress, H3cCommandPolicy.DisplayWlanApAllRadio }),
            "the original three required AC commands retain their ordering and protocol-independent business plan");
        foreach (var command in H3cAcResources.OptionalCommands.Concat(H3cAcResources.OverviewCommands))
            Assert(H3cCommandPolicy.IsAllowed(command), "fixed inherited optional/overview commands must use the shared command policy");
        foreach (var command in new[] { "display wlan ap name guessed verbose", "display wlan ap address",
            "display wlan ap all verbose; system-view", "display wlan ap all verbose\nsystem-view",
            "display wlan ac-role; system-view", "display wlan ac-role\nsystem-view" })
            Assert(!H3cAcResources.IsAllowed(command) && H3cAcResources.TryParse(command, "output") is null,
                "unverified templates, aliases and extra CLI operations are never inferred");

        const string commandText = H3cAcResources.DisplayWlanApAllConnectionRecord;
        var scenarios = new[]
        {
            (Output: Wrap(commandText, "AP-ONE explicit vendor text"), Status: "PARSED"),
            (Output: Wrap(commandText, "% Unrecognized command found at '^' position."), Status: "UNRECOGNIZED"),
            (Output: Wrap(commandText, "Permission denied"), Status: "UNRECOGNIZED"),
            (Output: Wrap(commandText, "AP-ONE\n---- More ----"), Status: "PARTIAL"),
            (Output: "<AC>" + commandText + "\nincomplete text", Status: "PARTIAL"),
            (Output: "", Status: "EMPTY")
        };
        foreach (var scenario in scenarios)
        {
            var result = H3cComwareParser.Parse(commandText, scenario.Output);
            Assert(result.Status == scenario.Status, "optional command errors, partial transcripts and empty output remain explicit");
        }
        var completed = H3cAcResources.TryParse(commandText, "Netmiko stripped output", completedExec: true)!;
        using var payload = JsonDocument.Parse(completed.Json);
        Assert(completed.Status == "PARSED" && !payload.RootElement.GetProperty("structured").GetBoolean() &&
            payload.RootElement.GetProperty("output").GetString() == "Netmiko stripped output",
            "transport completion admits stripped output while preserving the absence of a table parser");
        Assert(H3cAcResources.TryParse(commandText, "<AC>display version\nwrong command\n<AC>", completedExec: true)!.Status == "PARTIAL",
            "transport completion does not hide another command boundary");
    }

    private static async Task FullPlanPublishesEverySealedCommand()
    {
        using var fixture = new Fixture();
        var commands = H3cAcResources.RequiredCommands.Concat(H3cAcResources.OptionalCommands)
            .Concat(H3cAcResources.OverviewCommands.Where(H3cAcResources.IsAllowed)).ToArray();
        await using var scheduler = new BoundedCollectionScheduler(new Factory(), 1, 4, fixture.Catalog,
            resultSink: new H3cTaskRawResultSink(fixture.Catalog), scopeAuthorizer: fixture.Authorizer);
        var definition = Guid.NewGuid().ToString("N");
        fixture.Catalog.CreateTaskDefinition(definition, "H3C_SSH_READONLY", fixture.Profile, commands[0], TimeSpan.FromSeconds(10),
            scopeSnapshot: fixture.Authorizer.CaptureDefinition(fixture.Profile, "local-api-token"), commands: commands);
        Assert(scheduler.TryScheduleDefinition(definition, out var id, "local-api-token"), "AC plan must be admitted as one bounded collection task");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        DeviceCollectionSnapshot run;
        do { await Task.Delay(10, timeout.Token); run = scheduler.Get(id)!; }
        while (run.State is not ("SUCCEEDED" or "FAILED" or "CANCELLED" or "TIMED_OUT"));
        var facts = fixture.Catalog.QueryH3cFacts(fixture.Profile);
        Assert(run.State == "SUCCEEDED" && facts.Count == commands.Length + 1 && facts.All(item => item.CollectedRawVerified),
            "every required/optional/overview command including Radio publishes a fact backed by the sealed multi-command Raw");
        foreach (var command in commands)
        {
            var fact = facts.Single(item => item.Kind == Kind(command));
            using var json = JsonDocument.Parse(fact.PayloadJson);
            Assert(json.RootElement.GetProperty("command").GetString() == command &&
                json.RootElement.GetProperty("rawProvenance").GetProperty("runId").GetString() == id &&
                json.RootElement.GetProperty("capturedScope").GetProperty("profileId").GetString() == fixture.Profile &&
                !string.IsNullOrEmpty(json.RootElement.GetProperty("output").GetString()),
                "command facts retain exact command text, output and captured profile/Raw provenance");
        }
        var role = facts.Single(item => item.Kind == "controller.role");
        var capability = H3cOperationalCapabilities.Project(fixture.Profile, facts, role.CapturedMs);
        Assert(role.Status == "STALE" && role.CollectedRawVerified && capability.ControllerRole == "Common" &&
            capability.ControllerRoleNormalized == "COMMON" &&
            capability.Capabilities.Single(item => item.Key == "AC_ROLE_READ").State == "VERIFIED" &&
            capability.Capabilities.Single(item => item.Key == "HA_SWITCH").State == "UNKNOWN",
            "sealed controller Common evidence proves role read only, independently of AP redundancy and HA switching");
        Assert(new Catalog(fixture.Settings).QueryH3cFacts(fixture.Profile).All(item => item.CollectedRawVerified),
            "Radio and optional command Raw provenance survives Catalog restart");
        var groups = new WirelessReadModelService(fixture.Catalog).GetSnapshot(P2B5SiteScopeDecision.Global).ApGroups;
        Assert(groups.Count == 1 && groups.Single().GroupIdentityStatus == "CANDIDATE" && groups.Single().SourceGroupKey is null,
            "AP-list group names never become fabricated vendor group IDs through the expanded command plan");
    }

    private static void ControllerRoleStates()
    {
        const string command = H3cAcResources.DisplayWlanAcRole;
        foreach (var sample in new[]
        {
            (Body: "Current AC role: Common.", Status: "PARSED", Raw: "Common", Normalized: "COMMON"),
            (Body: "Current AC role: Master.", Status: "PARSED", Raw: "Master", Normalized: "MASTER"),
            (Body: "Current AC role: Backup.", Status: "PARSED", Raw: "Backup", Normalized: "BACKUP"),
            (Body: "Current AC role: Vendor role.", Status: "UNRECOGNIZED", Raw: "Vendor role", Normalized: "UNKNOWN"),
            (Body: "AP-ONE R/M", Status: "UNRECOGNIZED", Raw: (string?)null, Normalized: "UNKNOWN"),
            (Body: "Current AC role: Common.\nCurrent AC role: Backup.", Status: "PARTIAL", Raw: (string?)null, Normalized: "UNKNOWN"),
            (Body: "% Unrecognized command found at '^' position.", Status: "UNSUPPORTED", Raw: (string?)null, Normalized: "UNKNOWN"),
            (Body: "Permission denied", Status: "UNSUPPORTED", Raw: (string?)null, Normalized: "UNKNOWN"),
            (Body: "Current AC role: Common.\n---- More ----", Status: "PARTIAL", Raw: (string?)null, Normalized: "UNKNOWN"),
            (Body: "", Status: "EMPTY", Raw: (string?)null, Normalized: "UNKNOWN")
        })
        {
            var role = H3cControllerRoleParser.Parse(Wrap(command, sample.Body));
            Assert(role.Status == sample.Status && role.RawRole == sample.Raw && role.NormalizedRole == sample.Normalized,
                "controller roles preserve unsupported, conflict, partial and raw role states without inferring from AP roles");
            Assert(H3cComwareParser.Parse(command, Wrap(command, sample.Body)).Status == sample.Status,
                "shared command parser uses typed controller-role states");
        }
        Assert(H3cControllerRoleParser.Parse("Current AC role: Common.").Status == "PARTIAL" &&
            H3cControllerRoleParser.Parse("Current AC role: Common.", completedExec: true).Status == "PARSED",
            "stripped controller output requires sealed transport completion");
    }

    private static void OptionalFailureStatesRemainEvidence()
    {
        using var fixture = new Fixture();
        const string command = H3cAcResources.DisplayWlanApAllLldp;
        foreach (var sample in new[]
        {
            (Body: "% Unrecognized command found at '^' position.", Expected: "UNSUPPORTED"),
            (Body: "Permission denied", Expected: "UNSUPPORTED"),
            (Body: "AP-ONE\n---- More ----", Expected: "PARTIAL"),
            (Body: "", Expected: "EMPTY")
        }.Select((item, index) => (item.Body, item.Expected, Captured: 1000L + index)))
        {
            fixture.Catalog.ImportH3cOfflineReplay(fixture.Profile,
                new Dictionary<string, string> { [command] = Wrap(command, sample.Body) }, sample.Captured);
            var fact = fixture.Catalog.QueryH3cFacts(fixture.Profile, Kind(command)).Single();
            Assert(fact.Status == sample.Expected && !fact.CollectedRawVerified,
                "unsupported/permission/partial/empty optional results stay inspectable without becoming verified success");
        }
        Assert(fixture.Catalog.QueryH3cFacts(fixture.Profile, Kind(command), history: true).Count == 4,
            "failed optional evidence remains separate immutable history");
    }

    private static string Kind(string command) => "command." + command.Replace(' ', '.');
    private static string Wrap(string command, string body) => "<AC>" + command + "\n" + body + "\n<AC>";

    private sealed class Factory : IDeviceSessionFactory
    {
        public IDeviceSession Create(string target) => new Session();
        private sealed class Session : IDeviceSession
        {
            public Task ConnectAsync(CancellationToken token) => Task.CompletedTask;
            public Task<string> ExecuteAsync(string command, CancellationToken token) => Task.FromResult(command switch
            {
                H3cCommandPolicy.DisplayWlanApAll => "AP-ONE 1 R/M WA6320 SERIAL-001 group-a 1 day",
                H3cCommandPolicy.DisplayWlanApAllAddress => "AP-ONE 192.0.2.71 4c6f-d608-0400",
                H3cCommandPolicy.DisplayWlanApAllRadio => "AP-ONE 1 149 80 17",
                H3cAcResources.DisplayWlanAcRole => "Current AC role: Common.",
                _ => "AP-ONE explicit vendor text"
            });
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class Fixture : IDisposable
    {
        public readonly string Root = Path.Combine(Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_WORKSPACE_ROOT") ??
            (OperatingSystem.IsWindows() ? @"D:\study\NetConsole-Next-Workspace" : Path.Combine(Path.GetTempPath(), "netconsole-next-workspace")),
            "test-data", "ac-resource-tests", Guid.NewGuid().ToString("N"));
        public readonly string Profile = Guid.NewGuid().ToString("N");
        public readonly RuntimeSettings Settings;
        public readonly Catalog Catalog;
        public readonly TaskSiteScopeAuthorizer Authorizer;
        public Fixture()
        {
            Settings = new() { DataRoot = Root, WebPort = 18771, SyslogPort = 0,
                SyslogBind = IPAddress.Loopback, SyslogReceiveBufferBytes = 1024 * 1024 };
            Catalog = new(Settings); Catalog.Initialize();
            Catalog.CreateH3cDeviceProfile(new(Profile, "192.0.2.71", 22, "fixture", "FIXTURE_REFERENCE",
                "SHA256:" + Convert.ToBase64String(new byte[32]).TrimEnd('='), true, 0, 0));
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(Root, "catalog.db"), Pooling = false }.ToString());
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "UPDATE device_inventory SET device_type='AC'"; command.ExecuteNonQuery();
            Authorizer = new(Catalog, new(Settings));
        }
        public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(Root, true); }
    }

    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
