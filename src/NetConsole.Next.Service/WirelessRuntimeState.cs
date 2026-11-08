using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace NetConsole.Next.Service;

internal sealed record WirelessRuntimePolicy(long FreshnessWindowMs = 300_000, int MaximumFactRows = 4096)
{
    public static WirelessRuntimePolicy FromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_WIRELESS_FRESHNESS_SECONDS");
        if (string.IsNullOrWhiteSpace(value)) return new();
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds is < 1 or > 86_400)
            throw new InvalidDataException("Wireless freshness policy must be between 1 and 86400 seconds.");
        return new(seconds * 1000L);
    }
}

internal sealed record WirelessRuntimeSample(string SourceAcDeviceId, string SourceSiteId,
    string? SerialEvidence, IReadOnlyList<string> NormalizedMacSet, string RawState,
    string Outcome, string VerificationLevel, bool StateProfileVerified, long CapturedMs,
    string SourceRevision, string RawManifestRef, string ParserRevision);

internal sealed record WirelessEffectiveState(string NormalizedState, string RawState, long CapturedMs,
    string SourceRevision, string RawManifestRef, string ParserRevision, string VerificationLevel,
    string FreshnessState = "HISTORICAL_EVIDENCE");

internal sealed record WirelessEffectiveChange(string Id, string PreviousState, string NormalizedState,
    long CapturedMs, string SourceRevision, string RawManifestRef);

internal sealed record WirelessSourceRuntimeState(string SourceAcDeviceId, string SourceSiteId,
    string NormalizedState, WirelessEffectiveState? Current, WirelessEffectiveState? PreviousEffective,
    IReadOnlyList<WirelessEffectiveChange> Recent10, string LatestOutcome, long? LatestCapturedMs,
    long? FreshnessDeadlineMs, string HistoryStatus);

internal sealed record WirelessAggregateSourceEvidence(string SourceAcDeviceId, string SourceSiteId,
    string NormalizedState, long CapturedMs, string SourceRevision, string RawManifestRef);
internal sealed record WirelessAggregateEffectiveState(string NormalizedState, long CapturedMs,
    IReadOnlyList<WirelessAggregateSourceEvidence> Sources, string FreshnessState = "HISTORICAL_EVIDENCE");
internal sealed record WirelessAggregateChange(string Id, string PreviousState, string NormalizedState,
    long CapturedMs, string SourceRevision, IReadOnlyList<WirelessAggregateSourceEvidence> Sources);

internal sealed record WirelessRuntimeStateView(string ResourceId, string NormalizedState,
    IReadOnlyList<WirelessSourceRuntimeState> Sources, WirelessScopeView Scope, long AsOf,
    string SourceRevision, long FreshnessWindowMs, IReadOnlyList<string> Warnings,
    WirelessAggregateEffectiveState? Current = null, WirelessAggregateEffectiveState? PreviousEffective = null,
    IReadOnlyList<WirelessAggregateChange>? Recent10 = null);

internal sealed record WirelessRuntimeHistory(IReadOnlyList<WirelessRuntimeSample> Samples,
    bool ScanTruncated, IReadOnlySet<string> IncompleteSourceIds);

