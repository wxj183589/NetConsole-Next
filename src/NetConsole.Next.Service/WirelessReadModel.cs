using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

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

// A source group key must come from explicit vendor/source evidence. AP group
// display text in an AP listing is deliberately insufficient to populate it.
internal sealed record WirelessSourceGroupEvidence(
    string SourceAcDeviceId,
    string SourceSiteId,
    string? SourceGroupKey,
    string DisplayName,
    IReadOnlyList<string> SourceObservationIds,
    long CapturedMs,
    string SourceRevision,
    string RawManifestRef,
    string ParserRevision,
    string? SourceAcDomainId = null,
    IReadOnlyList<string>? DomainMemberDeviceIds = null,
    IReadOnlyList<string>? DomainSharedEvidenceRefs = null);

internal sealed record WirelessFixtureDocument(
    IReadOnlyList<WirelessAcDeviceSeed> Acs,
    IReadOnlyList<WirelessObservationSeed> Observations,
    IReadOnlyList<WirelessTopologyEvidence> TopologyEvidence,
    IReadOnlyList<WirelessRuntimeSample>? RuntimeSamples = null,
    IReadOnlyList<WirelessSourceGroupEvidence>? GroupEvidence = null);

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
    IReadOnlyList<WirelessApObservationView> Observations,
    string? StateConflictReason = null)
{
    public string IdentityRevision => WirelessIdentityContract.ComputeRevision(this);
}

internal sealed record WirelessApGroupView(
    string Id,
    string SourceAcDeviceId,
    string SourceAcDomainId,
    string? SourceGroupKey,
    string DisplayName,
    string GroupScope,
    IReadOnlyList<string> MemberResourceIds,
    string SourceRevision,
    string ScopeStatus,
    string? GroupIdentityKey,
    string GroupIdentityStatus,
    string MembershipRevision,
    IReadOnlyList<string> SourceObservationIds,
    IReadOnlyList<string> SourceAcDeviceIds,
    IReadOnlyList<string> SourceEvidenceRefs,
    long ObservedAtMs,
    string? ConflictReason,
    IReadOnlyList<WirelessSourceGroupEvidence> SourceGroupObservations);

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

internal sealed partial class WirelessProjectionEngine
{
    public WirelessProjectionSnapshot Project(
        IReadOnlyList<WirelessAcDeviceSeed> acs,
        IReadOnlyList<WirelessObservationSeed> observations,
        IReadOnlyList<WirelessTopologyEvidence> topologyEvidence,
        WirelessScopeView scope,
        IReadOnlyList<WirelessSourceGroupEvidence>? groupEvidence = null,
        string? sourceAcId = null)
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
            .DistinctBy(item => (ObservationId(item.Seed, item.DomainId), item.Seed.RawState, item.Seed.NormalizedState))
            .ToArray();

