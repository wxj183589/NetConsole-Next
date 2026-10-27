using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace NetConsole.Next.Service;

internal sealed class P2B5RailReferenceException(string code, string detail, int statusCode = 409) : Exception(detail)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

internal sealed record P2B5RailEntityInput(string Id, string Type, string Name);
internal sealed record P2B5RailRelationInput(string Kind, string ParentId, string ChildId);
internal sealed record P2B5RailReferenceImportRequest(
    string? IdempotencyKey,
    string SourceName,
    long SourceRevision,
    string? SourceHash,
    string ScopeReference,
    string SchemaVersion,
    string EffectiveAt,
    string CapturedAt,
    IReadOnlyList<P2B5RailEntityInput>? Entities,
    IReadOnlyList<P2B5RailRelationInput>? Relations);

internal sealed record P2B5RailPreviewSummary(
    int NewCount,
    int UpdateCount,
    int UnchangedCount,
    int ConflictCount,
    int DeletionCandidateCount,
    IReadOnlyList<string> Conflicts,
    IReadOnlyList<string> UnknownReferences);

internal sealed record P2B5RailReferenceImportStatus(
    string Id,
    string Actor,
    string IdempotencyKey,
    string SourceName,
    long SourceRevision,
    string SourceHash,
    string ScopeReference,
    string SchemaVersion,
    string State,
    P2B5RailPreviewSummary Preview,
    long CreatedMs,
    long UpdatedMs,
    long ExpiresMs);

internal sealed record P2B5RailReferenceEntity(
    string Id,
    string Type,
    string Name,
    string ScopeReference,
    string SourceName,
    long SourceRevision,
    string SourceHash,
    string EffectiveAt,
    string CapturedAt,
    string Lifecycle,
    long UpdatedMs);

internal sealed class P2B5RailReferenceService
{
    private const int MaximumEntities = 2_000;
    private const int MaximumRelations = 8_000;
    private const string SupportedSchemaVersion = "1";
    private const int PreviewLifetimeMs = 15 * 60 * 1000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly RuntimeSettings _settings;
    private string DatabasePath => Path.Combine(_settings.DataRoot, "catalog.db");

    public P2B5RailReferenceService(RuntimeSettings settings) => _settings = settings;

    public bool IsAuthorityAvailable()
    {
        if (!P2B4WriteFeatureGate.EnvironmentEnabled() || IsReservedRoot(_settings.DataRoot) ||
            !IsTemporaryRoot(_settings.DataRoot) || !File.Exists(DatabasePath)) return false;
        try
        {
            using var connection = Open();
            P2B4SchemaV14Migration.ValidateV14Schema(connection, _settings.DataRoot);
            ValidateRailSchema(connection);
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or SqliteException or IOException)
        {
            return false;
        }
    }