internal sealed class WirelessRuntimeStateProjection
{
    public WirelessRuntimeStateView Project(WirelessApResourceView resource, WirelessRuntimeHistory history,
        WirelessScopeView scope, long nowMs, WirelessRuntimePolicy policy)
    {
        if (policy.FreshnessWindowMs is < 1000 or > 86_400_000 || policy.MaximumFactRows is < 1 or > 65_536)
            throw new ArgumentOutOfRangeException(nameof(policy));
        if (resource.IdentityStatus != "CONFIRMED")
            return new(resource.Id, resource.IdentityStatus == "CONFLICT" ? "CONFLICT" : "UNKNOWN",
                Array.Empty<WirelessSourceRuntimeState>(), scope, nowMs, resource.ProjectionRevision,
                policy.FreshnessWindowMs, new[] { "AP_IDENTITY_NOT_CONFIRMED" });

        var allowedSources = resource.Observations.Select(item => (item.SourceAcDeviceId, item.SourceSiteId)).Distinct().ToArray();
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        var sources = new List<WirelessSourceRuntimeState>();
        var aggregateEvidence = new List<WirelessAggregateSourceEvidence>();
        foreach (var (acId, siteId) in allowedSources.OrderBy(item => item.SourceAcDeviceId, StringComparer.Ordinal))
        {
            var samples = history.Samples.Where(item => item.SourceAcDeviceId == acId && item.SourceSiteId == siteId &&
                    (item.Outcome != "SUCCESS" || MatchesIdentity(resource, item.SerialEvidence, item.NormalizedMacSet)))
                .Distinct().OrderBy(item => item.CapturedMs).ThenBy(item => item.SourceRevision, StringComparer.Ordinal).ToArray();
            if (samples.Any(item => item.VerificationLevel == "LIVE_VERIFIED" && item.Outcome == "SUCCESS" &&
                    item.CapturedMs > 0 && item.CapturedMs <= nowMs && NormalizeState(item.RawState, item.StateProfileVerified) != "UNKNOWN"))
            {
                if (samples.Any(item => item.VerificationLevel == "OFFLINE_FIXTURE")) warnings.Add("OFFLINE_FIXTURE_EXCLUDED_FROM_LIVE_CURRENT");
                samples = samples.Where(item => item.VerificationLevel != "OFFLINE_FIXTURE").ToArray();
            }
            WirelessEffectiveState? current = null;
            WirelessEffectiveState? previous = null;
            var changes = new List<WirelessEffectiveChange>();
            var latestOutcome = "NOT_COLLECTED";
            long? latestCaptured = null;
            foreach (var sameTime in samples.GroupBy(item => item.CapturedMs))
            {
                latestCaptured = sameTime.Key;
                if (sameTime.Key <= 0 || sameTime.Key > nowMs)
                {
                    latestOutcome = "CLOCK_SKEW";
                    continue;
                }
                var effective = sameTime.Where(item => item.Outcome == "SUCCESS" &&
                        item.VerificationLevel is "LIVE_VERIFIED" or "OFFLINE_FIXTURE")
                    .Select(item => (Sample: item, State: NormalizeState(item.RawState, item.StateProfileVerified)))
                    .Where(item => item.State != "UNKNOWN").ToArray();
                if (effective.Select(item => item.State).Distinct(StringComparer.Ordinal).Count() > 1)
                {
                    latestOutcome = "CONFLICT";
                    continue;
                }
                // A failed/ambiguous revision at this same instant cannot be silently
                // overridden by a successful-looking sibling revision.
                if (sameTime.Any(item => item.Outcome != "SUCCESS"))
                {
                    latestOutcome = sameTime.Select(item => item.Outcome).Where(item => item != "SUCCESS").Order(StringComparer.Ordinal).First();
                    continue;
                }
                if (effective.Length == 0) { latestOutcome = "UNKNOWN"; continue; }
                var chosen = effective.OrderBy(item => item.Sample.SourceRevision, StringComparer.Ordinal)
                    .ThenBy(item => item.Sample.RawState, StringComparer.Ordinal)
                    .ThenBy(item => item.Sample.RawManifestRef, StringComparer.Ordinal)
                    .ThenBy(item => item.Sample.ParserRevision, StringComparer.Ordinal).First();
                var next = new WirelessEffectiveState(chosen.State, chosen.Sample.RawState, chosen.Sample.CapturedMs,
                    chosen.Sample.SourceRevision, chosen.Sample.RawManifestRef, chosen.Sample.ParserRevision,
                    chosen.Sample.VerificationLevel);
                if (current is not null && current.NormalizedState != next.NormalizedState)
                {
                    previous = current;
                    changes.Add(new("state-change:" + WirelessProjectionEngine.Fingerprint(new[]
                        { resource.Id, acId, next.SourceRevision, next.CapturedMs.ToString(CultureInfo.InvariantCulture), current.NormalizedState, next.NormalizedState }),
                        current.NormalizedState, next.NormalizedState, next.CapturedMs, next.SourceRevision, next.RawManifestRef));
                    if (changes.Count > 10) changes.RemoveAt(0);
                }
                current = next; // Unchanged values refresh Current evidence, never append a change.
                aggregateEvidence.Add(new(acId, siteId, next.NormalizedState, next.CapturedMs, next.SourceRevision, next.RawManifestRef));
                latestOutcome = "SUCCESS";
            }
            var deadline = current is null ? (long?)null : current.CapturedMs + policy.FreshnessWindowMs;
            var state = latestOutcome == "CONFLICT" ? "CONFLICT" : current is null ? "UNKNOWN" :
                latestOutcome == "CLOCK_SKEW" ? "UNKNOWN" : current.VerificationLevel == "OFFLINE_FIXTURE" ||
                deadline <= nowMs || latestOutcome != "SUCCESS" ? "STALE" : current.NormalizedState;
            if (current is not null) current = current with { FreshnessState = state is "STALE" or "UNKNOWN" or "CONFLICT" ? state : "FRESH" };
            if (samples.Any(item => item.VerificationLevel == "OFFLINE_FIXTURE")) warnings.Add("OFFLINE_FIXTURE_EVIDENCE");
            var historyStatus = history.IncompleteSourceIds.Contains(acId) ? "PARTIAL_EVIDENCE" :
                history.ScanTruncated && changes.Count < 10 ? "PARTIAL_SCAN_LIMIT" : "RECENT10_COMPLETE";
            if (historyStatus != "RECENT10_COMPLETE") warnings.Add("RUNTIME_HISTORY_INCOMPLETE");
            sources.Add(new(acId, siteId, state, current, previous, changes.AsEnumerable().Reverse().ToArray(),
                latestOutcome, latestCaptured, deadline, historyStatus));
        }
        var masters = sources.Count(item => item.NormalizedState == "RUNNING_MASTER");
        var aggregate = masters > 1 || sources.Any(item => item.NormalizedState == "CONFLICT") ? "CONFLICT" :
            masters == 1 ? "RUNNING_MASTER" : sources.Any(item => item.NormalizedState == "RUNNING_BACKUP") ? "RUNNING_BACKUP" :
            sources.Count > 0 && sources.All(item => item.NormalizedState == "OFFLINE") ? "OFFLINE" :
            sources.Any(item => item.NormalizedState == "STALE") ? "STALE" : "UNKNOWN";
        if (masters > 1) warnings.Add("MULTI_SOURCE_MASTER");
        if (history.ScanTruncated) warnings.Add("RUNTIME_HISTORY_SCAN_LIMIT_REACHED");
        var aggregateHistory = AggregateHistory(resource.Id, aggregateEvidence, allowedSources.Length, policy.FreshnessWindowMs);
        var aggregateCurrent = aggregateHistory.Current;
        if (aggregateCurrent is not null) aggregateCurrent = aggregateCurrent with
            { FreshnessState = aggregate == aggregateCurrent.NormalizedState ? "FRESH" :
                aggregate is "UNKNOWN" or "CONFLICT" ? aggregate : "STALE" };
        var revision = WirelessProjectionEngine.Fingerprint(sources.Select(item => JsonSerializer.Serialize(item)));
        return new(resource.Id, aggregate, sources, scope, nowMs, revision, policy.FreshnessWindowMs,
            warnings.Order(StringComparer.Ordinal).ToArray(), aggregateCurrent, aggregateHistory.Previous, aggregateHistory.Changes);
    }

