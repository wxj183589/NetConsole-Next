using System.Net;
using System.Text.Json;

namespace NetConsole.Next.Service;

internal sealed record PingLossRawReference(string RunId, string Command, string OutputSha256, string SegmentSha256);
internal sealed record PingLossFactEvidence(string Id, string Kind, string IdentityKey, string Status,
    string Source, string CapturedScopeKey, long CapturedMs, long CollectionEndedMs, long AgeMs,
    IReadOnlyDictionary<string, string> ObservedValues, IReadOnlyList<PingLossRawReference> RawReferences);
internal sealed record PingLossEvidenceSnapshot(string RunId, string ItemId, string DeviceId, string SiteId,
    int Sequence, long SampleCapturedMs, string SampleStatus, string DestinationAddress, string Protocol, int? Port,
    string State, string ConnectedApState, string ConnectedApReason,
    IReadOnlyList<PingLossFactEvidence> Facts, bool HasMore, IReadOnlyList<string> Limitations);

// This is observation context at probe completion, never a reconstruction of the outage or client association.
internal static class PingLossEvidence
{
    public static PingLossEvidenceSnapshot? Get(Catalog catalog, NetworkDiagnosticsService diagnostics,
        string runId, string itemId, int sequence, string actor, P2B5SiteScopeDecision scope)
    {
        var run = diagnostics.Get(runId, actor, scope);
        return run is null ? null : Project(catalog, run, itemId, sequence, scope, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    internal static PingLossEvidenceSnapshot? Project(Catalog catalog, NetworkDiagnosticRunSnapshot run,
        string itemId, int sequence, P2B5SiteScopeDecision scope, long nowMs)
    {
        if (!scope.IsAuthorized) throw new DeviceSiteScopeException("P2B_SITE_SCOPE_DENIED", scope.Detail);
        var items = run.Items.Where(item => item.Id == itemId).ToArray();
        if (items.Length != 1 || items[0].Kind != "LONG_PING" || items[0].QuickPing is not { } ping) return null;
        var item = items[0];
        if (!scope.IsGlobal && !scope.AllowedSiteReferences.Contains(item.SiteId)) return null;
        var samples = ping.Samples.Where(sample => sample.Sequence == sequence).ToArray();
        if (samples.Length != 1 || sequence < 1 || sequence > ping.RequestedSamples) return null;
        var sample = samples[0];
        if (sample.Status is not ("TIMED_OUT" or "UNREACHABLE") || sample.LatencyMs is not null ||
            sample.CapturedMs < run.CreatedMs || sample.CapturedMs > nowMs ||
            run.StartedMs is not { } started || sample.CapturedMs < started ||
            item.StartedMs is not { } itemStarted || sample.CapturedMs < itemStarted ||
            (run.EndedMs is { } ended && sample.CapturedMs > ended) ||
            (item.EndedMs is { } itemEnded && sample.CapturedMs > itemEnded)) return null;

        PingLossEvidenceSnapshot Result(string state, IReadOnlyList<PingLossFactEvidence>? facts = null, bool more = false) =>
            new(run.Id, item.Id, item.DeviceId, item.SiteId, sample.Sequence, sample.CapturedMs, sample.Status,
                ping.DestinationAddress, ping.Protocol, ping.Port, state, "UNKNOWN", "NO_AUTHORITATIVE_CLIENT_AP_ASSOCIATION",
                facts ?? [], more, ["SAMPLE_TIME_IS_PROBE_COMPLETION_NOT_OUTAGE_START",
                    "OBSERVATION_CONTEXT_ONLY_NOT_CAUSALITY_OR_STATE_AT_SAMPLE",
                    "PUBLICATION_TIME_UNRECORDED_HISTORICAL_AVAILABILITY_UNKNOWN",
                    "NO_CLIENT_AP_ASSOCIATION_OR_CURRENT_WIRELESS_PROJECTION"]);

        var target = catalog.GetDeviceInventoryDetail(item.DeviceId, scope);
        // Older diagnostic snapshots do not retain an admitted endpoint. Refuse rebinding after admission,
        // DNS re-resolution, cross-Site matching, and a changed TCP destination.
        if (target is null || target.Item.SiteId != item.SiteId || target.UpdatedMs > run.CreatedMs ||
            target.TerminalProfile is not { } profile || profile.CreatedMs > run.CreatedMs || profile.UpdatedMs > run.CreatedMs ||
            !IPAddress.TryParse(profile.Host, out var registeredAddress) ||
            !IPAddress.TryParse(ping.DestinationAddress, out var probedAddress) || !registeredAddress.Equals(probedAddress) ||
            ping.Protocol is not ("ICMP" or "TCP") || (ping.Protocol == "TCP" && ping.Port != profile.Port))
            return Result("TARGET_BINDING_UNPROVEN");

        var rows = catalog.QueryPingLossFacts(profile.Id, item.DeviceId, item.SiteId, sample.CapturedMs, scope, 21);
        return Result(rows.Count == 0 ? "NO_PRIOR_VERIFIED_EVIDENCE" : "HISTORICAL_CONTEXT", rows.Take(20).ToArray(), rows.Count > 20);
    }
}

internal sealed partial class Catalog
{
    internal IReadOnlyList<PingLossFactEvidence> QueryPingLossFacts(string profileId, string deviceId, string siteId,
        long cutoffMs, P2B5SiteScopeDecision scope, int limit)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        var parameters = new List<(string Name, object? Value)>();
        var scoped = H3cFactScopePredicate(scope, parameters, "h3c_facts", true);
        // Cutoff and exact immutable scope precede both per-identity ranking and the bounded result limit.
        // is_current is intentionally absent: a later revision cannot hide earlier evidence.
        command.CommandText = $@"WITH prior AS (
            SELECT h3c_facts.*,t.ended_ms collection_ended_ms,
                ROW_NUMBER() OVER(PARTITION BY kind,identity_key,authority_key,scope_key ORDER BY captured_ms DESC,h3c_facts.id) revision
            FROM h3c_facts JOIN task_runs t ON t.id=json_extract(payload_json,'$.rawProvenance.runId')
            WHERE profile_id=$profile AND captured_ms<=$cutoff AND t.ended_ms<=$cutoff AND t.ended_ms>=captured_ms
                AND json_extract(payload_json,'$.capturedScope.deviceId')=$device
                AND json_extract(payload_json,'$.capturedScope.siteId')=$site
                AND {scoped} AND {H3cFactRawVerificationPredicate("h3c_facts")}
                AND status NOT IN ('PARTIAL','FAILED','UNSUPPORTED','EMPTY'))
            SELECT id,kind,identity_key,status,source,scope_key,captured_ms,collection_ended_ms,payload_json
            FROM prior WHERE revision=1 ORDER BY captured_ms DESC,kind,identity_key,id LIMIT $limit";
        command.Parameters.AddWithValue("$profile", profileId);
        command.Parameters.AddWithValue("$device", deviceId);
        command.Parameters.AddWithValue("$site", siteId);
        command.Parameters.AddWithValue("$cutoff", cutoffMs);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 21));
        DeviceSiteScope.AddParameters(command, parameters);
        using var reader = command.ExecuteReader();
        var rows = new List<PingLossFactEvidence>();
        while (reader.Read())
        {
            using var document = JsonDocument.Parse(reader.GetString(8));
            var raw = document.RootElement.GetProperty("rawProvenance");
            var runId = raw.GetProperty("runId").GetString()!;
            PingLossRawReference Reference(JsonElement entry) => new(runId, entry.GetProperty("command").GetString()!,
                entry.GetProperty("outputSha256").GetString()!, entry.GetProperty("segmentSha256").GetString()!);
            var references = raw.TryGetProperty("commands", out var commands) && commands.ValueKind == JsonValueKind.Array
                ? commands.EnumerateArray().Select(Reference).ToArray() : [Reference(raw)];
            rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetInt64(6), reader.GetInt64(7),
                cutoffMs - reader.GetInt64(6), ObservationValues(document.RootElement), references));
        }
        return rows;
    }

    private static IReadOnlyDictionary<string, string> ObservationValues(JsonElement payload)
    {
        // No transcript or arbitrary payload echo: expose bounded, parsed scalar observations only.
        string[] allowed = ["interfaceName", "linkStatus", "protocolStatus", "speed", "duplex", "portMode", "vlan",
            "radioMode", "channel", "inputBytes", "outputBytes", "role", "model", "comwareVersion",
            "softwareVersion", "peerName", "peerMac", "localInterface", "neighborSystemName", "status", "state"];
        var values = new Dictionary<string, string>();
        foreach (var key in allowed)
            if (payload.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            {
                var text = value.ToString();
                values[key] = text.Length <= 160 ? text : text[..160] + "…";
            }
        return values;
    }
}