    public P2B5RailReferenceImportStatus Preview(WebUserIdentity actor, P2B5RailReferenceImportRequest request)
    {
        EnsureAvailable();
        var normalized = NormalizeAndValidate(request);
        using var connection = Open();
        var fingerprint = Fingerprint(normalized);
        var existing = FindByIdempotency(connection, actor.Username, normalized.IdempotencyKey!, fingerprint);
        if (existing is not null) return existing;

        var analysis = Analyze(connection, normalized);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var importId = Guid.NewGuid().ToString("N");
        var state = analysis.ConflictCount == 0 ? "PREVIEWED" : "CONFLICT";
        var previewJson = JsonSerializer.Serialize(analysis, JsonOptions);
        var requestJson = JsonSerializer.Serialize(normalized, JsonOptions);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO p2b5_rail_reference_imports
                (id,actor,idempotency_key,request_fingerprint,source_name,source_revision,source_hash,scope_reference,schema_version,state,request_json,preview_json,created_ms,updated_ms,expires_ms)
            VALUES($id,$actor,$key,$fingerprint,$source,$revision,$hash,$scope,$schema,$state,$request,$preview,$now,$now,$expires)
            """;
        Add(command, "$id", importId); Add(command, "$actor", actor.Username); Add(command, "$key", normalized.IdempotencyKey);
        Add(command, "$fingerprint", fingerprint); Add(command, "$source", normalized.SourceName);
        Add(command, "$revision", normalized.SourceRevision); Add(command, "$hash", normalized.SourceHash);
        Add(command, "$scope", normalized.ScopeReference); Add(command, "$schema", normalized.SchemaVersion);
        Add(command, "$state", state); Add(command, "$request", requestJson); Add(command, "$preview", previewJson);
        Add(command, "$now", now); Add(command, "$expires", now + PreviewLifetimeMs); command.ExecuteNonQuery();
        return new(importId, actor.Username, normalized.IdempotencyKey!, normalized.SourceName, normalized.SourceRevision,
            normalized.SourceHash!, normalized.ScopeReference, normalized.SchemaVersion, state, analysis, now, now, now + PreviewLifetimeMs);
    }

    public P2B5RailReferenceImportStatus? Get(WebUserIdentity actor, string id)
    {
        EnsureAvailable();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,actor,idempotency_key,source_name,source_revision,source_hash,scope_reference,schema_version,state,preview_json,created_ms,updated_ms,expires_ms FROM p2b5_rail_reference_imports WHERE id=$id AND actor=$actor";
        Add(command, "$id", Required(id, "import id")); Add(command, "$actor", actor.Username);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadStatus(reader) : null;
    }

    public P2B5RailReferenceImportStatus Confirm(WebUserIdentity actor, string id)
    {
        EnsureAvailable();
        using var connection = Open();
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT id,actor,idempotency_key,source_name,source_revision,source_hash,scope_reference,schema_version,state,request_json,preview_json,created_ms,updated_ms,expires_ms FROM p2b5_rail_reference_imports WHERE id=$id AND actor=$actor";
        Add(read, "$id", Required(id, "import id")); Add(read, "$actor", actor.Username);
        using var reader = read.ExecuteReader();
        if (!reader.Read()) throw new P2B5RailReferenceException("RAIL_IMPORT_NOT_FOUND", "The rail reference import was not found.", 404);
        var status = ReadStatus(reader, previewColumn: 10);
        var request = JsonSerializer.Deserialize<P2B5RailReferenceImportRequest>(reader.GetString(9), JsonOptions)
            ?? throw new InvalidDataException("The stored rail reference request is invalid.");
        reader.Close();

        if (status.State == "SUCCEEDED") return status;
        if (status.State == "CONFLICT")
            throw new P2B5RailReferenceException("RAIL_IMPORT_CONFLICT", "The preview contains conflicts and cannot be confirmed.");
        if (status.State != "PREVIEWED")
            throw new P2B5RailReferenceException("RAIL_IMPORT_STATE_INVALID", "Only a PREVIEWED import can be confirmed.");
        if (status.ExpiresMs < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            throw new P2B5RailReferenceException("RAIL_IMPORT_EXPIRED", "The rail reference preview has expired.");

        var analysis = Analyze(connection, request);
        if (analysis.ConflictCount != 0 || analysis.NewCount != status.Preview.NewCount || analysis.UpdateCount != status.Preview.UpdateCount)
            throw new P2B5RailReferenceException("RAIL_IMPORT_STALE_PREVIEW", "The reference authority changed after preview; create a new preview.");

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
        try
        {
            foreach (var entity in request.Entities!)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO p2b5_rail_entities
                        (entity_id,entity_type,name,scope_reference,source_name,source_revision,source_hash,effective_at,captured_at,lifecycle,imported_by,import_id,created_ms,updated_ms)
                    VALUES($id,$type,$name,$scope,$source,$revision,$hash,$effective,$captured,'ACTIVE',$actor,$import,$now,$now)
                    ON CONFLICT(entity_id) DO UPDATE SET entity_type=excluded.entity_type,name=excluded.name,scope_reference=excluded.scope_reference,
                        source_name=excluded.source_name,source_revision=excluded.source_revision,source_hash=excluded.source_hash,
                        effective_at=excluded.effective_at,captured_at=excluded.captured_at,lifecycle='ACTIVE',imported_by=excluded.imported_by,
                        import_id=excluded.import_id,updated_ms=excluded.updated_ms
                    """;
                Add(command, "$id", entity.Id); Add(command, "$type", entity.Type); Add(command, "$name", entity.Name);
                Add(command, "$scope", request.ScopeReference); Add(command, "$source", request.SourceName);
                Add(command, "$revision", request.SourceRevision); Add(command, "$hash", status.SourceHash);
                Add(command, "$effective", request.EffectiveAt); Add(command, "$captured", request.CapturedAt);
                Add(command, "$actor", actor.Username); Add(command, "$import", status.Id); Add(command, "$now", now);
                command.ExecuteNonQuery();
            }

            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM p2b5_rail_relations WHERE source_name=$source AND scope_reference=$scope";
                Add(delete, "$source", request.SourceName); Add(delete, "$scope", request.ScopeReference); delete.ExecuteNonQuery();
            }
            foreach (var relation in request.Relations!)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO p2b5_rail_relations
                        (relation_kind,parent_id,child_id,scope_reference,source_name,source_revision,source_hash,import_id,created_ms)
                    VALUES($kind,$parent,$child,$scope,$source,$revision,$hash,$import,$now)
                    """;
                Add(command, "$kind", relation.Kind); Add(command, "$parent", relation.ParentId); Add(command, "$child", relation.ChildId);
                Add(command, "$scope", request.ScopeReference); Add(command, "$source", request.SourceName);
                Add(command, "$revision", request.SourceRevision); Add(command, "$hash", status.SourceHash);
                Add(command, "$import", status.Id); Add(command, "$now", now); command.ExecuteNonQuery();
            }
            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = "UPDATE p2b5_rail_reference_imports SET state='SUCCEEDED',updated_ms=$now WHERE id=$id AND actor=$actor";
                Add(update, "$now", now); Add(update, "$id", status.Id); Add(update, "$actor", actor.Username); update.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        catch
        {
            try { transaction.Rollback(); } catch { }
            throw;
        }
        return status with { State = "SUCCEEDED", UpdatedMs = now };
    }

    public IReadOnlyList<P2B5RailReferenceEntity> ListEntities(string? scopeReference, string? type)
    {
        EnsureAvailable();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT entity_id,entity_type,name,scope_reference,source_name,source_revision,source_hash,effective_at,captured_at,lifecycle,updated_ms
            FROM p2b5_rail_entities
            WHERE ($scope IS NULL OR scope_reference=$scope) AND ($type IS NULL OR entity_type=$type) AND lifecycle='ACTIVE'
            ORDER BY entity_type,entity_id
            LIMIT 2000
            """;
        Add(command, "$scope", string.IsNullOrWhiteSpace(scopeReference) ? null : scopeReference.Trim());
        Add(command, "$type", string.IsNullOrWhiteSpace(type) ? null : NormalizeType(type));
        using var reader = command.ExecuteReader();
        var result = new List<P2B5RailReferenceEntity>();
        while (reader.Read())
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetInt64(5), reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetInt64(10)));
        return result;
    }

    private P2B5RailPreviewSummary Analyze(SqliteConnection connection, P2B5RailReferenceImportRequest request)
    {
        var conflicts = new List<string>();
        var unknownReferences = new List<string>();
        var byId = request.Entities!.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var relationKeys = new HashSet<string>(StringComparer.Ordinal);
        var inbound = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var relation in request.Relations!)
        {
            var key = relation.Kind + "|" + relation.ParentId + "|" + relation.ChildId;
            if (!relationKeys.Add(key)) conflicts.Add("DUPLICATE_RELATION:" + key);
            if (!byId.TryGetValue(relation.ParentId, out var parent) || !byId.TryGetValue(relation.ChildId, out var child))
            {
                unknownReferences.Add(key); continue;
            }
            var expected = relation.Kind switch
            {
                "LINE_SECTION" => ("LINE", "SECTION"),
                "SECTION_STATION" => ("SECTION", "STATION"),
                "STATION_SITE" => ("STATION", "SITE"),
                "LINE_STATION" => ("LINE", "STATION"),
                _ => ("", "")
            };
            if (expected == ("", "") || parent.Type != expected.Item1 || child.Type != expected.Item2)
                conflicts.Add("RELATION_TYPE:" + key);
            if (relation.Kind is "LINE_SECTION" or "SECTION_STATION" or "STATION_SITE")
            {
                if (!inbound.TryGetValue(child.Id, out var kinds)) inbound[child.Id] = kinds = new(StringComparer.Ordinal);
                kinds.Add(relation.Kind);
            }
        }
        foreach (var entity in request.Entities!)
        {
            if (entity.Type == "SECTION" && (!inbound.TryGetValue(entity.Id, out var sectionKinds) || !sectionKinds.Contains("LINE_SECTION"))) conflicts.Add("MISSING_LINE_PARENT:" + entity.Id);
            if (entity.Type == "STATION" && (!inbound.TryGetValue(entity.Id, out var stationKinds) || !stationKinds.Contains("SECTION_STATION"))) conflicts.Add("MISSING_SECTION_PARENT:" + entity.Id);
            if (entity.Type == "SITE" && (!inbound.TryGetValue(entity.Id, out var siteKinds) || !siteKinds.Contains("STATION_SITE"))) conflicts.Add("MISSING_STATION_PARENT:" + entity.Id);
        }

        var newCount = 0; var updateCount = 0; var unchangedCount = 0;
        foreach (var entity in request.Entities!)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT entity_type,name,scope_reference,source_name,source_revision,source_hash FROM p2b5_rail_entities WHERE entity_id=$id";
            Add(command, "$id", entity.Id);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) { newCount++; continue; }
            var oldType = reader.GetString(0); var oldName = reader.GetString(1); var oldScope = reader.GetString(2);
            var oldSource = reader.GetString(3); var oldRevision = reader.GetInt64(4); var oldHash = reader.GetString(5);
            if (oldType != entity.Type || oldSource != request.SourceName || oldScope != request.ScopeReference)
                conflicts.Add("ENTITY_SOURCE_OR_SCOPE:" + entity.Id);
            else if (request.SourceRevision < oldRevision)
                conflicts.Add("STALE_REVISION:" + entity.Id);
            else if (request.SourceRevision == oldRevision && !string.Equals(oldHash, request.SourceHash, StringComparison.OrdinalIgnoreCase))
                conflicts.Add("REVISION_HASH_MISMATCH:" + entity.Id);
            else if (request.SourceRevision == oldRevision && oldName == entity.Name) unchangedCount++;
            else updateCount++;
        }
        var deletionCandidates = 0;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT entity_id FROM p2b5_rail_entities WHERE source_name=$source AND scope_reference=$scope AND lifecycle='ACTIVE'";
            Add(command, "$source", request.SourceName); Add(command, "$scope", request.ScopeReference);
            using var reader = command.ExecuteReader();
            var incoming = request.Entities!.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            while (reader.Read()) if (!incoming.Contains(reader.GetString(0))) deletionCandidates++;
        }
        var uniqueConflicts = conflicts.Distinct(StringComparer.Ordinal).ToArray();
        return new(newCount, updateCount, unchangedCount, uniqueConflicts.Length + unknownReferences.Count,
            deletionCandidates, uniqueConflicts, unknownReferences.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static P2B5RailReferenceImportRequest NormalizeAndValidate(P2B5RailReferenceImportRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 128 || request.IdempotencyKey.Any(char.IsControl))
            throw new P2B5RailReferenceException("RAIL_IDEMPOTENCY_REQUIRED", "A bounded idempotency key is required.", 400);
        Required(request.SourceName, "source name"); Required(request.ScopeReference, "scope reference");
        Required(request.EffectiveAt, "effectiveAt"); Required(request.CapturedAt, "capturedAt");
        if (request.SourceRevision < 1) throw new P2B5RailReferenceException("RAIL_REVISION_INVALID", "sourceRevision must be positive.", 400);
        if (!string.Equals(request.SchemaVersion, SupportedSchemaVersion, StringComparison.Ordinal))
            throw new P2B5RailReferenceException("RAIL_SCHEMA_UNSUPPORTED", "Only rail reference schema version 1 is supported.", 409);
        var entities = request.Entities ?? Array.Empty<P2B5RailEntityInput>();
        var relations = request.Relations ?? Array.Empty<P2B5RailRelationInput>();
        if (entities.Count is 0 or > MaximumEntities || relations.Count > MaximumRelations)
            throw new P2B5RailReferenceException("RAIL_INPUT_LIMIT", "The rail reference fixture exceeds the bounded import limit.", 413);
        var normalizedEntities = new List<P2B5RailEntityInput>(entities.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entity in entities)
        {
            var id = ValidateStableId(entity.Id, "entity id");
            var type = NormalizeType(entity.Type);
            var name = Required(entity.Name, "entity name");
            if (!ids.Add(id)) throw new P2B5RailReferenceException("RAIL_DUPLICATE_ID", "Stable entity IDs must be unique.", 409);
            normalizedEntities.Add(new(id, type, name));
        }
        var normalizedRelations = new List<P2B5RailRelationInput>(relations.Count);
        foreach (var relation in relations)
        {
            var kind = relation.Kind.Trim().ToUpperInvariant();
            if (kind is not ("LINE_SECTION" or "SECTION_STATION" or "STATION_SITE" or "LINE_STATION"))
                throw new P2B5RailReferenceException("RAIL_RELATION_INVALID", "The rail relation kind is unsupported.", 400);
            normalizedRelations.Add(new(kind, ValidateStableId(relation.ParentId, "relation parent id"), ValidateStableId(relation.ChildId, "relation child id")));
        }
        var normalized = request with { IdempotencyKey = request.IdempotencyKey.Trim(), SourceName = request.SourceName.Trim(), ScopeReference = request.ScopeReference.Trim(),
            SchemaVersion = request.SchemaVersion.Trim(), Entities = normalizedEntities, Relations = normalizedRelations };
        var computed = ComputeHash(normalized);
        if (!string.IsNullOrWhiteSpace(request.SourceHash) && !string.Equals(request.SourceHash, computed, StringComparison.OrdinalIgnoreCase))
            throw new P2B5RailReferenceException("RAIL_SOURCE_HASH_MISMATCH", "The supplied source hash does not match the canonical payload.", 409);
        return normalized with { SourceHash = computed };
    }

    private P2B5RailReferenceImportStatus? FindByIdempotency(SqliteConnection connection, string actor, string key, string fingerprint)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,actor,idempotency_key,source_name,source_revision,source_hash,scope_reference,schema_version,state,preview_json,created_ms,updated_ms,expires_ms,request_fingerprint FROM p2b5_rail_reference_imports WHERE actor=$actor AND idempotency_key=$key";
        Add(command, "$actor", actor); Add(command, "$key", key);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (!string.Equals(reader.GetString(13), fingerprint, StringComparison.Ordinal))
            throw new P2B5RailReferenceException("RAIL_IDEMPOTENCY_CONFLICT", "The idempotency key was already used for a different import.", 409);
        return ReadStatus(reader);
    }

    private static P2B5RailReferenceImportStatus ReadStatus(SqliteDataReader reader, int previewColumn = 9)
    {
        var preview = JsonSerializer.Deserialize<P2B5RailPreviewSummary>(reader.GetString(previewColumn), JsonOptions)
            ?? throw new InvalidDataException("The stored rail reference preview is invalid.");
        return new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4),
            reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8), preview,
            reader.GetInt64(previewColumn + 1), reader.GetInt64(previewColumn + 2), reader.GetInt64(previewColumn + 3));
    }

    private void EnsureAvailable()
    {
        if (!IsAuthorityAvailable()) throw new P2B5RailReferenceException("RAIL_REFERENCE_UNAVAILABLE", "Rail reference runtime requires an isolated v14 authority and explicit test gates.", 503);
    }

    private static string Fingerprint(P2B5RailReferenceImportRequest request) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        request.SourceName + "|" + request.SourceRevision + "|" + request.SourceHash + "|" + request.ScopeReference + "|" + request.SchemaVersion))).ToLowerInvariant();

    private static string ComputeHash(P2B5RailReferenceImportRequest request)
    {
        var canonical = JsonSerializer.Serialize(request with { SourceHash = null }, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string NormalizeType(string value) => value.Trim().ToUpperInvariant() switch
    {
        "LINE" => "LINE", "SECTION" => "SECTION", "STATION" => "STATION", "SITE" => "SITE",
        _ => throw new P2B5RailReferenceException("RAIL_ENTITY_TYPE_INVALID", "The rail entity type is unsupported.", 400)
    };

    private static string ValidateStableId(string value, string label)
    {
        var result = Required(value, label).Trim();
        if (result.Length > 128 || !char.IsLetterOrDigit(result[0]) || result.Any(character => !char.IsLetterOrDigit(character) && character is not ('.' or '_' or ':' or '-')))
            throw new P2B5RailReferenceException("RAIL_STABLE_ID_INVALID", $"The {label} must be an explicit stable identifier.", 400);
        return result;
    }

    private static string Required(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
            throw new P2B5RailReferenceException("RAIL_FIELD_INVALID", $"The {label} is invalid.", 400);
        return value.Trim();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false, DefaultTimeout = 5 }.ToString());
        connection.Open(); using var pragma = connection.CreateCommand(); pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;"; pragma.ExecuteNonQuery(); return connection;
    }

    private static void ValidateRailSchema(SqliteConnection connection)
    {
        foreach (var table in new[] { "p2b5_rail_reference_imports", "p2b5_rail_entities", "p2b5_rail_relations" })
        {
            using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name"; Add(command, "$name", table);
            if (Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 1) throw new InvalidDataException("Rail reference schema is missing: " + table);
        }
    }

    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    private static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static bool IsReservedRoot(string root) => string.Equals(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath("D:\\NetConsoleData").TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath("D:\\NetConsoleData-dev").TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    private static bool IsTemporaryRoot(string root) { var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar; var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar; return full.StartsWith(temp, StringComparison.OrdinalIgnoreCase); }
}

