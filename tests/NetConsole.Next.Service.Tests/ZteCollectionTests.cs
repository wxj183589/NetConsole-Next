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
                "reader", "TEST_CREDENTIAL", "SHA256:AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA", true, 0, 0));
            Assert(catalog.GetZteDeviceProfile(profileId)?.Host == "192.0.2.22", "ZTE profile must round-trip through SQLite");
            var service = new ZteCollectionService(catalog, new FixedCredentials(), new FixtureTransportFactory());
            var visibleProfile = service.Profiles(P2B5SiteScopeDecision.Global).Single();
            var visibleJson = JsonSerializer.Serialize(visibleProfile);
            Assert(!visibleJson.Contains("credential", StringComparison.OrdinalIgnoreCase) &&
                   !visibleJson.Contains("hostKey", StringComparison.OrdinalIgnoreCase),
                "ZTE profile API view must not expose credential references or host-key pins");
            Assert(service.Profiles(new P2B5SiteScopeDecision("AUTHORIZED", false,
                       new HashSet<string>(["other-site"], StringComparer.Ordinal), "test scope")).Count == 0,
                "ZTE profile reads must exclude sites outside the authenticated scope");

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
            Assert(history.Count == 1 && !history[0].IsCurrent, "successful ZTE collection must retain one history fact");

            var failedRun = Guid.NewGuid().ToString("N");
            catalog.StartZteCollectionRun(failedRun, profileId, 2000);
            catalog.CommitZteCollection(failedRun, profileId, "FAILED", "PERMISSIONDENIED",
                new Dictionary<string, string> { [ZteCommandPolicy.ShowOpticalInfoBrief] = "PERMISSIONDENIED" },
                Array.Empty<(string, string, string)>());
            Assert(catalog.QueryZteFacts(profileId, "interfaces", false, 10).Count == 1,
                "failed or empty ZTE collection must not clear the previous current fact");

            var liveRunId = service.Start(profileId);
            ZteCollectionRunSnapshot? liveRun = null;
            for (var attempt = 0; attempt < 40; attempt++)
            {
                await Task.Delay(10);
                liveRun = service.GetRun(liveRunId);
                if (liveRun is not null && liveRun.State is not ("QUEUED" or "RUNNING")) break;
            }
            Assert(liveRun?.State == "SUCCEEDED", "ZTE runtime fixture session must complete all five read-only commands");
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

    private sealed class FixedCredentials : IH3cCredentialProvider
    {
        public string? GetPassword(string reference) => reference == "TEST_CREDENTIAL" ? "synthetic" : null;
    }

    private sealed class FixtureTransportFactory : IH3cSshTransportFactory
    {
        public IH3cSshTransport Create(H3cDeviceProfileSnapshot profile, string password) => new FixtureTransport();
    }

    private sealed class FixtureTransport : IH3cSshTransport
    {
        public bool IsConnected { get; private set; }
        public Task ConnectAsync(Func<string?, string?, bool> verifyHostKey, CancellationToken cancellationToken)
        {
            if (!verifyHostKey("ssh-ed25519", "SHA256:AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA"))
                throw new H3cHostKeyMismatchException();
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
