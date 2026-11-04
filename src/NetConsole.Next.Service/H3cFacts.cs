using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace NetConsole.Next.Service;

internal sealed record H3cFactSnapshot(
    string Id,
    string ProfileId,
    string Kind,
    string IdentityKey,
    string Status,
    string Source,
    long CapturedMs,
    bool IsCurrent,
    string PayloadJson)
{
    // The catalog keeps the original STALE status for schema compatibility. These
    // derived fields make the evidence boundary explicit to API and UI consumers.
    public string DataState => Status switch
    {
        "EMPTY" or "UNSUPPORTED" or "PARTIAL" or "FAILED" => Status,
        _ when Source == Catalog.H3cOfflineFixtureSource => "OFFLINE_REAL_FIXTURE_VERIFIED",
        _ => Status
    };

    public string Freshness => Source == Catalog.H3cOfflineFixtureSource
        ? Status == "STALE" ? "EXPIRED_DEVICE_OFFLINE" : "NOT_APPLICABLE"
        : Status == "LIVE_VERIFIED" ? "LIVE" : "UNKNOWN";
}

internal sealed record H3cOfflineReplayResult(
    string ProfileId,
    string Source,
    long CapturedMs,
    int FactCount,
    int CurrentCount,
    int HistoricalCount);

internal sealed partial class Catalog
{
    public const string H3cOfflineFixtureSource = "OFFLINE_REAL_FIXTURE_VERIFIED";

    public H3cOfflineReplayResult ImportH3cOfflineReplay(string profileId,
        IReadOnlyDictionary<string, string> commandOutputs, long capturedMs, string source = H3cOfflineFixtureSource)
    {
        if (string.IsNullOrWhiteSpace(profileId)) throw new ArgumentException("H3C profile id is required.", nameof(profileId));
        if (commandOutputs is null || commandOutputs.Count == 0) throw new ArgumentException("At least one command output is required.", nameof(commandOutputs));
        if (capturedMs <= 0) throw new ArgumentOutOfRangeException(nameof(capturedMs));
        if (string.IsNullOrWhiteSpace(source) || source.Length > 128 || source.Any(char.IsControl))
            throw new ArgumentException("H3C fact source must be a bounded non-control label.", nameof(source));

        using var db = Open();
        using (var profile = db.CreateCommand())
        {
            profile.CommandText = "SELECT 1 FROM h3c_device_profiles WHERE id=$id";
            profile.Parameters.AddWithValue("$id", profileId);
            if (profile.ExecuteScalar() is null) throw new KeyNotFoundException($"H3C profile '{profileId}' does not exist.");
        }

        using var transaction = db.BeginTransaction();
        var scope = CaptureH3cFactScope(db, transaction, profileId);
        var facts = BuildFacts(profileId, commandOutputs, capturedMs, source, scope);
        if (facts.Count == 0) throw new InvalidDataException("H3C replay did not produce a fact or structured command state.");
        foreach (var fact in facts)
        {
            // A revision is immutable, even for historical late arrivals. Validate
            // before changing Current; the transaction rolls back the whole batch.
            using (var existing = db.CreateCommand())
            {
                existing.Transaction = transaction;
                existing.CommandText = @"SELECT source,status,payload_json FROM h3c_facts
                    WHERE profile_id=$profile AND kind=$kind AND identity_key=$identity AND captured_ms=$captured";
                existing.Parameters.AddWithValue("$profile", fact.ProfileId);
                existing.Parameters.AddWithValue("$kind", fact.Kind);
                existing.Parameters.AddWithValue("$identity", fact.IdentityKey);
                existing.Parameters.AddWithValue("$captured", fact.CapturedMs);
                using var reader = existing.ExecuteReader();
                var sameRevision = false;
                while (reader.Read())
                {
                    if (reader.GetString(1) != fact.Status || reader.GetString(2) != fact.PayloadJson)
                        throw new H3cFactRevisionConflictException();
                    sameRevision |= reader.GetString(0) == fact.Source;
                }
                if (sameRevision) continue; // Exact replay performs no fact writes.
            }
            using var newest = db.CreateCommand();
            newest.Transaction = transaction;
            newest.CommandText = @"SELECT MAX(captured_ms) FROM h3c_facts
                WHERE profile_id=$profile AND kind=$kind AND identity_key=$identity";
            newest.Parameters.AddWithValue("$profile", fact.ProfileId);
            newest.Parameters.AddWithValue("$kind", fact.Kind);
            newest.Parameters.AddWithValue("$identity", fact.IdentityKey);
            var maximum = newest.ExecuteScalar();
            var advance = maximum is null or DBNull || fact.CapturedMs > Convert.ToInt64(maximum);
            if (advance)
            using (var clear = db.CreateCommand())
            {
                clear.Transaction = transaction;
                clear.CommandText = @"UPDATE h3c_facts SET is_current=0
                    WHERE profile_id=$profile AND kind=$kind AND identity_key=$identity";
                clear.Parameters.AddWithValue("$profile", fact.ProfileId);
                clear.Parameters.AddWithValue("$kind", fact.Kind);
                clear.Parameters.AddWithValue("$identity", fact.IdentityKey);
                clear.ExecuteNonQuery();
            }
            using var insert = db.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = @"INSERT INTO h3c_facts
                (id,profile_id,kind,identity_key,status,source,captured_ms,is_current,payload_json)
                VALUES($id,$profile,$kind,$identity,$status,$source,$captured,$current,$payload)";
            insert.Parameters.AddWithValue("$id", fact.Id);
            insert.Parameters.AddWithValue("$profile", fact.ProfileId);
            insert.Parameters.AddWithValue("$kind", fact.Kind);
            insert.Parameters.AddWithValue("$identity", fact.IdentityKey);
            insert.Parameters.AddWithValue("$status", fact.Status);
            insert.Parameters.AddWithValue("$source", fact.Source);
            insert.Parameters.AddWithValue("$captured", fact.CapturedMs);
            insert.Parameters.AddWithValue("$current", advance ? 1 : 0);
            insert.Parameters.AddWithValue("$payload", fact.PayloadJson);
            insert.ExecuteNonQuery();
        }
        transaction.Commit();

        using var counts = db.CreateCommand();
        counts.CommandText = "SELECT COUNT(*),COALESCE(SUM(is_current),0) FROM h3c_facts WHERE profile_id=$profile";
        counts.Parameters.AddWithValue("$profile", profileId);
        using var countReader = counts.ExecuteReader();
        countReader.Read();
        return new(profileId, source, capturedMs, facts.Count, countReader.GetInt32(1), countReader.GetInt32(0) - countReader.GetInt32(1));
    }