        var resources = BuildResources(observationWithDomain, scope);
        if (sourceAcId is not null)
        {
            // Source filtering must keep the same opaque physical AP locator as
            // the complete authorized projection. Summaries and group evidence
            // are still rebuilt solely from the selected source observations.
            var resourceIds = resources.SelectMany(item => item.Observations)
                .GroupBy(item => BaseObservationId(item.Id), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().ResourceId, StringComparer.Ordinal);
            visibleObservations = visibleObservations.Where(item => item.SourceAcDeviceId == sourceAcId).ToArray();
            observationWithDomain = observationWithDomain.Where(item => item.Seed.SourceAcDeviceId == sourceAcId).ToArray();
            resources = BuildResources(observationWithDomain, scope, resourceIds);
        }
        var groups = BuildGroups(resources, acById, domainsById,
            (groupEvidence ?? Array.Empty<WirelessSourceGroupEvidence>()).Where(item => item is not null &&
                (sourceAcId is null || item.SourceAcDeviceId == sourceAcId)).ToArray(), scope);
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
                    $"topology|{item.DomainId}|{item.DomainKind}|{string.Join(',', item.MemberDeviceIds.Order(StringComparer.Ordinal))}|{item.TopologyStatus}"))
                .Concat(groups.Select(item => $"group|{item.Id}|{item.SourceRevision}|{item.MembershipRevision}|{item.GroupIdentityStatus}")));
        return new(domains, resources, groups, asOf, sourceRevision, scope,
            visibleObservations.Length == 0 ? new[] { "NO_VISIBLE_AP_OBSERVATIONS" } : Array.Empty<string>());
    }

    private static IReadOnlyList<WirelessApResourceView> BuildResources(
        IReadOnlyList<(WirelessObservationSeed Seed, string DomainId)> observations,
        WirelessScopeView scope,
        IReadOnlyDictionary<string, string>? resourceIdsByObservation = null)
    {
        var bySerial = observations.Where(item => !string.IsNullOrWhiteSpace(item.Seed.SerialEvidence) && item.Seed.NormalizedMacSet.Count > 0)
            .GroupBy(item => item.Seed.SerialEvidence!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => MacKey(item.Seed.NormalizedMacSet)).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        // A missing serial is incomplete evidence, not a conflicting serial. Only
        // distinct non-empty serials make a MAC ambiguous. This lets a MAC-only
        // observation join one exact serial+MAC group while still refusing a
        // MAC shared by two different serials. Serial-only observations remain
        // candidates because a serial without a hardware address is incomplete.
        var byMac = observations.SelectMany(item => item.Seed.NormalizedMacSet
                .Select(mac => (mac, Serial: item.Seed.SerialEvidence ?? string.Empty)))
            .Where(item => item.Serial.Length > 0)
            .GroupBy(item => item.mac, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Serial).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);

        var exactGroups = observations
            .Where(item => !string.IsNullOrWhiteSpace(item.Seed.SerialEvidence) && item.Seed.NormalizedMacSet.Count > 0)
            .GroupBy(item => IdentityKey(item.Seed), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var exactByMac = exactGroups.Values
            .SelectMany(item => item.Seed.NormalizedMacSet.Select(mac => (mac, Key: IdentityKey(item.Seed))))
            .GroupBy(item => item.mac, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Key).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);

        // Merge incomplete observations only when their exact evidence resolves
        // to one and only one established group. Ambiguous evidence keeps its
        // own candidate resource and is surfaced below as CONFLICT.
        var grouped = observations
            .GroupBy(item => resourceIdsByObservation is not null
                ? resourceIdsByObservation[ObservationId(item.Seed, item.DomainId)] : ResolveIdentityGroupKey(item, exactByMac), StringComparer.Ordinal);
        var resources = new List<WirelessApResourceView>();
        foreach (var group in grouped)
        {
            var items = group.ToArray();
            var serial = items.Select(item => item.Seed.SerialEvidence).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            var macs = items.SelectMany(item => item.Seed.NormalizedMacSet).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var conflict = (serial is not null && bySerial.TryGetValue(serial, out var serialMacs) && serialMacs.Length > 1) ||
                           macs.Any(mac => byMac.TryGetValue(mac, out var macSerials) && macSerials.Length > 1);
            var incompleteEvidence = items.Any(item => string.IsNullOrWhiteSpace(item.Seed.SerialEvidence) || item.Seed.NormalizedMacSet.Count == 0);
            var identityStatus = conflict ? "CONFLICT" : serial is not null && macs.Length > 0 && !incompleteEvidence ? "CONFIRMED" : serial is not null || macs.Length > 0 ? "CANDIDATE" : "UNKNOWN";
            var sourceObservationIds = items.Select(item => ObservationId(item.Seed, item.DomainId)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var resourceId = "ap-resource:" + Fingerprint(new[] { group.Key });
            if (identityStatus == "UNKNOWN") resourceId = "ap-candidate:" + Fingerprint(sourceObservationIds);
            if (resourceIdsByObservation is not null) resourceId = group.Key;
            var views = items.Select(item => ToObservationView(item.Seed, item.DomainId, resourceId)).OrderBy(item => item.CapturedMs).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
            var conflictingIds = views.GroupBy(item => item.Id, StringComparer.Ordinal)
                .Where(group => group.Select(item => item.NormalizedState).Distinct(StringComparer.Ordinal).Count() > 1)
                .Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
            views = views.Select(item => conflictingIds.Contains(item.Id) ? item with
            {
                Id = item.Id + ":conflict:" + Fingerprint(new[] { item.RawState, item.NormalizedState }),
                NormalizedState = "CONFLICT", StateConfidence = "LOW", RelationStatus = "CONFLICT"
            } : item).ToArray();
            sourceObservationIds = views.Select(item => item.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
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
                Fingerprint(new[] { resourceId, sourceRevision }.Concat(views.Select(item => $"{item.Id}|{item.RawState}|{item.NormalizedState}"))),
                conflict ? "SERIAL_MAC_EVIDENCE_CONFLICT" : null,
                views,
                views.Where(item => item.NormalizedState == "RUNNING_MASTER").Select(item => item.SourceAcDeviceId).Distinct(StringComparer.Ordinal).Count() > 1
                    ? "MULTI_SOURCE_MASTER" : conflictingIds.Count > 0 ? "SAME_REVISION_STATE_CONFLICT" : null));
        }
        return resources.OrderBy(item => item.DisplayName, StringComparer.Ordinal).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
    }

    private static string ResolveIdentityGroupKey(
        (WirelessObservationSeed Seed, string DomainId) item,
        IReadOnlyDictionary<string, string[]> exactByMac)
    {
        var seed = item.Seed;
        if (!string.IsNullOrWhiteSpace(seed.SerialEvidence) && seed.NormalizedMacSet.Count > 0)
            return IdentityKey(seed);

        if (seed.NormalizedMacSet.Count > 0)
        {
            var matching = seed.NormalizedMacSet
                .Where(exactByMac.ContainsKey)
                .SelectMany(mac => exactByMac[mac])
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (matching.Length == 1) return matching[0];
        }

        if (!string.IsNullOrWhiteSpace(seed.SerialEvidence))
            return "serial|" + seed.SerialEvidence;
        if (seed.NormalizedMacSet.Count > 0)
            return "mac|" + MacKey(seed.NormalizedMacSet);
        return "unknown|" + ObservationId(seed, item.DomainId);
    }

    private static IReadOnlyDictionary<string, WirelessDomainSeed> BuildDomains(
        IReadOnlyDictionary<string, WirelessAcDeviceSeed> acs,
        IReadOnlyList<WirelessTopologyEvidence> evidence)
    {
        var result = new Dictionary<string, WirelessDomainSeed>(StringComparer.Ordinal);
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var candidates = evidence.Where(item => item.MemberDeviceIds.Count > 0)
            .GroupBy(item => string.IsNullOrWhiteSpace(item.DomainId)
                ? "ac-domain:" + Fingerprint(item.MemberDeviceIds.Distinct(StringComparer.Ordinal)) : item.DomainId.Trim(), StringComparer.Ordinal);
        foreach (var group in candidates)
        {
            var items = group.ToArray();
            var members = items.SelectMany(item => item.MemberDeviceIds).Where(acs.ContainsKey)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (members.Length == 0) continue;
            var signatures = items.Select(item => Fingerprint(new[] { item.DomainKind, item.TopologyStatus,
                string.Join(',', item.MemberDeviceIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)), item.Confirmed.ToString() }))
                .Distinct(StringComparer.Ordinal).ToArray();
            var conflict = signatures.Length > 1;
            var item = items[0];
            var refs = items.SelectMany(value => value.SourceEvidenceRefs).Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var complete = item.MemberDeviceIds.All(acs.ContainsKey);
            var kind = (item.DomainKind ?? "UNKNOWN").Trim().ToUpperInvariant();
            var confirmed = !conflict && complete && item.Confirmed && refs.Length > 0 &&
                kind is "STANDALONE" or "INDEPENDENT" or "STACK" or "IRF" or "CLUSTER" or "ONE_PLUS_ONE";
            result[group.Key] = new(group.Key, members, conflict ? "UNKNOWN" : kind,
                conflict ? "CONFLICT" : confirmed ? "CONFIRMED" : "EVIDENCE_PENDING", refs, confirmed);
        }
        // A member assigned to multiple domains is ambiguous. Preserve each AC as a
        // conflict candidate rather than choosing a domain by lexical sort order.
        var overlapping = result.Values.SelectMany(domain => domain.MemberDeviceIds)
            .GroupBy(id => id, StringComparer.Ordinal).Where(group => group.Count() > 1)
            .Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var conflictedMembers = result.Values.Where(domain => domain.MemberDeviceIds.Any(overlapping.Contains))
            .SelectMany(domain => domain.MemberDeviceIds).ToHashSet(StringComparer.Ordinal);
        foreach (var id in result.Values.Where(domain => domain.MemberDeviceIds.Any(conflictedMembers.Contains)).Select(domain => domain.Id).ToArray())
            result.Remove(id);
        foreach (var id in conflictedMembers)
            result["ac-domain:" + id] = new("ac-domain:" + id, new[] { id }, "UNKNOWN", "CONFLICT", Array.Empty<string>(), false);
        foreach (var domain in result.Values)
            foreach (var member in domain.MemberDeviceIds) claimed.Add(member);
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

    private static string RuntimeFor(WirelessAcDeviceSeed ac, IReadOnlyList<(WirelessObservationSeed Seed, string DomainId)> observations)
    {
        var own = observations.Where(item => item.Seed.SourceAcDeviceId == ac.DeviceId).ToArray();
        if (own.GroupBy(item => (IdentityKey(item.Seed), item.Seed.SourceRevision, item.Seed.CapturedMs))
            .Any(group => group.Select(item => item.Seed.NormalizedState).Distinct(StringComparer.Ordinal).Count() > 1)) return "CONFLICT";
        if (own.Any(item => item.Seed.NormalizedState == "RUNNING_MASTER")) return "RUNNING_MASTER";
        if (own.Any(item => item.Seed.NormalizedState == "RUNNING_BACKUP")) return "RUNNING_BACKUP";
        return own.Length == 0 || own.All(item => item.Seed.NormalizedState == "STALE") ? "STALE" : "UNKNOWN";
    }

    private static string RoleFor(WirelessDomainSeed domain, string deviceId) =>
        domain.DomainKind == "ONE_PLUS_ONE" ? "ROLE_EVIDENCE_PENDING" : "MEMBER";

    private static string IdentityKey(WirelessObservationSeed seed) =>
        seed.SerialEvidence is { Length: > 0 } || seed.NormalizedMacSet.Count > 0
            ? $"evidence|{seed.SerialEvidence}|{MacKey(seed.NormalizedMacSet)}"
            : "unknown|" + ObservationId(seed, seed.SourceAcDeviceId);

    private static string ObservationId(WirelessObservationSeed seed, string domainId) =>
        "ap-observation:" + Fingerprint(new[] { domainId, seed.SourceAcDeviceId, seed.SourceRevision, seed.ApName, seed.SerialEvidence ?? string.Empty, MacKey(seed.NormalizedMacSet), seed.CapturedMs.ToString(System.Globalization.CultureInfo.InvariantCulture) });

    private static string BaseObservationId(string id) => id.Split(":conflict:", 2, StringSplitOptions.None)[0];

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

