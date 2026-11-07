using System.Text.Json;

namespace NetConsole.Next.Service.Tests;

internal static class H3cOperationalCapabilityTests
{
    public static void Run()
    {
        VerifyLiveIdentityParsing();
        const long now = 1_800_000_000_000;
        static H3cFactSnapshot Fact(string id, string kind, string status, long time, bool raw = true,
            bool current = true, string profile = "p") =>
            new(id, profile, kind, "default", status, raw ? "SIMULATOR" : "OFFLINE_FIXTURE", time, current,
                "{\"model\":\"WX5540H-HCL\",\"version\":\"7.1.064\",\"release\":\"R5447\",\"password\":\"must-not-leak\"}")
                { CollectedRawVerified = raw };
        static H3cOperationalCapability Capability(H3cOperationalCapabilityView view, string key) =>
            view.Capabilities.Single(c => c.Key == key);
        var view = H3cOperationalCapabilities.Project("p", new[]
        {
            Fact("identity", "device.identity", "STALE", now - 1000),
            Fact("ap", "command.display.wlan.ap.all", "STALE", now - 1000),
            Fact("vlan", "vlan.table", "UNSUPPORTED", now - 1000),
            Fact("other-profile", "interface", "STALE", now, profile: "other"),
            Fact("previous", "interface", "STALE", now - 2000, current: false),
            Fact("future", "interface", "STALE", now + 1000)
        }, now);
        Assert(view.Model == "WX5540H-HCL" && view.Version == "7.1.064", "identity metadata must retain evidence");
        Assert(Capability(view, "AP_INVENTORY_READ").State == "VERIFIED", "raw-verified recent AP evidence must resolve");
        Assert(Capability(view, "VLAN_READ").State == "UNSUPPORTED", "unsupported must not become model-based support");
        Assert(Capability(view, "INTERFACE_READ").State == "UNKNOWN", "other profile/history/future cannot establish capability");
        Assert(Capability(view, "HA_SWITCH").State == "UNKNOWN", "model/AP support cannot prove HA switch");
        Assert(!JsonSerializer.Serialize(view).Contains("must-not-leak"), "raw payload must not escape capability projection");
        var old = H3cOperationalCapabilities.Project("p", new[]
        { Fact("old", "command.display.wlan.ap.all", "STALE", now - H3cOperationalCapabilities.FreshnessWindowMs - 1) }, now);
        Assert(Capability(old, "AP_INVENTORY_READ").State == "STALE", "old raw evidence must expire");
        var fixture = H3cOperationalCapabilities.Project("p", new[]
        { Fact("fixture", "interface", "STALE", now - 1, raw: false) }, now);
        Assert(Capability(fixture, "INTERFACE_READ").State == "FIXTURE", "fixture cannot claim authenticated transport");
        var failed = H3cOperationalCapabilities.Project("p", new[]
        {
            Fact("before", "command.display.wlan.ap.all", "STALE", now - 2),
            Fact("latest", "command.display.wlan.ap.all", "PARTIAL", now - 1)
        }, now);
        Assert(Capability(failed, "AP_INVENTORY_READ").State == "PARTIAL" &&
            Capability(failed, "AP_INVENTORY_READ").EvidenceIds.SequenceEqual(new[] { "latest" }),
            "latest partial capture must not fall back to older success");
        var failedIdentity = H3cOperationalCapabilities.Project("p", new[]
        { Fact("old-identity", "device.identity", "STALE", now - 2),
          Fact("new-identity", "device.identity", "FAILED", now - 1) }, now);
        Assert(failedIdentity.Model is null && failedIdentity.Version is null &&
            Capability(failedIdentity, "DEVICE_IDENTITY").State == "FAILED",
            "latest failed identity cannot expose metadata from older successful revision");
        var roleFact = Fact("role", "controller.role", "STALE", now - 1) with
        { PayloadJson = "{\"rawRole\":\"Common\",\"normalizedRole\":\"COMMON\"}" };
        var controller = H3cOperationalCapabilities.Project("p", new[] { roleFact }, now);
        Assert(controller.ControllerRole == "Common" && controller.ControllerRoleNormalized == "COMMON" &&
            Capability(controller, "AC_ROLE_READ").State == "VERIFIED" && Capability(controller, "HA_SWITCH").State == "UNKNOWN",
            "controller role read is independent of HA switching and AP primary/backup observations");
        var failedRole = H3cOperationalCapabilities.Project("p", new[]
        { roleFact, roleFact with { Id = "new-role", CapturedMs = now, Status = "PARTIAL" } }, now);
        Assert(failedRole.ControllerRole is null && failedRole.ControllerRoleNormalized is null &&
            Capability(failedRole, "AC_ROLE_READ").State == "PARTIAL",
            "a latest partial controller role cannot expose old successful role fields");

        var obs = new[]
        {
            new WirelessApObservationView("o1", "ap", "ac1", "domain", "site", "AP", "serial",
                new[] { "001122334455" }, "WA", "group-a", "R/M", "RUN", "DEVICE", now - 1, now,
                "raw:one", "parser", "rev1", "MATCHED"),
            new WirelessApObservationView("o2", "ap", "ac2", "domain", "site", "AP", "serial",
                new[] { "001122334455" }, "WA", "group-b", "R/B", "RUN", "DEVICE", now - 1, now,
                "raw:two", "parser", "rev2", "MATCHED")
        };
        var ap = new WirelessApResourceView("ap", "AP", "RESOLVED", "serial", new[] { "001122334455" },
            new[] { "o1", "o2" }, new[] { "domain" }, "FULL", now - 1, now, "rev", "projection", null, obs);
        var domain = new WirelessAcDomainView("domain", "AC domain", "ONE_PLUS_ONE", "USER_CONFIRMED_TOPOLOGY",
            new[] { "ac1", "ac2" }, Array.Empty<WirelessAcMemberView>(), new[] { "user:topology" }, "FULL", "rev", now);
        var snapshot = new WirelessProjectionSnapshot(new[] { domain }, new[] { ap }, Array.Empty<WirelessApGroupView>(),
            now, "source-rev", new("FULL", "claims-rev"), Array.Empty<string>());
        var wireless = H3cOperationalCapabilities.ProjectWireless(snapshot);
        Assert(wireless.Domains.Single().TopologyEvidence == "USER_CONFIRMED_TOPOLOGY" &&
            wireless.Domains.Single().DeviceRoleEvidence == "DEVICE_REPORTED_ROLE" &&
            wireless.Domains.Single().HaVerification == "NOT_TESTED", "user topology, AP role and verified HA must remain separate");
        Assert(wireless.Domains.Single().Observations.Select(o => o.RawState).SequenceEqual(new[] { "R/M", "R/B" }),
            "both AC observations must retain their roles without selecting one winner");
        Assert(wireless.FitAps.Single().DirectAccessState == "UNKNOWN" &&
            wireless.FitAps.Single().AcDeviceIds.Count == 2 && wireless.FitAps.Single().ApGroups.Count == 2,
            "AC-managed FIT identity/groups do not prove direct Telnet access");
        Assert(wireless.Scope.ClaimsRevision == "claims-rev", "claims revision must survive projection");
        var empty = H3cOperationalCapabilities.ProjectWireless(snapshot with
        { AcDomains = Array.Empty<WirelessAcDomainView>(), ApResources = Array.Empty<WirelessApResourceView>() });
        Assert(empty.Domains.Count == 0 && empty.FitAps.Count == 0, "restricted snapshot must not repopulate hidden evidence");
        Console.WriteLine("PASS: H3C evidence capabilities; scope, freshness, provenance, HA separation and AC-managed FIT access");
    }

