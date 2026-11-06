using System.Net;
using System.Text.Json;
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
            Console.WriteLine("PASS: ZTE profile, parser replay, current/history atomic facts and failed-run preservation");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
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
