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
            using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO wireless_ap_groups
                    (id,source_ac_device_id,source_ac_domain_id,source_group_key,display_name,group_scope,member_resource_ids_json,source_revision,scope_status,updated_ms)
                VALUES($id,$sourceAc,$domain,$key,$display,$scope,$members,$revision,$status,$updated)
                ON CONFLICT(id) DO UPDATE SET source_ac_device_id=excluded.source_ac_device_id,source_ac_domain_id=excluded.source_ac_domain_id,
                    source_group_key=excluded.source_group_key,display_name=excluded.display_name,group_scope=excluded.group_scope,
                    member_resource_ids_json=excluded.member_resource_ids_json,source_revision=excluded.source_revision,
                    scope_status=excluded.scope_status,updated_ms=excluded.updated_ms
                """;
            command.Parameters.AddWithValue("$id", group.Id);
            command.Parameters.AddWithValue("$sourceAc", group.SourceAcDeviceId);
            command.Parameters.AddWithValue("$domain", group.SourceAcDomainId);
            command.Parameters.AddWithValue("$key", group.SourceGroupKey);
            command.Parameters.AddWithValue("$display", group.DisplayName);
            command.Parameters.AddWithValue("$scope", group.GroupScope);
            command.Parameters.AddWithValue("$members", JsonSerializer.Serialize(group.MemberResourceIds));
            command.Parameters.AddWithValue("$revision", group.SourceRevision);
            command.Parameters.AddWithValue("$status", group.ScopeStatus);
            command.Parameters.AddWithValue("$updated", now);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        return new(snapshot.AcDomains.Count, snapshot.ApResources.Count,
            snapshot.ApResources.Sum(item => item.Observations.Count), snapshot.ApGroups.Count);
    }
}
