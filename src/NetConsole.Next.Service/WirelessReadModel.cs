using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NetConsole.Next.Service;

internal sealed record WirelessAcDeviceSeed(
    string DeviceId,
    string DisplayName,
    string SiteId,
    string ManagementIp,
    string Vendor,
    string Model,
    string ConnectionStatus,
    string? TerminalProfileId,
    string? Version,
    bool Enabled);

internal sealed record WirelessObservationSeed(
    string SourceAcDeviceId,
    string SourceAcName,
    string SourceSiteId,
    string? SourceAcModel,
    string? SourceAcVersion,
    string ApName,
    string? SerialEvidence,
    IReadOnlyList<string> NormalizedMacSet,
    string? ApModel,
    string? ApGroup,
    string RawState,
    string NormalizedState,
    string StateConfidence,
    long CapturedMs,
    string RawManifestRef,
    string ParserRevision,
    string SourceRevision);

internal sealed record WirelessTopologyEvidence(
    string DomainId,
    string DomainKind,
    string TopologyStatus,
    IReadOnlyList<string> MemberDeviceIds,
    IReadOnlyList<string> SourceEvidenceRefs,
    bool Confirmed);

internal sealed record WirelessFixtureDocument(
    IReadOnlyList<WirelessAcDeviceSeed> Acs,
    IReadOnlyList<WirelessObservationSeed> Observations,
    IReadOnlyList<WirelessTopologyEvidence> TopologyEvidence);

internal sealed record WirelessScopeView(string Status, string ClaimsRevision);

internal sealed record WirelessAcMemberView(
    string DeviceId,
    string DisplayName,
    string SiteId,
    string ManagementIp,
    string Vendor,
    string Model,
    string? Version,
    string Role,
    string RuntimeState,
    string ConnectionStatus,
    bool Enabled);

internal sealed record WirelessAcDomainView(
    string Id,
    string DisplayName,
    string DomainKind,
    string TopologyStatus,
    IReadOnlyList<string> MemberDeviceIds,
    IReadOnlyList<WirelessAcMemberView> Members,
    IReadOnlyList<string> SourceEvidenceRefs,
    string SiteScopeStatus,
    string Revision,
    long AsOf);

internal sealed record WirelessApObservationView(
    string Id,
    string ResourceId,
    string SourceAcDeviceId,
    string SourceAcDomainId,
    string SourceSiteId,
    string ObservedApName,
    string? SerialEvidence,
    IReadOnlyList<string> NormalizedMacSet,
    string? ApModel,
    string? ApGroup,
    string RawState,
    string NormalizedState,
    string StateConfidence,
    long CapturedMs,
    long ReceivedMs,
    string RawManifestRef,
    string ParserRevision,
    string SourceRevision,
    string RelationStatus);

internal sealed record WirelessApResourceView(
    string Id,
    string DisplayName,
    string IdentityStatus,
    string? SerialEvidence,
    IReadOnlyList<string> NormalizedMacSet,
    IReadOnlyList<string> SourceObservationIds,
    IReadOnlyList<string> SourceAcDomainIds,
    string ScopeStatus,
    long FirstSeenMs,
    long LastSeenMs,
    string SourceRevision,
    string ProjectionRevision,
    string? ConflictReason,
    IReadOnlyList<WirelessApObservationView> Observations);

internal sealed record WirelessApGroupView(
    string Id,
    string SourceAcDeviceId,
    string SourceAcDomainId,
    string SourceGroupKey,
    string DisplayName,
    string GroupScope,
    IReadOnlyList<string> MemberResourceIds,
    string SourceRevision,
    string ScopeStatus);

internal sealed record WirelessTopologyView(
    string DomainId,
    string DomainKind,
    string TopologyStatus,
    IReadOnlyList<string> MemberDeviceIds,
    IReadOnlyList<string> SourceEvidenceRefs,
    bool Confirmed,
    string Revision,
    long AsOf);

internal sealed record WirelessProjectionSnapshot(
    IReadOnlyList<WirelessAcDomainView> AcDomains,
    IReadOnlyList<WirelessApResourceView> ApResources,
    IReadOnlyList<WirelessApGroupView> ApGroups,
    long AsOf,
    string SourceRevision,
    WirelessScopeView Scope,
    IReadOnlyList<string> Warnings);