    private static void VerifyLiveIdentityParsing()
    {
        const string ac = "display version\nH3C Comware Software, Version 7.1.064, Alpha 7165\n" +
            "H3C WX5540H-HCL uptime is 1 week, 0 days, 11 hours, 14 minutes\n" +
            "Slot 1:\nType : LSR1SRP2C1\n<AC1>";
        var parsed = H3cComwareParser.ParseVersion(ac);
        Assert(parsed.Model == "WX5540H-HCL" && parsed.Version == "7.1.064" && parsed.Release == "Alpha 7165",
            "actual HCL identity must include chassis model and Alpha release without using board type");
        var sw = H3cComwareParser.ParseVersion("H3C Comware Software, Version 7.1.070, Release 6635P01\nH3C S6850-56HF uptime is 1 day");
        Assert(sw.Model == "S6850-56HF" && sw.Release == "6635P01", "existing Release format must remain compatible");
        var absent = H3cComwareParser.ParseVersion("H3C Comware Software, Version 7.1.064\nBoot image: flash:/wx5540.bin\nType : WX5540");
        Assert(absent.Recognized && absent.Model is null && absent.Release is null,
            "image paths and board types are not chassis identity evidence");
        var conflict = H3cComwareParser.ParseVersion(ac + "\nH3C OTHER uptime is 1 day");
        Assert(conflict.Model is null, "conflicting chassis model lines must remain unknown");
        var unrecognized = H3cComwareParser.ParseVersion("H3C WX5540H-HCL uptime is 1 day");
        Assert(!unrecognized.Recognized && unrecognized.Model is null, "model alone cannot recognize Comware version");
        var facts = new[] { new H3cFactSnapshot("parsed", "p", "device.identity", "device", "STALE", "SIMULATOR", 1000, true,
            JsonSerializer.Serialize(parsed, new JsonSerializerOptions(JsonSerializerDefaults.Web))) };
        var view = H3cOperationalCapabilities.Project("p", facts, 1001);
        Assert(view.Model == "WX5540H-HCL" && view.Release == "Alpha 7165",
            "real parser output must reach operational capability metadata through normal fact serialization");
    }

    private static void Assert(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