internal sealed record P2B5RailReferenceMigrationContext(string DatabasePath, string DataRoot, bool IsolatedTest)
{
    public const string ExplicitMigrationEnvironmentVariable = "NETCONSOLE_NEXT_P2B5_RAIL_REFERENCE_MIGRATION_ALLOWED";
    public static P2B5RailReferenceMigrationContext ForIsolatedTest(string databasePath, string dataRoot) => new(databasePath, dataRoot, true);

    public void Validate(SqliteConnection connection)
    {
        if (!IsolatedTest || Environment.GetEnvironmentVariable(ExplicitMigrationEnvironmentVariable) != "1" || !P2B4WriteFeatureGate.EnvironmentEnabled())
            throw new InvalidOperationException("Rail reference migration requires the explicit isolated P2B4 and rail migration gates.");
        var databasePath = P2B2MigrationPathSecurity.CanonicalizeDatabasePath(DatabasePath, "migration context");
        var dataRoot = P2B2MigrationPathSecurity.CanonicalizeDatabasePath(DataRoot, "data root");
        var tempRoot = P2B2MigrationPathSecurity.CanonicalizeDatabasePath(Path.GetTempPath(), "temporary root");
        P2B2MigrationPathSecurity.ValidateTemporaryDatabaseBoundary(databasePath, tempRoot);
        P2B2MigrationPathSecurity.ValidateTemporaryDatabaseBoundary(dataRoot, tempRoot);
        P2B2MigrationPathSecurity.ValidateNoReparsePoints(tempRoot);
        P2B2MigrationPathSecurity.ValidateNoReparsePoints(dataRoot);
        P2B2MigrationPathSecurity.ValidateNoReparsePoints(databasePath);
        if (!P2B2MigrationPathSecurity.PathsEqual(P2B2MigrationPathSecurity.CanonicalizeDatabasePath(connection.DataSource, "open connection"), databasePath))
            throw new InvalidOperationException("Rail reference migration connection path does not match.");
    }
}

