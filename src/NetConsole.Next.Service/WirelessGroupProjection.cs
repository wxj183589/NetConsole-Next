using System.Globalization;
using System.Text.Json;

namespace NetConsole.Next.Service;

internal sealed partial class WirelessProjectionEngine
{
    private sealed record GroupEvidenceProjection(WirelessSourceGroupEvidence Evidence,
        string DomainId, string GroupScope, string? IdentityKey, string Status, string? Reason);

    private static IReadOnlyList<WirelessApGroupView> BuildGroups(
        IReadOnlyList<WirelessApResourceView> resources,
        IReadOnlyDictionary<string, WirelessAcDeviceSeed> acs,
        IReadOnlyDictionary<string, WirelessDomainSeed> domains,
        IReadOnlyList<WirelessSourceGroupEvidence> evidence,
        WirelessScopeView scope)
    {
        var observations = resources.SelectMany(item => item.Observations).ToArray();
        var byId = observations.GroupBy(item => item.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var domainByAc = domains.Values.SelectMany(domain => domain.MemberDeviceIds.Select(id => (id, domain)))
            .GroupBy(item => item.id, StringComparer.Ordinal).ToDictionary(group => group.Key,
                group => group.OrderBy(item => item.domain.Id, StringComparer.Ordinal).First().domain, StringComparer.Ordinal);
        var visible = new List<WirelessSourceGroupEvidence>();
        foreach (var item in evidence)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.SourceAcDeviceId) ||
                !acs.TryGetValue(item.SourceAcDeviceId, out var ac) || ac.SiteId != item.SourceSiteId ||
                item.SourceObservationIds is null || item.SourceObservationIds.Any(id =>
                    string.IsNullOrWhiteSpace(id) || !byId.TryGetValue(id, out var observation) || observation.SourceAcDeviceId != ac.DeviceId || observation.SourceSiteId != ac.SiteId))
                continue;
            var normalized = item with
            {
                SourceGroupKey = NullIfEmpty(item.SourceGroupKey), DisplayName = item.DisplayName?.Trim() ?? string.Empty,
                SourceObservationIds = Sorted(item.SourceObservationIds),
                DomainMemberDeviceIds = Sorted(item.DomainMemberDeviceIds ?? Array.Empty<string>()),
                DomainSharedEvidenceRefs = Sorted(item.DomainSharedEvidenceRefs ?? Array.Empty<string>())
            };
            // A partial Site view cannot use or expose another AC's domain evidence.
            if (normalized.DomainMemberDeviceIds.Any(id => !acs.ContainsKey(id)))
                normalized = normalized with { SourceAcDomainId = null, DomainMemberDeviceIds = Array.Empty<string>(), DomainSharedEvidenceRefs = Array.Empty<string>() };
            visible.Add(normalized);
        }

        var claimed = visible.SelectMany(item => item.SourceObservationIds).ToHashSet(StringComparer.Ordinal);
        // AP listing text only establishes a provisional relation within this
        // source revision. It never supplies a vendor key or a stable identity.
        visible.AddRange(observations.Where(item => !claimed.Contains(item.Id) && !string.IsNullOrWhiteSpace(item.ApGroup))
            .GroupBy(item => (item.SourceAcDeviceId, item.SourceRevision, item.CapturedMs, Name: item.ApGroup!.Trim()))
            .Select(group => new WirelessSourceGroupEvidence(group.Key.SourceAcDeviceId, group.First().SourceSiteId,
                null, group.Key.Name, Sorted(group.Select(item => item.Id)), group.Key.CapturedMs,
                group.Key.SourceRevision, group.First().RawManifestRef, group.First().ParserRevision)));

        var projected = visible.Select(item =>
        {
            var domain = domainByAc[item.SourceAcDeviceId];
            var sufficient = item.SourceGroupKey is not null && item.CapturedMs > 0 &&
                !string.IsNullOrWhiteSpace(item.SourceRevision) && !string.IsNullOrWhiteSpace(item.RawManifestRef) && !string.IsNullOrWhiteSpace(item.ParserRevision);
            if (!sufficient)
                return new GroupEvidenceProjection(item, domain.Id, "AC", null,
                    item.DisplayName.Length > 0 || item.SourceGroupKey is not null ? "CANDIDATE" : "UNKNOWN", "GROUP_SOURCE_KEY_OR_EVIDENCE_MISSING");
            var groupScope = "AC";
            string? reason = null;
            var status = "CONFIRMED";
            if (item.DomainSharedEvidenceRefs!.Count > 0)
            {
                if (domain.Confirmed && item.SourceAcDomainId == domain.Id &&
                    item.DomainMemberDeviceIds!.SequenceEqual(domain.MemberDeviceIds.Order(StringComparer.Ordinal), StringComparer.Ordinal)) groupScope = "DOMAIN";
                else
                {
                    status = domain.Confirmed ? "CONFLICT" : "CANDIDATE";
                    reason = domain.Confirmed ? "DOMAIN_SHARED_EVIDENCE_CONFLICT" : "DOMAIN_SHARED_EVIDENCE_PENDING";
                }
            }
            var identityKey = groupScope + "|" + (groupScope == "DOMAIN" ? domain.Id : item.SourceAcDeviceId) + "|" + item.SourceGroupKey;
            return new GroupEvidenceProjection(item, domain.Id, groupScope, status == "CANDIDATE" ? null : identityKey, status, reason);
        }).ToArray();

        var groups = projected.GroupBy(item => item.IdentityKey ?? "candidate:" + Fingerprint(new[]
            { item.Evidence.SourceAcDeviceId, item.Evidence.SourceRevision, item.Evidence.RawManifestRef,
                item.Evidence.CapturedMs.ToString(CultureInfo.InvariantCulture), item.Evidence.DisplayName }
            .Concat(item.Evidence.SourceObservationIds)), StringComparer.Ordinal)
            .Select(group => BuildGroup(group.ToArray(), byId, scope)).ToArray();
        var ambiguousNames = groups.Where(item => item.SourceGroupKey is not null)
            .GroupBy(item => (item.GroupScope, Scope: item.GroupScope == "DOMAIN" ? item.SourceAcDomainId : item.SourceAcDeviceId, item.DisplayName))
            .Where(group => group.Select(item => item.SourceGroupKey).Distinct(StringComparer.Ordinal).Count() > 1)
            .SelectMany(group => group.Select(item => item.Id)).ToHashSet(StringComparer.Ordinal);
        return groups.Select(item => ambiguousNames.Contains(item.Id)
                ? item with { GroupIdentityStatus = "CONFLICT", ConflictReason = "GROUP_NAME_KEY_CONFLICT" } : item)
            .OrderBy(item => item.DisplayName, StringComparer.Ordinal).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
    }

    private static WirelessApGroupView BuildGroup(IReadOnlyList<GroupEvidenceProjection> items,
        IReadOnlyDictionary<string, WirelessApObservationView> observations, WirelessScopeView scope)
    {
        var first = items[0];
        var all = items.Select(item => item.Evidence).DistinctBy(item => JsonSerializer.Serialize(item)).OrderBy(item => item.CapturedMs)
            .ThenBy(item => item.SourceRevision, StringComparer.Ordinal).ThenBy(item => item.DisplayName, StringComparer.Ordinal).ToArray();
        // An older revision is provenance, not a current membership union.
        var latest = all.GroupBy(item => item.SourceAcDeviceId).SelectMany(group =>
            group.Where(item => item.CapturedMs == group.Max(value => value.CapturedMs))).ToArray();
        var sourceIds = Sorted(latest.SelectMany(item => item.SourceObservationIds));
        var members = sourceIds.Select(id => observations[id]).ToArray();
        var names = latest.Select(item => item.DisplayName).Distinct(StringComparer.Ordinal).ToArray();
        var reason = items.Select(item => item.Reason).FirstOrDefault(item => item is not null);
        var status = items.Any(item => item.Status == "CONFLICT") ? "CONFLICT" : items.Any(item => item.Status == "CANDIDATE") ? "CANDIDATE" : first.Status;
        if (names.Length > 1) { status = "CONFLICT"; reason = "GROUP_DISPLAY_NAME_CONFLICT"; }
        var sourceAcIds = Sorted(all.Select(item => item.SourceAcDeviceId));
        var id = first.IdentityKey is null ? "ap-group-candidate:" + Fingerprint(all.Select(item => JsonSerializer.Serialize(item)))
            : "ap-group:" + Fingerprint(new[] { first.IdentityKey });
        return new(id, sourceAcIds[0], first.DomainId, first.Evidence.SourceGroupKey,
            latest.OrderByDescending(item => item.CapturedMs).ThenBy(item => item.DisplayName, StringComparer.Ordinal).First().DisplayName,
            first.GroupScope, Sorted(members.Select(item => item.ResourceId)), Fingerprint(all.Select(item => JsonSerializer.Serialize(item))), scope.Status,
            first.IdentityKey, status, Fingerprint(new[] { first.IdentityKey ?? "CANDIDATE" }
                .Concat(Sorted(members.Select(item => "member|" + item.SourceAcDeviceId + "|" + item.ResourceId)))
                .Concat(Sorted(latest.Select(item => "source|" + item.SourceAcDeviceId)))
                .Concat(Sorted(latest.SelectMany(item => item.DomainSharedEvidenceRefs is { Count: > 0 } && first.GroupScope == "DOMAIN"
                    ? item.DomainMemberDeviceIds!.Select(id => "domain-member|" + id) : Array.Empty<string>())))), sourceIds, sourceAcIds,
            Sorted(all.SelectMany(item => new[] { item.RawManifestRef }.Concat(item.DomainSharedEvidenceRefs ?? Array.Empty<string>()))),
            all.Max(item => item.CapturedMs), reason, all);
    }

    internal static IReadOnlyList<WirelessApResourceView> GroupMembers(WirelessProjectionSnapshot snapshot, WirelessApGroupView group)
    {
        var ids = group.SourceObservationIds.ToHashSet(StringComparer.Ordinal);
        var selected = snapshot.ApResources.SelectMany(item => item.Observations).Where(item => ids.Contains(item.Id)).ToArray();
        var seeds = selected.Select(item => (Seed: new WirelessObservationSeed(item.SourceAcDeviceId, item.SourceAcDeviceId,
            item.SourceSiteId, null, null, item.ObservedApName, item.SerialEvidence, item.NormalizedMacSet, item.ApModel,
            item.ApGroup, item.RawState, item.NormalizedState, item.StateConfidence, item.CapturedMs,
            item.RawManifestRef, item.ParserRevision, item.SourceRevision), DomainId: item.SourceAcDomainId)).ToArray();
        var originalIds = selected.GroupBy(item => BaseObservationId(item.Id), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().ResourceId, StringComparer.Ordinal);
        return BuildResources(seeds, snapshot.Scope, originalIds).Select(resource =>
        {
            var own = selected.Where(item => item.ResourceId == resource.Id).OrderBy(item => item.CapturedMs).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
            return resource with
            {
                Observations = own, SourceObservationIds = Sorted(own.Select(item => item.Id)),
                ProjectionRevision = Fingerprint(new[] { resource.Id, resource.SourceRevision }.Concat(own.Select(item => $"{item.Id}|{item.RawState}|{item.NormalizedState}"))),
                StateConflictReason = own.Any(item => item.RelationStatus == "CONFLICT") ? "SAME_REVISION_STATE_CONFLICT" : resource.StateConflictReason
            };
        }).ToArray();
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string[] Sorted(IEnumerable<string> values) => values.Where(item => !string.IsNullOrWhiteSpace(item))
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
}
