using System.Text.Json;

namespace NetConsole.Next.Service;

internal sealed record H3cOperationalCapability(string Key, string State, string Reason,
    IReadOnlyList<string> EvidenceIds, long? CapturedMs, string Source, string Freshness);

internal sealed record H3cOperationalCapabilityView(string ProfileId, string? Model, string? Version,
    string? Release, long AsOf, IReadOnlyList<H3cOperationalCapability> Capabilities);

internal sealed record WirelessRoleEvidence(string AcDeviceId, string ObservationId, string RawState,
    string NormalizedState, long CapturedMs, string RawManifestRef, string SourceRevision);

internal sealed record WirelessHaCapability(string DomainId, string DomainKind, string TopologyEvidence,
    string DeviceRoleEvidence, string HaVerification, string Reason,
    IReadOnlyList<string> TopologyEvidenceRefs, IReadOnlyList<WirelessRoleEvidence> Observations);

internal sealed record WirelessFitCapability(string ResourceId, string DisplayName, string IdentityStatus,
    string AcManagedState, string DirectAccessState, IReadOnlyList<string> AcDeviceIds,
    IReadOnlyList<string> ApGroups, IReadOnlyList<WirelessRoleEvidence> Observations,
    string? IdentityConflict, string? StateConflict);

internal sealed record WirelessOperationalCapabilityView(long AsOf, string SourceRevision, WirelessScopeView Scope,
    IReadOnlyList<WirelessHaCapability> Domains, IReadOnlyList<WirelessFitCapability> FitAps);

/// <summary>Projects existing scoped evidence. No commands, credentials or device writes.</summary>
internal static class H3cOperationalCapabilities
{
    public const long FreshnessWindowMs = 300_000;

    public static H3cOperationalCapabilityView Project(string profileId,
        IReadOnlyList<H3cFactSnapshot> scopedFacts, long nowMs)
    {
        var facts = scopedFacts.Where(f => f.ProfileId == profileId && f.IsCurrent && f.CapturedMs <= nowMs)
            .OrderByDescending(f => f.CapturedMs).ThenBy(f => f.Id, StringComparer.Ordinal).ToArray();
        var identity = facts.FirstOrDefault(f => f.Kind == "device.identity" &&
            f.Status is not ("FAILED" or "PARTIAL" or "UNSUPPORTED" or "EMPTY"));
        string? Field(string key)
        {
            if (identity is null) return null;
            try
            {
                using var json = JsonDocument.Parse(identity.PayloadJson);
                return json.RootElement.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String
                    ? property.GetString() : null;
            }
            catch (JsonException) { return null; }
        }
        var specifications = new (string Key, string Kind)[]
        {
            ("DEVICE_IDENTITY", "device.identity"), ("INTERFACE_READ", "interface"),
            ("VLAN_READ", "vlan.table"), ("LLDP_READ", "lldp.neighbor"),
            ("AP_INVENTORY_READ", "command.display.wlan.ap.all"),
            ("AP_ADDRESS_READ", "command.display.wlan.ap.all.address"),
            ("AP_RADIO_READ", "command.display.wlan.ap.all.radio"),
            ("AP_DETAILS_READ", "command.display.wlan.ap.all.verbose"),
            ("CPU_READ", "command.display.cpu-usage"), ("MEMORY_READ", "command.display.memory")
        };
        var capabilities = specifications.Select(spec => Evaluate(spec.Key,
            facts.Where(f => f.Kind == spec.Kind).ToArray(), nowMs)).ToList();
        capabilities.Add(new("HA_SWITCH", "UNKNOWN", "CONTROLLED_SWITCH_AND_SERVICE_RECOVERY_NOT_VERIFIED",
            Array.Empty<string>(), null, "NONE", "UNKNOWN"));
        capabilities.Add(new("WHOLE_CONFIGURATION_RESTORE", "UNKNOWN", "RESTORE_READBACK_AND_SERVICE_VERIFICATION_REQUIRED",
            Array.Empty<string>(), null, "NONE", "UNKNOWN"));
        return new(profileId, Field("model"), Field("version"), Field("release"), nowMs, capabilities);
    }

    private static H3cOperationalCapability Evaluate(string key, H3cFactSnapshot[] candidates, long nowMs)
    {
        if (candidates.Length == 0)
            return new(key, "UNKNOWN", "NO_CURRENT_SCOPED_EVIDENCE", Array.Empty<string>(), null, "NONE", "UNKNOWN");
        // A command revision is indivisible. Never use older success to hide a new failure.
        var captured = candidates.Max(f => f.CapturedMs);
        var latest = candidates.Where(f => f.CapturedMs == captured).ToArray();
        var invalid = latest.FirstOrDefault(f => f.Status is "FAILED" or "PARTIAL" or "UNSUPPORTED" or "EMPTY");
        var stale = nowMs - captured > FreshnessWindowMs;
        var raw = latest.All(f => f.CollectedRawVerified);
        var state = invalid?.Status ?? (stale ? "STALE" : raw ? "VERIFIED" : "FIXTURE");
        var reason = invalid is not null ? "LATEST_COMMAND_" + invalid.Status : stale ? "EVIDENCE_EXPIRED" :
            raw ? "COLLECTED_RAW_VERIFIED" : "TRANSPORT_NOT_VERIFIED";
        return new(key, state, reason, latest.Select(f => f.Id).ToArray(), captured,
            string.Join(",", latest.Select(f => f.Source).Distinct(StringComparer.Ordinal)), stale ? "STALE" : "RECENT");
    }

    public static WirelessOperationalCapabilityView ProjectWireless(WirelessProjectionSnapshot snapshot)
    {
        static WirelessRoleEvidence Evidence(WirelessApObservationView observation) => new(observation.SourceAcDeviceId,
            observation.Id, observation.RawState, observation.NormalizedState, observation.CapturedMs,
            observation.RawManifestRef, observation.SourceRevision);
        var observations = snapshot.ApResources.SelectMany(ap => ap.Observations).ToArray();
        var domains = snapshot.AcDomains.Select(domain =>
        {
            var evidence = observations.Where(o => o.SourceAcDomainId == domain.Id &&
                domain.MemberDeviceIds.Contains(o.SourceAcDeviceId, StringComparer.Ordinal)).Select(Evidence).ToArray();
            return new WirelessHaCapability(domain.Id, domain.DomainKind, domain.TopologyStatus,
                evidence.Length > 0 ? "DEVICE_REPORTED_ROLE" : "UNKNOWN", "NOT_TESTED",
                "AP_PRIMARY_BACKUP_OBSERVATIONS_DO_NOT_VERIFY_CONTROLLER_FAILOVER",
                domain.SourceEvidenceRefs, evidence);
        }).ToArray();
        var aps = snapshot.ApResources.Select(ap => new WirelessFitCapability(ap.Id, ap.DisplayName, ap.IdentityStatus,
            ap.Observations.Count > 0 ? "AC_OBSERVATION_AVAILABLE" : "UNKNOWN", "UNKNOWN",
            ap.Observations.Select(o => o.SourceAcDeviceId).Distinct(StringComparer.Ordinal).ToArray(),
            ap.Observations.Select(o => o.ApGroup).Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g!)
                .Distinct(StringComparer.Ordinal).ToArray(), ap.Observations.Select(Evidence).ToArray(),
            ap.ConflictReason, ap.StateConflictReason)).ToArray();
        return new(snapshot.AsOf, snapshot.SourceRevision, snapshot.Scope, domains, aps);
    }
}