    private static (WirelessAggregateEffectiveState? Current, WirelessAggregateEffectiveState? Previous, IReadOnlyList<WirelessAggregateChange> Changes)
        AggregateHistory(string resourceId, IReadOnlyList<WirelessAggregateSourceEvidence> evidence, int expectedSources, long windowMs)
    {
        var latest = new Dictionary<string, WirelessAggregateSourceEvidence>(StringComparer.Ordinal);
        var updated = new HashSet<string>(StringComparer.Ordinal);
        WirelessAggregateEffectiveState? current = null;
        WirelessAggregateEffectiveState? previous = null;
        var changes = new List<WirelessAggregateChange>();
        foreach (var batch in evidence.OrderBy(item => item.CapturedMs).GroupBy(item => item.CapturedMs))
        {
            foreach (var item in batch)
            {
                latest[item.SourceAcDeviceId] = item;
                updated.Add(item.SourceAcDeviceId);
            }
            var members = latest.Values.Where(item => item.CapturedMs + windowMs > batch.Key)
                .OrderBy(item => item.SourceAcDeviceId, StringComparer.Ordinal).ToArray();
            // Without a collection batch identifier, require new, fresh evidence
            // from every known source before advancing the aggregate history.
            if (members.Length != expectedSources || updated.Count != expectedSources) continue;
            updated.Clear();
            var masters = members.Count(item => item.NormalizedState == "RUNNING_MASTER");
            if (masters > 1) continue; // Preserve the prior effective AP state; conflict remains independently visible.
            var value = masters == 1 ? "RUNNING_MASTER" : members.Any(item => item.NormalizedState == "RUNNING_BACKUP") ? "RUNNING_BACKUP" :
                members.Length == expectedSources && members.All(item => item.NormalizedState == "OFFLINE") ? "OFFLINE" : "UNKNOWN";
            if (value == "UNKNOWN") continue;
            var next = new WirelessAggregateEffectiveState(value, batch.Key, members);
            if (current is not null && current.NormalizedState != next.NormalizedState)
            {
                previous = current;
                var revision = WirelessProjectionEngine.Fingerprint(members.Select(item => JsonSerializer.Serialize(item)));
                changes.Add(new("ap-state-change:" + WirelessProjectionEngine.Fingerprint(new[] { resourceId, revision, current.NormalizedState, value }),
                    current.NormalizedState, value, batch.Key, revision, members));
                if (changes.Count > 10) changes.RemoveAt(0);
            }
            current = next;
        }
        return (current, previous, changes.AsEnumerable().Reverse().ToArray());
    }