internal sealed record WirelessPage<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    long Total,
    bool HasMore,
    long AsOf,
    WirelessScopeView Scope,
    string SourceRevision,
    IReadOnlyList<string> Warnings);

internal sealed class WirelessProjectionEngine
{
    public WirelessProjectionSnapshot Project(
        IReadOnlyList<WirelessAcDeviceSeed> acs,
        IReadOnlyList<WirelessObservationSeed> observations,
        IReadOnlyList<WirelessTopologyEvidence> topologyEvidence,
        WirelessScopeView scope)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var acById = acs.Where(ac => !string.IsNullOrWhiteSpace(ac.DeviceId))
            .GroupBy(ac => ac.DeviceId, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => item.Enabled).ThenBy(item => item.DisplayName, StringComparer.Ordinal).First())
            .ToDictionary(ac => ac.DeviceId, StringComparer.Ordinal);
        var visibleObservations = observations
            .Where(item => acById.TryGetValue(item.SourceAcDeviceId, out var ac) &&
                string.Equals(ac.SiteId, item.SourceSiteId, StringComparison.Ordinal))
            .Select(item => item with
            {
                SerialEvidence = NormalizeSerial(item.SerialEvidence),
                NormalizedMacSet = NormalizeMacSet(item.NormalizedMacSet),
                ApName = item.ApName?.Trim() ?? string.Empty
            })
            .ToArray();

        var domainsById = BuildDomains(acById, topologyEvidence);
        var domainByAc = domainsById.Values
            .SelectMany(domain => domain.MemberDeviceIds.Select(deviceId => (DeviceId: deviceId, Domain: domain)))
            .GroupBy(item => item.DeviceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.Domain.Id, StringComparer.Ordinal).First().Domain, StringComparer.Ordinal);
        var observationWithDomain = visibleObservations.Select(item =>
            (Seed: item, DomainId: domainByAc.TryGetValue(item.SourceAcDeviceId, out var domain) ? domain.Id : "ac-domain:" + item.SourceAcDeviceId))
            .DistinctBy(item => ObservationId(item.Seed, item.DomainId))
            .ToArray();

        var resources = BuildResources(observationWithDomain, scope);
        var groups = BuildGroups(observationWithDomain, resources, scope);
        var domains = domainByAc.Values
            .GroupBy(domain => domain.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .Select(domain => BuildDomain(domain, acById, observationWithDomain, scope, now))
            .OrderBy(item => item.DisplayName, StringComparer.Ordinal)
            .ToArray();
        var asOf = visibleObservations.Length == 0 ? now : visibleObservations.Max(item => item.CapturedMs);
        var sourceRevision = Fingerprint(
            acs.Select(ac => $"ac|{ac.DeviceId}|{ac.SiteId}|{ac.Model}|{ac.Version}")
                .Concat(visibleObservations.Select(item =>
                    $"obs|{item.SourceAcDeviceId}|{item.ApName}|{item.SerialEvidence}|{string.Join(',', item.NormalizedMacSet)}|{item.RawState}|{item.SourceRevision}"))
                .Concat(topologyEvidence.Select(item =>
                    $"topology|{item.DomainId}|{item.DomainKind}|{string.Join(',', item.MemberDeviceIds.Order(StringComparer.Ordinal))}|{item.TopologyStatus}")));
        return new(domains, resources, groups, asOf, sourceRevision, scope,
            visibleObservations.Length == 0 ? new[] { "NO_VISIBLE_AP_OBSERVATIONS" } : Array.Empty<string>());
    }

    private static IReadOnlyList<WirelessApResourceView> BuildResources(
        IReadOnlyList<(WirelessObservationSeed Seed, string DomainId)> observations,
        WirelessScopeView scope)
    {
        var bySerial = observations.Where(item => !string.IsNullOrWhiteSpace(item.Seed.SerialEvidence))
            .GroupBy(item => item.Seed.SerialEvidence!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => MacKey(item.Seed.NormalizedMacSet)).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var byMac = observations.SelectMany(item => item.Seed.NormalizedMacSet.Select(mac => (mac, item)))
            .GroupBy(item => item.mac, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.item.Seed.SerialEvidence ?? string.Empty).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);

        var grouped = observations.GroupBy(item => IdentityKey(item.Seed), StringComparer.Ordinal);
        var resources = new List<WirelessApResourceView>();
        foreach (var group in grouped)
        {
            var items = group.ToArray();
            var serial = items.Select(item => item.Seed.SerialEvidence).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            var macs = items.SelectMany(item => item.Seed.NormalizedMacSet).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var conflict = (serial is not null && bySerial.TryGetValue(serial, out var serialMacs) && serialMacs.Length > 1) ||
                           macs.Any(mac => byMac.TryGetValue(mac, out var macSerials) && macSerials.Length > 1);
            var identityStatus = conflict ? "CONFLICT" : serial is not null && macs.Length > 0 ? "CONFIRMED" : serial is not null || macs.Length > 0 ? "CANDIDATE" : "UNKNOWN";
            var sourceObservationIds = items.Select(item => ObservationId(item.Seed, item.DomainId)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var resourceId = "ap-resource:" + Fingerprint(new[] { group.Key });
            if (identityStatus == "UNKNOWN") resourceId = "ap-candidate:" + Fingerprint(sourceObservationIds);
            var views = items.Select(item => ToObservationView(item.Seed, item.DomainId, resourceId)).OrderBy(item => item.CapturedMs).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
            var sourceRevision = Fingerprint(views.Select(item => item.SourceRevision));
            resources.Add(new(resourceId,
                views.Select(item => item.ObservedApName).FirstOrDefault(name => name.Length > 0) ?? "未命名 AP",
                identityStatus,
                serial,
                macs,
                sourceObservationIds,
                views.Select(item => item.SourceAcDomainId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                scope.Status,
                views.Min(item => item.CapturedMs),
                views.Max(item => item.CapturedMs),
                sourceRevision,
                Fingerprint(new[] { resourceId, sourceRevision }),
                conflict ? "SERIAL_MAC_EVIDENCE_CONFLICT" : null,
                views));
        }
        return resources.OrderBy(item => item.DisplayName, StringComparer.Ordinal).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<WirelessApGroupView> BuildGroups(
        IReadOnlyList<(WirelessObservationSeed Seed, string DomainId)> observations,
        IReadOnlyList<WirelessApResourceView> resources,
        WirelessScopeView scope)
    {
        var resourceByObservation = resources.SelectMany(resource => resource.Observations.Select(observation =>
                (ObservationId: observation.Id, ResourceId: resource.Id)))
            .GroupBy(item => item.ObservationId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().ResourceId, StringComparer.Ordinal);
        return observations.Where(item => !string.IsNullOrWhiteSpace(item.Seed.ApGroup))
            .GroupBy(item => (item.Seed.SourceAcDeviceId, GroupKey(item.Seed.ApGroup)), item => item)
            .Select(group =>
            {
                var memberIds = group.Select(item => resourceByObservation.GetValueOrDefault(ObservationId(item.Seed, item.DomainId)))
                    .Where(id => id is not null).Cast<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                var sourceRevision = Fingerprint(group.Select(item => item.Seed.SourceRevision));
                return new WirelessApGroupView(
                    "ap-group:" + Fingerprint(new[] { group.Key.SourceAcDeviceId, group.Key.Item2 }),
                    group.Key.SourceAcDeviceId,
                    group.First().DomainId,
                    group.Key.Item2,
                    group.First().Seed.ApGroup!.Trim(),
                    "AC_OWNED",
                    memberIds,
                    sourceRevision,
                    scope.Status);
            })
            .OrderBy(item => item.DisplayName, StringComparer.Ordinal)
            .ThenBy(item => item.SourceAcDeviceId, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyDictionary<string, WirelessDomainSeed> BuildDomains(
        IReadOnlyDictionary<string, WirelessAcDeviceSeed> acs,
        IReadOnlyList<WirelessTopologyEvidence> evidence)
    {
        var result = new Dictionary<string, WirelessDomainSeed>(StringComparer.Ordinal);
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in evidence.Where(item => item.MemberDeviceIds.Count > 0))
        {
            var members = item.MemberDeviceIds.Where(acs.ContainsKey).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (members.Length == 0) continue;
            var id = string.IsNullOrWhiteSpace(item.DomainId) ? "ac-domain:" + Fingerprint(members) : item.DomainId.Trim();
            var kind = string.IsNullOrWhiteSpace(item.DomainKind) ? "UNKNOWN" : item.DomainKind.Trim().ToUpperInvariant();
            result[id] = new(id, members, kind,
                string.IsNullOrWhiteSpace(item.TopologyStatus) ? item.Confirmed ? "CONFIRMED" : "EVIDENCE_PENDING" : item.TopologyStatus,
                item.SourceEvidenceRefs.Where(refId => !string.IsNullOrWhiteSpace(refId)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                item.Confirmed);
            foreach (var member in members) claimed.Add(member);
        }
        foreach (var ac in acs.Values.Where(item => !claimed.Contains(item.DeviceId)).OrderBy(item => item.DeviceId, StringComparer.Ordinal))
        {
            var id = "ac-domain:" + ac.DeviceId;
            result[id] = new(id, new[] { ac.DeviceId }, "UNKNOWN", "EVIDENCE_PENDING", Array.Empty<string>(), false);
        }
        return result;
    }

    private static WirelessAcDomainView BuildDomain(
        WirelessDomainSeed domain,
        IReadOnlyDictionary<string, WirelessAcDeviceSeed> acs,
        IReadOnlyList<(WirelessObservationSeed Seed, string DomainId)> observations,
        WirelessScopeView scope,
        long now)
    {
        var sourceObservations = observations.Where(item => item.DomainId == domain.Id).ToArray();
        var members = domain.MemberDeviceIds.Select(id => acs[id]).Select(ac => new WirelessAcMemberView(
            ac.DeviceId, ac.DisplayName, ac.SiteId, ac.ManagementIp, ac.Vendor, ac.Model, ac.Version,
            RoleFor(domain, ac.DeviceId), RuntimeFor(ac, sourceObservations), ac.ConnectionStatus, ac.Enabled)).ToArray();
        var revision = Fingerprint(new[] { domain.Id, domain.DomainKind, string.Join(',', domain.MemberDeviceIds), string.Join(',', domain.SourceEvidenceRefs) });
        var asOf = sourceObservations.Length == 0 ? now : sourceObservations.Max(item => item.Seed.CapturedMs);
        return new(domain.Id,
            members.Length == 1 ? members[0].DisplayName : $"{domain.Id} 无线域",
            domain.DomainKind,
            domain.TopologyStatus,
            domain.MemberDeviceIds,
            members,
            domain.SourceEvidenceRefs,
            scope.Status,
            revision,
            asOf);
    }

    private static WirelessApObservationView ToObservationView(WirelessObservationSeed seed, string domainId, string resourceId) =>
        new(ObservationId(seed, domainId), resourceId, seed.SourceAcDeviceId, domainId, seed.SourceSiteId,
            seed.ApName, seed.SerialEvidence, seed.NormalizedMacSet, seed.ApModel, seed.ApGroup,
            seed.RawState, seed.NormalizedState, seed.StateConfidence, seed.CapturedMs, seed.CapturedMs,
            seed.RawManifestRef, seed.ParserRevision, seed.SourceRevision,
            seed.NormalizedState == "CONFLICT" ? "CONFLICT" : "OBSERVED");

    private static string RuntimeFor(WirelessAcDeviceSeed ac, IReadOnlyList<(WirelessObservationSeed Seed, string DomainId)> observations) =>
        observations.Any(item => item.Seed.SourceAcDeviceId == ac.DeviceId && item.Seed.NormalizedState == "RUNNING_MASTER") ? "RUNNING_MASTER" :
        observations.Any(item => item.Seed.SourceAcDeviceId == ac.DeviceId && item.Seed.NormalizedState == "RUNNING_BACKUP") ? "RUNNING_BACKUP" :
        observations.Any(item => item.Seed.SourceAcDeviceId == ac.DeviceId) ? "UNKNOWN" : "STALE";

    private static string RoleFor(WirelessDomainSeed domain, string deviceId) =>
        domain.DomainKind == "ONE_PLUS_ONE" ? "ROLE_EVIDENCE_PENDING" : "MEMBER";

    private static string IdentityKey(WirelessObservationSeed seed) =>
        seed.SerialEvidence is { Length: > 0 } || seed.NormalizedMacSet.Count > 0
            ? $"evidence|{seed.SerialEvidence}|{MacKey(seed.NormalizedMacSet)}"
            : "unknown|" + ObservationId(seed, seed.SourceAcDeviceId);

    private static string ObservationId(WirelessObservationSeed seed, string domainId) =>
        "ap-observation:" + Fingerprint(new[] { domainId, seed.SourceAcDeviceId, seed.SourceRevision, seed.ApName, seed.SerialEvidence ?? string.Empty, MacKey(seed.NormalizedMacSet), seed.CapturedMs.ToString(System.Globalization.CultureInfo.InvariantCulture) });

    private static string GroupKey(string? value) => (value ?? string.Empty).Trim();
    private static string MacKey(IEnumerable<string> values) => string.Join(',', values.Order(StringComparer.Ordinal));

    internal static string? NormalizeSerial(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length == 0 ? null : normalized.ToUpperInvariant();
    }

    internal static string NormalizeMac(string value)
    {
        var input = (value ?? string.Empty).Trim();
        // Accept only complete common representations, never discard arbitrary characters.
        if (!System.Text.RegularExpressions.Regex.IsMatch(input,
                @"\A(?:[0-9A-Fa-f]{12}|(?:[0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}|(?:[0-9A-Fa-f]{2}-){5}[0-9A-Fa-f]{2}|(?:[0-9A-Fa-f]{4}-){2}[0-9A-Fa-f]{4}|(?:[0-9A-Fa-f]{4}\.){2}[0-9A-Fa-f]{4})\z"))
            return string.Empty;
        var compact = new string(input.Where(Uri.IsHexDigit).ToArray());
        return compact.Length == 12
            ? string.Join(':', Enumerable.Range(0, 6).Select(index => compact[(index * 2)..(index * 2 + 2)].ToUpperInvariant()))
            : string.Empty;
    }

    internal static IReadOnlyList<string> NormalizeMacSet(IEnumerable<string> values) =>
        values.Select(NormalizeMac).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    internal static string Fingerprint(IEnumerable<string> values)
    {
        var payload = string.Join('\n', values.Order(StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant()[..24];
    }

    private sealed record WirelessDomainSeed(
        string Id,
        IReadOnlyList<string> MemberDeviceIds,
        string DomainKind,
        string TopologyStatus,
        IReadOnlyList<string> SourceEvidenceRefs,
        bool Confirmed);
}

internal sealed class WirelessReadModelService
{
    private readonly Catalog _catalog;
    private readonly WirelessProjectionEngine _engine = new();

    public WirelessReadModelService(Catalog catalog) => _catalog = catalog;

    public WirelessProjectionSnapshot GetSnapshot(P2B5SiteScopeDecision scope)
    {
        var fixturePath = Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_WIRELESS_FIXTURE_PATH");
        if (!string.IsNullOrWhiteSpace(fixturePath))
        {
            var json = File.ReadAllText(fixturePath);
            var fixture = JsonSerializer.Deserialize<WirelessFixtureDocument>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidDataException("Wireless fixture is empty.");
            var acs = fixture.Acs.Where(ac => DeviceSiteScope.Allows(scope, ac.SiteId)).ToArray();
            var allowedAcIds = acs.Select(ac => ac.DeviceId).ToHashSet(StringComparer.Ordinal);
            var observations = fixture.Observations.Where(item => allowedAcIds.Contains(item.SourceAcDeviceId) && DeviceSiteScope.Allows(scope, item.SourceSiteId)).ToArray();
            var topology = fixture.TopologyEvidence.Where(item => item.MemberDeviceIds.All(allowedAcIds.Contains)).ToArray();
            var snapshot = _engine.Project(acs, observations, topology, scope.IsGlobal ? new("FULL", ClaimsRevision(scope)) : new("PARTIAL_SCOPE", ClaimsRevision(scope)));
            return snapshot with { Warnings = snapshot.Warnings.Append("OFFLINE_FIXTURE_EVIDENCE").ToArray() };
        }

        var catalog = _catalog.QueryWirelessCatalog(scope);
        return _engine.Project(catalog.Acs, catalog.Observations, catalog.TopologyEvidence,
            scope.IsGlobal ? new("FULL", ClaimsRevision(scope)) : new("PARTIAL_SCOPE", ClaimsRevision(scope)));
    }

    private static string ClaimsRevision(P2B5SiteScopeDecision scope) =>
        scope.IsGlobal ? "GLOBAL" : WirelessProjectionEngine.Fingerprint(new[] { scope.State, string.Join(',', scope.AllowedSiteReferences.Order(StringComparer.Ordinal)) });
}

internal sealed record WirelessCatalogSnapshot(
    IReadOnlyList<WirelessAcDeviceSeed> Acs,
    IReadOnlyList<WirelessObservationSeed> Observations,
    IReadOnlyList<WirelessTopologyEvidence> TopologyEvidence);

internal sealed partial class Catalog
{
    public WirelessCatalogSnapshot QueryWirelessCatalog(P2B5SiteScopeDecision scope)
    {
        var inventory = new List<DeviceInventoryItem>();
        for (var page = 1; ; page++)
        {
            var batch = QueryManagedDeviceInventory(page, 100, deviceType: "AC", scope: scope);
            inventory.AddRange(batch.Items);
            if (!batch.HasMore) break;
        }
        var acs = new List<WirelessAcDeviceSeed>();
        var observations = new List<WirelessObservationSeed>();
        foreach (var device in inventory)
        {
            var profileId = device.TerminalProfileId;
            string? version = null;
            acs.Add(new(device.Id, device.Name, device.SiteId, device.ManagementIp, device.Vendor, device.Model,
                device.ConnectionStatus, profileId, version, device.Enabled));
            if (string.IsNullOrWhiteSpace(profileId)) continue;
            var facts = QueryH3cFacts(profileId, history: false, limit: 1000);
            version = ReadVersion(facts.FirstOrDefault(item => item.Kind == "device.identity")?.PayloadJson);
            if (version is not null)
            {
                var index = acs.FindIndex(item => item.DeviceId == device.Id);
                if (index >= 0) acs[index] = acs[index] with { Version = version };
            }

            var apFact = facts.Where(item => item.Kind == "command.display.wlan.ap.all")
                .OrderByDescending(item => item.CapturedMs).FirstOrDefault();
            if (apFact is null) continue;
            var addressFact = facts.Where(item => item.Kind == "command.display.wlan.ap.all.address")
                .OrderByDescending(item => item.CapturedMs).FirstOrDefault();
            var addressByName = ParseAddressFacts(addressFact?.PayloadJson);
            var apRows = H3cComwareParser.ParseWlanApAll(ReadOutput(apFact.PayloadJson));
            foreach (var row in apRows)
            {
                addressByName.TryGetValue(row.Name, out var address);
                var rawState = (row.State ?? string.Empty).Trim().ToUpperInvariant();
                var normalized = rawState switch
                {
                    "R/M" or "RM" => "RUNNING_MASTER",
                    "R/B" or "RB" => "RUNNING_BACKUP",
                    "I" => "OFFLINE",
                    _ => "UNKNOWN"
                };
                observations.Add(new(device.Id, device.Name, device.SiteId, device.Model, version, row.Name,
                    row.Serial, address is null ? Array.Empty<string>() : new[] { address }, row.Model, row.Group,
                    rawState, normalized, normalized == "UNKNOWN" ? "LOW" : "HIGH", row.OnlineTime is null ? apFact.CapturedMs : apFact.CapturedMs,
                    "fact:" + apFact.Id, "h3c.ap.v1", apFact.Id));
            }
        }
        return new(acs, observations, Array.Empty<WirelessTopologyEvidence>());
    }

    private static string ReadOutput(string payload)
    {
        try
        {
            using var json = JsonDocument.Parse(payload);
            return json.RootElement.TryGetProperty("output", out var output) ? output.GetString() ?? string.Empty : string.Empty;
        }
        catch (JsonException) { return string.Empty; }
    }

    private static string? ReadVersion(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;
        try
        {
            using var json = JsonDocument.Parse(payload);
            if (json.RootElement.TryGetProperty("version", out var version)) return version.GetString();
            if (json.RootElement.TryGetProperty("Version", out version)) return version.GetString();
        }
        catch (JsonException) { }
        return null;
    }

    private static Dictionary<string, string> ParseAddressFacts(string? payload)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var output = ReadOutput(payload ?? string.Empty);
        foreach (var row in H3cComwareParser.ParseWlanApAllAddress(output))
            result[row.Name] = row.MacAddress ?? string.Empty;
        return result;
    }
}