internal sealed partial class WirelessReadModelService
{
    private readonly Catalog _catalog;
    private readonly WirelessProjectionEngine _engine = new();

    public WirelessReadModelService(Catalog catalog) => _catalog = catalog;

    public WirelessProjectionSnapshot GetSnapshot(P2B5SiteScopeDecision scope) => GetSnapshotCore(scope, null, persist: true);

    public WirelessProjectionSnapshot GetGroupSnapshot(P2B5SiteScopeDecision scope, string? sourceAcId = null)
    {
        var snapshot = GetSnapshotCore(scope, sourceAcId, persist: false);
        return snapshot with
        {
            SourceRevision = WirelessProjectionEngine.Fingerprint(snapshot.ApGroups.Select(item => item.Id + "|" + item.SourceRevision + "|" + item.MembershipRevision)),
            AsOf = snapshot.ApGroups.Count == 0 ? 0 : snapshot.ApGroups.Max(item => item.ObservedAtMs)
        };
    }

    private WirelessProjectionSnapshot GetSnapshotCore(P2B5SiteScopeDecision scope, string? sourceAcId, bool persist)
    {
        var fixture = LoadFixture();
        if (fixture is not null) return ProjectFixture(fixture, scope, sourceAcId);

        var catalog = _catalog.QueryWirelessCatalog(scope, sourceAcId);
        var projected = _engine.Project(catalog.Acs, catalog.Observations, catalog.TopologyEvidence,
            scope.IsGlobal ? new("FULL", ClaimsRevision(scope)) : new("PARTIAL_SCOPE", ClaimsRevision(scope)),
            catalog.GroupEvidence, sourceAcId);
        if (persist && scope.IsGlobal)
        {
            try { _catalog.PersistWirelessProjection(projected); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or SqliteException)
            {
                projected = projected with { Warnings = projected.Warnings.Append("WIRELESS_PROJECTION_PERSISTENCE_FAILED").Distinct(StringComparer.Ordinal).ToArray() };
            }
        }
        return projected with { Warnings = projected.Warnings.Concat(catalog.Warnings ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToArray() };
    }

    private static WirelessFixtureDocument? LoadFixture()
    {
        var fixturePath = Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_WIRELESS_FIXTURE_PATH");
        if (string.IsNullOrWhiteSpace(fixturePath)) return null;
        return JsonSerializer.Deserialize<WirelessFixtureDocument>(File.ReadAllText(fixturePath), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Wireless fixture is empty.");
    }

    private WirelessProjectionSnapshot ProjectFixture(WirelessFixtureDocument fixture, P2B5SiteScopeDecision scope, string? sourceAcId = null)
    {
            var acs = fixture.Acs.Where(ac => DeviceSiteScope.Allows(scope, ac.SiteId)).ToArray();
            var allowedAcIds = acs.Select(ac => ac.DeviceId).ToHashSet(StringComparer.Ordinal);
            var observations = fixture.Observations.Where(item => allowedAcIds.Contains(item.SourceAcDeviceId) &&
                DeviceSiteScope.Allows(scope, item.SourceSiteId)).ToArray();
            var topology = fixture.TopologyEvidence.Where(item => item.MemberDeviceIds.All(allowedAcIds.Contains)).ToArray();
            var snapshot = _engine.Project(acs, observations, topology, scope.IsGlobal ? new("FULL", ClaimsRevision(scope)) : new("PARTIAL_SCOPE", ClaimsRevision(scope)),
                fixture.GroupEvidence, sourceAcId);
            return snapshot with { Warnings = snapshot.Warnings.Append("OFFLINE_FIXTURE_EVIDENCE").ToArray() };
    }

    private static string ClaimsRevision(P2B5SiteScopeDecision scope) =>
        scope.IsGlobal ? "GLOBAL" : scope.ClaimsRevision ?? WirelessProjectionEngine.Fingerprint(new[] { scope.State, string.Join(',', scope.AllowedSiteReferences.Order(StringComparer.Ordinal)) });
}

internal sealed record WirelessCatalogSnapshot(
    IReadOnlyList<WirelessAcDeviceSeed> Acs,
    IReadOnlyList<WirelessObservationSeed> Observations,
    IReadOnlyList<WirelessTopologyEvidence> TopologyEvidence,
    IReadOnlyList<string>? Warnings = null,
    IReadOnlyList<WirelessSourceGroupEvidence>? GroupEvidence = null);

internal sealed partial class Catalog
{
    public WirelessCatalogSnapshot QueryWirelessCatalog(P2B5SiteScopeDecision scope, string? sourceAcId = null)
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
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var device in inventory)
        {
            var profileId = device.TerminalProfileId;
            string? version = null;
            acs.Add(new(device.Id, device.Name, device.SiteId, device.ManagementIp, device.Vendor, device.Model,
                device.ConnectionStatus, profileId, version, device.Enabled));
            var selectedSource = sourceAcId is null || device.Id == sourceAcId;
            if (string.IsNullOrWhiteSpace(profileId)) continue;
            var facts = QueryH3cFacts(profileId, history: false, limit: 1000, scope: scope, requireCapturedScope: true);
            version = ReadVersion(facts.FirstOrDefault(item => item.Kind == "device.identity")?.PayloadJson);
            if (version is not null)
            {
                var index = acs.FindIndex(item => item.DeviceId == device.Id);
                if (index >= 0) acs[index] = acs[index] with { Version = version };
            }

            const string apKind = "command.display.wlan.ap.all";
            var latest = QueryH3cFacts(profileId, apKind, history: false, limit: 1, scope: scope, requireCapturedScope: true).FirstOrDefault();
            if (latest is null) continue;
            bool Usable(H3cFactSnapshot fact) => fact.Status is not ("PARTIAL" or "FAILED" or "UNSUPPORTED" or "EMPTY") &&
                H3cComwareParser.ParseWlanApAll(ReadOutput(fact.PayloadJson)).Count > 0;
            var apFact = Usable(latest) ? latest : QueryH3cFacts(profileId, apKind, history: true, limit: 1000, scope: scope, requireCapturedScope: true)
                .FirstOrDefault(Usable);
            if (apFact is null) { if (selectedSource) warnings.Add("AP_COLLECTION_UNAVAILABLE"); continue; }
            var preserved = apFact.Id != latest.Id;
            if (preserved && selectedSource) warnings.Add("AP_COLLECTION_FAILED_PREVIOUS_EVIDENCE_PRESERVED");
            if (apFact.Source == H3cOfflineFixtureSource && selectedSource) warnings.Add("OFFLINE_FIXTURE_EVIDENCE");
            // Only correlate address rows from the same captured source revision.
            const string addressKind = "command.display.wlan.ap.all.address";
            var addressFact = QueryH3cFacts(profileId, addressKind, history: false, limit: 1, scope: scope, requireCapturedScope: true)
                .Concat(QueryH3cFacts(profileId, addressKind, history: true, limit: 1000, scope: scope, requireCapturedScope: true))
                .FirstOrDefault(item => item.CapturedMs == apFact.CapturedMs && item.Source == apFact.Source &&
                    item.Status is not ("PARTIAL" or "FAILED" or "UNSUPPORTED" or "EMPTY"));
            var verifiedCollection = apFact.CollectedRawVerified && addressFact?.CollectedRawVerified == true;
            var addressByName = ParseAddressFacts(addressFact?.PayloadJson);
            var apRows = H3cComwareParser.ParseWlanApAll(ReadOutput(apFact.PayloadJson));
            foreach (var row in apRows)
            {
                addressByName.TryGetValue(row.Name, out var addresses);
                var rawState = (row.State ?? string.Empty).Trim().ToUpperInvariant();
                var normalized = rawState switch
                {
                    "R/M" or "RM" => "RUNNING_MASTER",
                    "R/B" or "RB" => "RUNNING_BACKUP",
                    "I" when version is not null => "OFFLINE",
                    _ => "UNKNOWN"
                };
                if (preserved || !verifiedCollection || apFact.Source == H3cOfflineFixtureSource) normalized = "STALE";
                observations.Add(new(device.Id, device.Name, device.SiteId, device.Model, version, row.Name,
                    row.Serial, addresses ?? Array.Empty<string>(), row.Model, row.Group,
                    rawState, normalized, normalized is "UNKNOWN" or "STALE" ? "LOW" : "HIGH", apFact.CapturedMs,
                    "fact:" + apFact.Id, "h3c.ap.v1", apFact.Id));
            }
        }
        return new(acs, observations, Array.Empty<WirelessTopologyEvidence>(), warnings.Order(StringComparer.Ordinal).ToArray());
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

    private static Dictionary<string, string[]> ParseAddressFacts(string? payload)
    {
        var output = ReadOutput(payload ?? string.Empty);
        return H3cComwareParser.ParseWlanApAllAddress(output)
            .GroupBy(row => row.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(row => row.MacAddress ?? string.Empty)
                .Where(mac => !string.IsNullOrWhiteSpace(mac)).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
    }
}