    internal static string NormalizeState(string rawState, bool profileVerified) => !profileVerified ? "UNKNOWN" :
        rawState.Trim().ToUpperInvariant() switch
        {
            "R/M" or "RM" => "RUNNING_MASTER", "R/B" or "RB" => "RUNNING_BACKUP", "I" => "OFFLINE", _ => "UNKNOWN"
        };

    internal static bool MatchesIdentity(WirelessApResourceView resource, string? serial, IEnumerable<string> macs) =>
        WirelessProjectionEngine.NormalizeSerial(serial) == resource.SerialEvidence &&
        WirelessProjectionEngine.NormalizeMacSet(macs).SequenceEqual(resource.NormalizedMacSet, StringComparer.Ordinal);
}

internal sealed partial class WirelessReadModelService
{
    public WirelessRuntimeStateView? GetRuntimeState(string resourceId, P2B5SiteScopeDecision scope,
        CancellationToken cancellationToken = default, long? nowMs = null, WirelessRuntimePolicy? policy = null,
        string? sourceAcId = null, string? domainId = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fixture = LoadFixture();
        var snapshot = fixture is null ? GetSnapshot(scope) : ProjectFixture(fixture, scope);
        var resource = snapshot.ApResources.FirstOrDefault(item => item.Id == resourceId);
        if (resource is null) return null;
        resource = resource with { Observations = resource.Observations.Where(item =>
            (sourceAcId is null || item.SourceAcDeviceId == sourceAcId) &&
            (domainId is null || item.SourceAcDomainId == domainId)).ToArray() };
        if (resource.Observations.Count == 0) return null;
        policy ??= WirelessRuntimePolicy.FromEnvironment();
        if (resource.IdentityStatus != "CONFIRMED")
            return new WirelessRuntimeStateProjection().Project(resource,
                new(Array.Empty<WirelessRuntimeSample>(), false, new HashSet<string>()), snapshot.Scope,
                nowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), policy);
        WirelessRuntimeHistory history;
        if (fixture is not null)
        {
            var fixtureSamples = fixture.RuntimeSamples ?? resource.Observations.Select(item => new WirelessRuntimeSample(item.SourceAcDeviceId,
                item.SourceSiteId, item.SerialEvidence, item.NormalizedMacSet, item.RawState, "SUCCESS", "OFFLINE_FIXTURE",
                item.NormalizedState is "RUNNING_MASTER" or "RUNNING_BACKUP" or "OFFLINE", item.CapturedMs,
                item.SourceRevision, item.RawManifestRef, item.ParserRevision)).ToArray();
            var allowed = resource.Observations.Select(item => (item.SourceAcDeviceId, item.SourceSiteId)).ToHashSet();
            var visible = fixtureSamples.Where(item => allowed.Contains((item.SourceAcDeviceId, item.SourceSiteId)))
                .OrderByDescending(item => item.CapturedMs).Take(policy.MaximumFactRows + 1).ToArray();
            history = new(visible.Take(policy.MaximumFactRows).ToArray(), visible.Length > policy.MaximumFactRows, new HashSet<string>());
        }
        else history = _catalog.QueryWirelessRuntimeHistory(resource, scope, policy.MaximumFactRows, cancellationToken);
        return new WirelessRuntimeStateProjection().Project(resource, history, snapshot.Scope,
            nowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), policy);
    }
}

