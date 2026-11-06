using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace NetConsole.Next.Service;

internal sealed record WirelessProjectionPersistenceResult(int Domains, int Resources, int Observations, int Groups);

internal sealed partial class Catalog
{
    public WirelessProjectionPersistenceResult PersistWirelessProjection(WirelessProjectionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!string.Equals(snapshot.Scope.Status, "FULL", StringComparison.Ordinal))
            throw new InvalidOperationException("Only a full-scope wireless projection can update the durable identity projection.");
        using var db = Open();
        using var transaction = db.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sourceCutoffs = snapshot.ApResources.SelectMany(item => item.Observations)
            .Select(item => (Source: item.SourceAcDeviceId, Captured: item.CapturedMs))
            .Concat(snapshot.ApGroups.SelectMany(item => item.SourceGroupObservations)
                .Select(item => (Source: item.SourceAcDeviceId, Captured: item.CapturedMs)))
            .Where(item => !string.IsNullOrWhiteSpace(item.Source) && item.Captured > 0)
            .GroupBy(item => item.Source, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Max(item => item.Captured), StringComparer.Ordinal);
        var cachedCutoffs = ReadGroupCacheSourceCutoffs(db, transaction, sourceCutoffs.Keys);
        var staleSources = sourceCutoffs.Where(item => cachedCutoffs.TryGetValue(item.Key, out var cached) && item.Value < cached)
            .Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var domain in snapshot.AcDomains)
        {
            using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO wireless_ac_domains
                    (id,domain_kind,topology_status,member_device_ids_json,source_evidence_refs_json,confirmed,revision,as_of_ms,updated_ms)
                VALUES($id,$kind,$status,$members,$evidence,$confirmed,$revision,$asOf,$updated)
                ON CONFLICT(id) DO UPDATE SET domain_kind=excluded.domain_kind,topology_status=excluded.topology_status,
                    member_device_ids_json=excluded.member_device_ids_json,source_evidence_refs_json=excluded.source_evidence_refs_json,
                    confirmed=excluded.confirmed,revision=excluded.revision,as_of_ms=excluded.as_of_ms,updated_ms=excluded.updated_ms
                """;
            command.Parameters.AddWithValue("$id", domain.Id);
            command.Parameters.AddWithValue("$kind", domain.DomainKind);
            command.Parameters.AddWithValue("$status", domain.TopologyStatus);
            command.Parameters.AddWithValue("$members", JsonSerializer.Serialize(domain.MemberDeviceIds));
            command.Parameters.AddWithValue("$evidence", JsonSerializer.Serialize(domain.SourceEvidenceRefs));
            command.Parameters.AddWithValue("$confirmed", string.Equals(domain.TopologyStatus, "CONFIRMED", StringComparison.Ordinal) ? 1 : 0);
            command.Parameters.AddWithValue("$revision", domain.Revision);
            command.Parameters.AddWithValue("$asOf", domain.AsOf);
            command.Parameters.AddWithValue("$updated", now);
            command.ExecuteNonQuery();
        }
        foreach (var resource in snapshot.ApResources)
        {
            using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO wireless_ap_resources
                    (id,display_name,identity_status,serial_evidence,normalized_mac_set_json,source_observation_ids_json,
                     source_ac_domain_ids_json,scope_status,first_seen_ms,last_seen_ms,source_revision,projection_revision,
                     conflict_reason,state_conflict_reason,updated_ms)
                VALUES($id,$display,$identity,$serial,$macs,$observations,$domains,$scope,$first,$last,$sourceRevision,$projectionRevision,$conflict,$stateConflict,$updated)
                ON CONFLICT(id) DO UPDATE SET display_name=excluded.display_name,identity_status=excluded.identity_status,
                    serial_evidence=excluded.serial_evidence,normalized_mac_set_json=excluded.normalized_mac_set_json,
                    source_observation_ids_json=excluded.source_observation_ids_json,source_ac_domain_ids_json=excluded.source_ac_domain_ids_json,
                    scope_status=excluded.scope_status,first_seen_ms=excluded.first_seen_ms,last_seen_ms=excluded.last_seen_ms,
                    source_revision=excluded.source_revision,projection_revision=excluded.projection_revision,
                    conflict_reason=excluded.conflict_reason,state_conflict_reason=excluded.state_conflict_reason,updated_ms=excluded.updated_ms
                """;
            command.Parameters.AddWithValue("$id", resource.Id);
            command.Parameters.AddWithValue("$display", resource.DisplayName);
            command.Parameters.AddWithValue("$identity", resource.IdentityStatus);
            command.Parameters.AddWithValue("$serial", (object?)resource.SerialEvidence ?? DBNull.Value);
            command.Parameters.AddWithValue("$macs", JsonSerializer.Serialize(resource.NormalizedMacSet));
            command.Parameters.AddWithValue("$observations", JsonSerializer.Serialize(resource.SourceObservationIds));
            command.Parameters.AddWithValue("$domains", JsonSerializer.Serialize(resource.SourceAcDomainIds));
            command.Parameters.AddWithValue("$scope", resource.ScopeStatus);
            command.Parameters.AddWithValue("$first", resource.FirstSeenMs);
            command.Parameters.AddWithValue("$last", resource.LastSeenMs);
            command.Parameters.AddWithValue("$sourceRevision", resource.SourceRevision);
            command.Parameters.AddWithValue("$projectionRevision", resource.ProjectionRevision);
            command.Parameters.AddWithValue("$conflict", (object?)resource.ConflictReason ?? DBNull.Value);
            command.Parameters.AddWithValue("$stateConflict", (object?)resource.StateConflictReason ?? DBNull.Value);
            command.Parameters.AddWithValue("$updated", now);
            command.ExecuteNonQuery();

            foreach (var observation in resource.Observations)
            {
                using var observationCommand = db.CreateCommand();
                observationCommand.Transaction = transaction;
                observationCommand.CommandText = """
                    INSERT INTO wireless_ap_observations
                        (id,resource_id,source_ac_device_id,source_ac_domain_id,source_site_id,observed_ap_name,serial_evidence,
                         normalized_mac_set_json,ap_model,ap_group,raw_state,normalized_state,state_confidence,captured_ms,received_ms,
                         raw_manifest_ref,parser_revision,source_revision,relation_status)
                    VALUES($id,$resource,$sourceAc,$domain,$site,$name,$serial,$macs,$model,$group,$raw,$normalized,$confidence,$captured,$received,$manifest,$parser,$revision,$relation)
                    ON CONFLICT(id) DO UPDATE SET resource_id=excluded.resource_id,source_ac_device_id=excluded.source_ac_device_id,
                        source_ac_domain_id=excluded.source_ac_domain_id,source_site_id=excluded.source_site_id,observed_ap_name=excluded.observed_ap_name,
                        serial_evidence=excluded.serial_evidence,normalized_mac_set_json=excluded.normalized_mac_set_json,ap_model=excluded.ap_model,
                        ap_group=excluded.ap_group,raw_state=excluded.raw_state,normalized_state=excluded.normalized_state,state_confidence=excluded.state_confidence,
                        captured_ms=excluded.captured_ms,received_ms=excluded.received_ms,raw_manifest_ref=excluded.raw_manifest_ref,
                        parser_revision=excluded.parser_revision,source_revision=excluded.source_revision,relation_status=excluded.relation_status
                    """;
                observationCommand.Parameters.AddWithValue("$id", observation.Id);
                observationCommand.Parameters.AddWithValue("$resource", observation.ResourceId);
                observationCommand.Parameters.AddWithValue("$sourceAc", observation.SourceAcDeviceId);
                observationCommand.Parameters.AddWithValue("$domain", observation.SourceAcDomainId);
                observationCommand.Parameters.AddWithValue("$site", observation.SourceSiteId);
                observationCommand.Parameters.AddWithValue("$name", observation.ObservedApName);
                observationCommand.Parameters.AddWithValue("$serial", (object?)observation.SerialEvidence ?? DBNull.Value);
                observationCommand.Parameters.AddWithValue("$macs", JsonSerializer.Serialize(observation.NormalizedMacSet));
                observationCommand.Parameters.AddWithValue("$model", (object?)observation.ApModel ?? DBNull.Value);
                observationCommand.Parameters.AddWithValue("$group", (object?)observation.ApGroup ?? DBNull.Value);
                observationCommand.Parameters.AddWithValue("$raw", observation.RawState);
                observationCommand.Parameters.AddWithValue("$normalized", observation.NormalizedState);
                observationCommand.Parameters.AddWithValue("$confidence", observation.StateConfidence);
                observationCommand.Parameters.AddWithValue("$captured", observation.CapturedMs);
                observationCommand.Parameters.AddWithValue("$received", observation.ReceivedMs);
                observationCommand.Parameters.AddWithValue("$manifest", observation.RawManifestRef);
                observationCommand.Parameters.AddWithValue("$parser", observation.ParserRevision);
                observationCommand.Parameters.AddWithValue("$revision", observation.SourceRevision);
                observationCommand.Parameters.AddWithValue("$relation", observation.RelationStatus);
                observationCommand.ExecuteNonQuery();
            }
        }
        foreach (var group in snapshot.ApGroups)
        {
            // A delayed snapshot must not recreate evicted candidates or
            // regress the only captured watermark for a confirmed empty group.
            if (group.SourceAcDeviceIds.Any(staleSources.Contains)) continue;
            using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO wireless_ap_groups
                    (id,source_ac_device_id,source_ac_domain_id,source_group_key,display_name,group_scope,member_resource_ids_json,source_revision,scope_status,updated_ms,
                     group_identity_key,group_identity_status,membership_revision,source_observation_ids_json,source_ac_device_ids_json,source_evidence_refs_json,
                     observed_at_ms,conflict_reason,source_group_observations_json)
                VALUES($id,$sourceAc,$domain,$key,$display,$scope,$members,$revision,$status,$updated,
                       $identityKey,$identityStatus,$membershipRevision,$observations,$sourceAcs,$evidence,$observed,$conflict,$groupObservations)
                ON CONFLICT(id) DO UPDATE SET source_ac_device_id=excluded.source_ac_device_id,source_ac_domain_id=excluded.source_ac_domain_id,
                    source_group_key=excluded.source_group_key,display_name=excluded.display_name,group_scope=excluded.group_scope,
                    member_resource_ids_json=excluded.member_resource_ids_json,source_revision=excluded.source_revision,
                    scope_status=excluded.scope_status,updated_ms=excluded.updated_ms,group_identity_key=excluded.group_identity_key,
                    group_identity_status=excluded.group_identity_status,membership_revision=excluded.membership_revision,
                    source_observation_ids_json=excluded.source_observation_ids_json,source_ac_device_ids_json=excluded.source_ac_device_ids_json,
                    source_evidence_refs_json=excluded.source_evidence_refs_json,observed_at_ms=excluded.observed_at_ms,
                    conflict_reason=excluded.conflict_reason,source_group_observations_json=excluded.source_group_observations_json
                """;
            command.Parameters.AddWithValue("$id", group.Id);
            command.Parameters.AddWithValue("$sourceAc", group.SourceAcDeviceId);
            command.Parameters.AddWithValue("$domain", group.SourceAcDomainId);
            command.Parameters.AddWithValue("$key", (object?)group.SourceGroupKey ?? DBNull.Value);
            command.Parameters.AddWithValue("$display", group.DisplayName);
            command.Parameters.AddWithValue("$scope", group.GroupScope);
            command.Parameters.AddWithValue("$members", JsonSerializer.Serialize(group.MemberResourceIds));
            command.Parameters.AddWithValue("$revision", group.SourceRevision);
            command.Parameters.AddWithValue("$status", group.ScopeStatus);
            command.Parameters.AddWithValue("$updated", now);
            command.Parameters.AddWithValue("$identityKey", (object?)group.GroupIdentityKey ?? DBNull.Value);
            command.Parameters.AddWithValue("$identityStatus", group.GroupIdentityStatus);
            command.Parameters.AddWithValue("$membershipRevision", group.MembershipRevision);
            command.Parameters.AddWithValue("$observations", JsonSerializer.Serialize(group.SourceObservationIds));
            command.Parameters.AddWithValue("$sourceAcs", JsonSerializer.Serialize(group.SourceAcDeviceIds));
            command.Parameters.AddWithValue("$evidence", JsonSerializer.Serialize(group.SourceEvidenceRefs));
            command.Parameters.AddWithValue("$observed", group.ObservedAtMs);
            command.Parameters.AddWithValue("$conflict", (object?)group.ConflictReason ?? DBNull.Value);
            command.Parameters.AddWithValue("$groupObservations", JsonSerializer.Serialize(group.SourceGroupObservations));
            command.ExecuteNonQuery();
        }
        using (var compact = db.CreateCommand())
        {
            compact.Transaction = transaction;
            // This only compacts disposable candidate locators. It does not
            // end a group or remove Raw/Facts/history. Equal timestamps have no
            // reliable revision ordering and therefore retain both candidates.
            compact.CommandText = """
                DELETE FROM wireless_ap_groups
                WHERE group_identity_key IS NULL
                  AND id NOT IN (SELECT value FROM json_each($currentIds))
                  AND EXISTS (
                      SELECT 1 FROM json_each($sourceCutoffs) source
                      WHERE source.key=wireless_ap_groups.source_ac_device_id
                        AND wireless_ap_groups.observed_at_ms<CAST(source.value AS INTEGER))
                """;
            compact.Parameters.AddWithValue("$currentIds", JsonSerializer.Serialize(snapshot.ApGroups.Select(item => item.Id)));
            compact.Parameters.AddWithValue("$sourceCutoffs", JsonSerializer.Serialize(sourceCutoffs
                .Where(item => !staleSources.Contains(item.Key)).ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)));
            compact.ExecuteNonQuery();
        }
        var observations = snapshot.ApResources.SelectMany(item => item.Observations).ToArray();
        var manifestId = "wireless-collection:" + snapshot.SourceRevision;
        var firstCaptured = observations.Length == 0 ? snapshot.AsOf : observations.Min(item => item.CapturedMs);
        var lastCaptured = observations.Length == 0 ? snapshot.AsOf : observations.Max(item => item.CapturedMs);
        using (var manifest = db.CreateCommand())
        {
            manifest.Transaction = transaction;
            manifest.CommandText = """
                INSERT INTO wireless_collection_manifests
                    (id,source_revision,scope_status,source_ac_device_ids_json,first_captured_ms,last_captured_ms,observation_count,status,created_ms)
                VALUES($id,$revision,$scope,$sources,$first,$last,$count,'PUBLISHED',$created)
                ON CONFLICT(source_revision) DO UPDATE SET scope_status=excluded.scope_status,
                    source_ac_device_ids_json=excluded.source_ac_device_ids_json,first_captured_ms=excluded.first_captured_ms,
                    last_captured_ms=excluded.last_captured_ms,observation_count=excluded.observation_count,status=excluded.status
                """;
            manifest.Parameters.AddWithValue("$id", manifestId);
            manifest.Parameters.AddWithValue("$revision", snapshot.SourceRevision);
            manifest.Parameters.AddWithValue("$scope", snapshot.Scope.Status);
            manifest.Parameters.AddWithValue("$sources", JsonSerializer.Serialize(observations.Select(item => item.SourceAcDeviceId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)));
            manifest.Parameters.AddWithValue("$first", firstCaptured);
            manifest.Parameters.AddWithValue("$last", lastCaptured);
            manifest.Parameters.AddWithValue("$count", observations.Length);
            manifest.Parameters.AddWithValue("$created", now);
            manifest.ExecuteNonQuery();
        }
        foreach (var observation in observations)
        {
            using var item = db.CreateCommand();
            item.Transaction = transaction;
            item.CommandText = """
                INSERT INTO wireless_collection_manifest_items
                    (manifest_id,observation_id,source_ac_device_id,source_ac_domain_id,source_site_id,raw_manifest_ref,source_revision,captured_ms,outcome)
                VALUES($manifest,$observation,$sourceAc,$domain,$site,$raw,$revision,$captured,$outcome)
                ON CONFLICT(manifest_id,observation_id) DO UPDATE SET source_ac_device_id=excluded.source_ac_device_id,
                    source_ac_domain_id=excluded.source_ac_domain_id,source_site_id=excluded.source_site_id,
                    raw_manifest_ref=excluded.raw_manifest_ref,source_revision=excluded.source_revision,
                    captured_ms=excluded.captured_ms,outcome=excluded.outcome
                """;
            item.Parameters.AddWithValue("$manifest", manifestId);
            item.Parameters.AddWithValue("$observation", observation.Id);
            item.Parameters.AddWithValue("$sourceAc", observation.SourceAcDeviceId);
            item.Parameters.AddWithValue("$domain", observation.SourceAcDomainId);
            item.Parameters.AddWithValue("$site", observation.SourceSiteId);
            item.Parameters.AddWithValue("$raw", observation.RawManifestRef);
            item.Parameters.AddWithValue("$revision", observation.SourceRevision);
            item.Parameters.AddWithValue("$captured", observation.CapturedMs);
            item.Parameters.AddWithValue("$outcome", observation.NormalizedState is "UNKNOWN" or "CONFLICT" ? "AMBIGUOUS" : "OBSERVED");
            item.ExecuteNonQuery();
        }
        transaction.Commit();
        return new(snapshot.AcDomains.Count, snapshot.ApResources.Count, observations.Length, snapshot.ApGroups.Count);
    }

    private static IReadOnlyDictionary<string, long> ReadGroupCacheSourceCutoffs(
        SqliteConnection db, SqliteTransaction transaction, IEnumerable<string> sources)
    {
        using var query = db.CreateCommand();
        query.Transaction = transaction;
        // Observation rows retain the watermark even when a newer snapshot has
        // no groups. Explicit source-group evidence also covers empty groups;
        // use each source's captured time, not a Domain aggregate or cache time.
        query.CommandText = """
            SELECT source_id,MAX(captured_ms) FROM (
                SELECT source_ac_device_id AS source_id,captured_ms
                FROM wireless_ap_observations
                WHERE source_ac_device_id IN (SELECT value FROM json_each($sources))
                UNION ALL
                SELECT json_extract(evidence.value,'$.SourceAcDeviceId'),
                       CAST(json_extract(evidence.value,'$.CapturedMs') AS INTEGER)
                FROM wireless_ap_groups cached,json_each(cached.source_group_observations_json) evidence
                WHERE json_extract(evidence.value,'$.SourceAcDeviceId') IN (SELECT value FROM json_each($sources))
            ) GROUP BY source_id
            """;
        query.Parameters.AddWithValue("$sources", JsonSerializer.Serialize(sources));
        using var reader = query.ExecuteReader();
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        while (reader.Read()) result[reader.GetString(0)] = reader.GetInt64(1);
        return result;
    }
}