internal sealed record P2B5RailReferenceMigrationResult(bool AlreadyApplied, int BaseSchemaVersion);

internal static class P2B5RailReferenceMigration
{
    public static P2B5RailReferenceMigrationResult Apply(SqliteConnection connection, P2B5RailReferenceMigrationContext context)
    {
        context.Validate(connection);
        if (connection.State != System.Data.ConnectionState.Open) throw new InvalidOperationException("The migration connection must already be open.");
        var version = ReadVersion(connection);
        if (version != 14) throw new InvalidDataException($"Rail reference migration requires Schema v14, found {version}.");
        using var exists = connection.CreateCommand(); exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='p2b5_rail_reference_imports'";
        if (Convert.ToInt32(exists.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1) { Validate(connection); return new(true, 14); }
        using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
        try
        {
            using var resource = typeof(P2B5RailReferenceMigration).Assembly.GetManifestResourceStream("schema_p2b5_rail_reference.sql") ?? throw new InvalidOperationException("Embedded rail reference schema was not found.");
            using var reader = new StreamReader(resource); using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = reader.ReadToEnd(); command.ExecuteNonQuery(); transaction.Commit(); Validate(connection); return new(false, 14);
        }
        catch { try { transaction.Rollback(); } catch { } throw; }
    }

    private static int ReadVersion(SqliteConnection connection) { using var command = connection.CreateCommand(); command.CommandText = "SELECT COALESCE(MAX(version),0) FROM schema_versions"; return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture); }
    private static void Validate(SqliteConnection connection) { foreach (var table in new[] { "p2b5_rail_reference_imports", "p2b5_rail_entities", "p2b5_rail_relations" }) { using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name"; command.Parameters.AddWithValue("$name", table); if (Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 1) throw new InvalidDataException("Rail reference schema is missing: " + table); } }
}