internal sealed partial class Catalog
{
    public WirelessRuntimeHistory QueryWirelessRuntimeHistory(WirelessApResourceView resource,
        P2B5SiteScopeDecision scope, int maximumRows, CancellationToken cancellationToken = default)
    {
        maximumRows = Math.Clamp(maximumRows, 1, 65_536);
        var samples = new List<WirelessRuntimeSample>();
        var incomplete = new HashSet<string>(StringComparer.Ordinal);
        var truncated = false;
        var remaining = maximumRows;
        var sourceList = resource.Observations.Select(item => (item.SourceAcDeviceId, item.SourceSiteId)).Distinct()
            .OrderBy(item => item.SourceAcDeviceId, StringComparer.Ordinal).ToArray();
        for (var sourceIndex = 0; sourceIndex < sourceList.Length; sourceIndex++)
        {
            var source = sourceList[sourceIndex];
            cancellationToken.ThrowIfCancellationRequested();
            if (!DeviceSiteScope.Allows(scope, source.SourceSiteId)) continue;
            var device = GetDeviceInventoryDetail(source.SourceAcDeviceId, scope);
            if (device?.TerminalProfileId is not { } profile || device.Item.SiteId != source.SourceSiteId) continue;
            if (remaining == 0) { truncated = true; incomplete.Add(source.SourceAcDeviceId); continue; }
            var quota = Math.Max(1, remaining / (sourceList.Length - sourceIndex));
            using var db = Open();
            using var command = db.CreateCommand();
            var proofParameters = new List<(string Name, object? Value)>();
            var apProof = H3cFactScopePredicate(scope, proofParameters, "ap", true);
            var addrProof = H3cFactScopePredicate(scope, proofParameters, "addr", true);
            var versionProof = H3cFactScopePredicate(scope, proofParameters, "identity", true);
            command.CommandText = $@"SELECT ap.id,ap.status,ap.source,ap.captured_ms,ap.payload_json,addr.payload_json,
                EXISTS(SELECT 1 FROM h3c_facts identity WHERE identity.profile_id=ap.profile_id AND identity.kind='device.identity'
                    AND identity.status NOT IN ('FAILED','UNSUPPORTED','EMPTY','PARTIAL') AND identity.captured_ms<=ap.captured_ms
                    AND identity.source=ap.source AND identity.scope_key=ap.scope_key AND {versionProof}),
                ({H3cFactRawVerificationPredicate("ap")} AND {H3cFactRawVerificationPredicate("addr")}),
                {H3cFactRawVerificationPredicate("ap")}
                FROM h3c_facts ap LEFT JOIN h3c_facts addr ON addr.profile_id=ap.profile_id
                    AND addr.kind='command.display.wlan.ap.all.address' AND addr.source=ap.source AND addr.captured_ms=ap.captured_ms
                    AND addr.scope_key=ap.scope_key
                    AND addr.status NOT IN ('FAILED','UNSUPPORTED','EMPTY','PARTIAL') AND {addrProof}
                WHERE ap.profile_id=$profile AND ap.kind='command.display.wlan.ap.all' AND {apProof}
                ORDER BY ap.captured_ms DESC,ap.id DESC LIMIT $limit";
            command.Parameters.AddWithValue("$profile", profile);
            command.Parameters.AddWithValue("$limit", quota + 1);
            DeviceSiteScope.AddParameters(command, proofParameters);
            using var reader = command.ExecuteReader();
            var read = 0;
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (read++ == quota) { truncated = true; break; }
                var revision = reader.GetString(0);
                var status = reader.GetString(1);
                var verification = reader.GetString(2) == H3cOfflineFixtureSource ? "OFFLINE_FIXTURE" :
                    status == "LIVE_VERIFIED" || reader.GetInt64(7) != 0 ? "LIVE_VERIFIED" : "UNVERIFIED";
                var captured = reader.GetInt64(3);
                var rows = status is "FAILED" or "UNSUPPORTED" or "EMPTY" or "PARTIAL" ?
                    Array.Empty<H3cApFact>() : H3cComwareParser.ParseWlanApAll(ReadOutput(reader.GetString(4)));
                if (rows.Count == 0)
                {
                    samples.Add(new(source.SourceAcDeviceId, source.SourceSiteId, null, Array.Empty<string>(), "",
                        status is "FAILED" or "UNSUPPORTED" or "EMPTY" or "PARTIAL" ? status : "PARSE_FAILED",
                        verification, false, captured, revision, "fact:" + revision, "h3c.ap.v1"));
                    continue;
                }
                var addresses = ParseAddressFacts(reader.IsDBNull(5) ? null : reader.GetString(5));
                if (reader.IsDBNull(5) && reader.GetInt64(8) != 0)
                {
                    // This is a source-wide collection outcome, not AP identity evidence.
                    incomplete.Add(source.SourceAcDeviceId);
                    samples.Add(new(source.SourceAcDeviceId, source.SourceSiteId, null, Array.Empty<string>(), "",
                        "INCOMPLETE", verification, false, captured, revision, "fact:" + revision, "h3c.ap.v1"));
                }
                foreach (var row in rows)
                {
                    addresses.TryGetValue(row.Name, out var addressSet);
                    var macs = addressSet ?? Array.Empty<string>();
                    if (!WirelessRuntimeStateProjection.MatchesIdentity(resource, row.Serial, macs))
                    {
                        if (WirelessProjectionEngine.NormalizeSerial(row.Serial) == resource.SerialEvidence && WirelessProjectionEngine.NormalizeMacSet(macs).Count == 0)
                            incomplete.Add(source.SourceAcDeviceId);
                        continue; // Names, IPs and partial evidence never attach history to a physical AP.
                    }
                    samples.Add(new(source.SourceAcDeviceId, source.SourceSiteId, row.Serial, macs, row.State ?? "",
                        "SUCCESS", verification, reader.GetInt64(6) != 0, captured, revision, "fact:" + revision, "h3c.ap.v1"));
                }
            }
            remaining -= Math.Min(read, quota);
        }
        return new(samples, truncated, incomplete);
    }
}