    public IReadOnlyList<H3cFactSnapshot> QueryH3cFacts(string profileId, string? kind = null,
        bool history = false, int limit = 100, P2B5SiteScopeDecision? scope = null, bool requireCapturedScope = false)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        var filters = new List<string> { "profile_id=$profile" };
        var scopeParameters = new List<(string Name, object? Value)>();
        filters.Add(H3cFactScopePredicate(scope, scopeParameters, "h3c_facts", requireCapturedScope));
        if (!history) filters.Add("is_current=1");
        if (!string.IsNullOrWhiteSpace(kind)) filters.Add("kind=$kind");
        command.CommandText = $@"SELECT id,profile_id,kind,identity_key,status,source,captured_ms,is_current,payload_json
            FROM h3c_facts WHERE {string.Join(" AND ", filters)}
            ORDER BY captured_ms DESC,kind,identity_key,source,id LIMIT $limit";
        command.Parameters.AddWithValue("$profile", profileId);
        if (!string.IsNullOrWhiteSpace(kind)) command.Parameters.AddWithValue("$kind", kind.Trim());
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        DeviceSiteScope.AddParameters(command, scopeParameters);
        using var reader = command.ExecuteReader();
        var rows = new List<H3cFactSnapshot>();
        while (reader.Read())
            rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetInt64(6), reader.GetInt32(7) != 0, reader.GetString(8)));
        return rows;
    }

    private static IReadOnlyList<H3cFactSnapshot> BuildFacts(string profileId,
        IReadOnlyDictionary<string, string> outputs, long capturedMs, string source, H3cFactScope? scope)
    {
        var facts = new List<H3cFactSnapshot>();
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        void Add(string kind, string identity, object payload, string status = "STALE")
        {
            var json = payload is string text ? text : JsonSerializer.Serialize(payload, jsonOptions);
            if (scope is not null)
            {
                var node = JsonNode.Parse(json)!.AsObject();
                node["capturedScope"] = JsonSerializer.SerializeToNode(scope, jsonOptions);
                json = node.ToJsonString(jsonOptions);
            }
            var idInput = $"{profileId}|{kind}|{identity}|{source}|{capturedMs}";
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(idInput))).ToLowerInvariant()[..32];
            facts.Add(new(id, profileId, kind, identity, status, source, capturedMs, true, json));
        }

        if (outputs.TryGetValue(H3cCommandPolicy.DisplayVersion, out var versionOutput))
        {
            var version = H3cComwareParser.ParseVersion(versionOutput);
            Add("device.identity", "device", version, version.Recognized ? "STALE" : "UNSUPPORTED");
        }

        var brief = outputs.TryGetValue(H3cCommandPolicy.DisplayInterfaceBrief, out var briefOutput)
            ? H3cComwareParser.ParseInterfaceBrief(briefOutput).Interfaces
            : Array.Empty<H3cInterfaceStatus>();
        var details = outputs.TryGetValue(H3cCommandPolicy.DisplayInterface, out var interfaceOutput)
            ? H3cComwareParser.ParseInterfaceDetails(interfaceOutput)
            : Array.Empty<H3cInterfaceDetailFact>();
        var detailByName = details.ToDictionary(item => item.InterfaceName, StringComparer.OrdinalIgnoreCase);
        foreach (var row in brief)
        {
            detailByName.TryGetValue(row.InterfaceName, out var detail);
            Add("interface", row.InterfaceName, new
            {
                row.InterfaceName, row.LinkStatus, row.ProtocolStatus, row.IpAddress, row.Speed,
                row.Duplex, row.PortMode, row.Vlan, row.Description,
                radioMode = detail?.RadioMode, channel = detail?.Channel,
                inputBytes = detail?.InputBytes, outputBytes = detail?.OutputBytes
            });
        }
        foreach (var detail in details.Where(item => !brief.Any(row => string.Equals(row.InterfaceName, item.InterfaceName, StringComparison.OrdinalIgnoreCase))))
            Add("interface", detail.InterfaceName, detail);
        foreach (var detail in details.Where(item => item.InterfaceName.StartsWith("WLAN-Radio", StringComparison.OrdinalIgnoreCase)))
            Add("radio", detail.InterfaceName, detail);

        if (outputs.TryGetValue(H3cCommandPolicy.DisplayWlanMeshProfile, out var meshProfileOutput))
        {
            var profile = H3cComwareParser.ParseMeshProfile(meshProfileOutput);
            Add("mesh.profile", profile.Number?.ToString() ?? "default", profile,
                profile.MeshId is null ? "EMPTY" : "STALE");
        }
        if (outputs.TryGetValue(H3cCommandPolicy.DisplayWlanMeshLink, out var meshLinkOutput))
        {
            foreach (var link in H3cComwareParser.ParseMeshLink(meshLinkOutput))
            {
                var identity = StableMeshPeerKey(link);
                Add("mesh.peer", identity, link);
            }
        }
        if (outputs.TryGetValue(H3cCommandPolicy.DisplayLldpNeighborInformationList, out var lldpOutput))
        {
            foreach (var neighbor in H3cComwareParser.ParseLldpList(lldpOutput))
                Add("lldp.neighbor", $"{neighbor.LocalInterface}|{neighbor.NeighborInterface}|{neighbor.ChassisId}", neighbor);
        }

        foreach (var command in new[] { H3cCommandPolicy.DisplayWlanApAll, H3cCommandPolicy.DisplayWlanApAllAddress, H3cCommandPolicy.DisplayWlanServiceTemplateAll, H3cCommandPolicy.DisplayWlanClient })
        {
            if (!outputs.TryGetValue(command, out var output)) continue;
            var transcript = CliTranscriptParser.Parse(output, command);
            var status = transcript.Status switch
            {
                CliTranscriptStatus.Unsupported or CliTranscriptStatus.PermissionDenied => "UNSUPPORTED",
                CliTranscriptStatus.Paged or CliTranscriptStatus.Incomplete => "PARTIAL",
                CliTranscriptStatus.Empty => "EMPTY",
                _ when string.IsNullOrWhiteSpace(transcript.Output) => "EMPTY",
                _ => "STALE"
            };
            Add($"command.{command.Replace(' ', '.')}", "default", new
            {
                command,
                status = transcript.Status.ToString().ToUpperInvariant(),
                output = transcript.Output,
                notice = transcript.Notice
            }, status);
        }
        return facts;

        static string StableMeshPeerKey(H3cMeshLinkFact link)
        {
            var peerMac = CanonicalIdentity(link.PeerMac);
            if (peerMac.Length > 0) return $"mac:{peerMac}";
            var peerName = CanonicalIdentity(link.PeerName);
            var bssid = CanonicalIdentity(link.Bssid);
            var iface = CanonicalIdentity(link.InterfaceName);
            return $"peer:{peerName}|bssid:{bssid}|interface:{iface}";
        }

        static string CanonicalIdentity(string? value)
        {
            var normalized = string.Join(' ', (value ?? string.Empty).Trim().Split((char[]?)null,
                StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
            if (normalized.Length == 0) return string.Empty;
            var compactMac = normalized.Replace("-", string.Empty, StringComparison.Ordinal)
                .Replace(":", string.Empty, StringComparison.Ordinal)
                .Replace(".", string.Empty, StringComparison.Ordinal);
            return compactMac.Length == 12 && compactMac.All(Uri.IsHexDigit) ? compactMac : normalized;
        }
    }
}
