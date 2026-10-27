using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace NetConsole.Next.Service;

internal sealed class P2B5ExportException(string code, string detail, int statusCode = 409) : Exception(detail)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

internal sealed record P2B5ExportRequest(string? IdempotencyKey, string SiteId, string? DomainId, string Format);

internal sealed record P2B5ExportDomainRow(
    string Id,
    string Name,
    string DomainKind,
    string SourceKind,
    string SourceReference,
    long Revision,
    IReadOnlyList<string> SiteReferences);

internal sealed record P2B5ExportSnapshot(
    string SchemaVersion,
    string CapturedAt,
    string ScopeKind,
    string ScopeReference,
    string? DomainFilter,
    IReadOnlyList<P2B5ExportDomainRow> Domains,
    long MaxRevision);

internal sealed record P2B5ExportManifest(
    string ExportId,
    string Format,
    string Encoding,
    int FieldVersion,
    string Source,
    long SourceRevision,
    string CapturedAt,
    string ScopeKind,
    string ScopeReference,
    int RowCount,
    string ContentSha256,
    string State);

internal sealed record P2B5ExportStatus(
    string Id,
    string Actor,
    string IdempotencyKey,
    string Format,
    string Status,
    P2B5ExportManifest Manifest,
    long CreatedMs,
    long UpdatedMs,
    string? ErrorCode);

internal sealed record P2B5ExportDownload(Stream Stream, string Name, string ContentType);

internal sealed class P2B5ExportService
{
    private const int FieldVersion = 1;
    private const int MaximumRows = 10_000;
    private const int MaximumSnapshotBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly RuntimeSettings _settings;
    private string DatabasePath => Path.Combine(_settings.DataRoot, "catalog.db");

    public P2B5ExportService(RuntimeSettings settings) => _settings = settings;

    public bool IsAuthorityAvailable()
    {
        if (!P2B4WriteFeatureGate.EnvironmentEnabled() || IsReservedRoot(_settings.DataRoot) ||
            !IsTemporaryRoot(_settings.DataRoot) || !File.Exists(DatabasePath)) return false;
        try
        {
            using var connection = Open();
            P2B4SchemaV14Migration.ValidateV14Schema(connection, _settings.DataRoot);
            ValidateExportSchema(connection);
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or SqliteException or IOException)
        {
            return false;
        }
    }

    public P2B5ExportStatus Create(WebUserIdentity actor, P2B5ExportRequest request)
    {
        EnsureAvailable();
        ValidateRequest(request);
        var format = NormalizeFormat(request.Format);
        var fingerprint = Fingerprint(request.SiteId + "|" + request.DomainId + "|" + format);
        using var connection = Open();
        var existing = FindByIdempotency(connection, actor.Username, request.IdempotencyKey!, fingerprint);
        if (existing is not null) return existing;

        var exportId = Guid.NewGuid().ToString("N");
        var snapshot = CaptureSnapshot(connection, request.SiteId, request.DomainId);
        var snapshotJson = JsonSerializer.Serialize(snapshot, JsonOptions);
        if (Encoding.UTF8.GetByteCount(snapshotJson) > MaximumSnapshotBytes)
            throw new P2B5ExportException("EXPORT_SNAPSHOT_LIMIT", "The export snapshot exceeds the isolated size limit.", 413);

        var content = Render(format, snapshot);
        var bytes = Encoding.UTF8.GetBytes(content);
        if (bytes.Length > MaximumSnapshotBytes)
            throw new P2B5ExportException("EXPORT_OUTPUT_LIMIT", "The export output exceeds the isolated size limit.", 413);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var capturedAt = snapshot.CapturedAt;
        var manifest = new P2B5ExportManifest(exportId, format, "UTF-8", FieldVersion, "P2B5_NETWORK_PLANNING",
            snapshot.MaxRevision, capturedAt, "SITE", request.SiteId, snapshot.Domains.Count, hash, "SUCCEEDED");
        var manifestJson = JsonSerializer.Serialize(manifest, JsonOptions);
        var relativePath = $"exports/p2b5/{exportId}.{format.ToLowerInvariant()}";
        WriteAtomic(relativePath, bytes);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO p2b5_exports
                    (id,actor,idempotency_key,request_fingerprint,format,status,snapshot_json,manifest_json,relative_path,content_sha256,row_count,created_ms,updated_ms,error_code)
                VALUES($id,$actor,$key,$fingerprint,$format,'SUCCEEDED',$snapshot,$manifest,$path,$hash,$rows,$now,$now,NULL)
                """;
            Add(command, "$id", exportId); Add(command, "$actor", actor.Username); Add(command, "$key", request.IdempotencyKey);
            Add(command, "$fingerprint", fingerprint); Add(command, "$format", format); Add(command, "$snapshot", snapshotJson);
            Add(command, "$manifest", manifestJson); Add(command, "$path", relativePath); Add(command, "$hash", hash);
            Add(command, "$rows", snapshot.Domains.Count); Add(command, "$now", now); command.ExecuteNonQuery();
        }
        catch
        {
            TryDelete(relativePath);
            throw;
        }
        return new(exportId, actor.Username, request.IdempotencyKey!, format, "SUCCEEDED", manifest, now, now, null);
    }

    public P2B5ExportStatus? Get(WebUserIdentity actor, string id)
    {
        EnsureAvailable();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,actor,idempotency_key,format,status,manifest_json,created_ms,updated_ms,error_code FROM p2b5_exports WHERE id=$id AND actor=$actor";
        Add(command, "$id", Required(id, "export id")); Add(command, "$actor", actor.Username);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadStatus(reader) : null;
    }

    public P2B5ExportDownload? OpenDownload(WebUserIdentity actor, string id)
    {
        EnsureAvailable();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT format,status,relative_path,content_sha256 FROM p2b5_exports WHERE id=$id AND actor=$actor";
        Add(command, "$id", Required(id, "export id")); Add(command, "$actor", actor.Username);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var format = reader.GetString(0);
        if (!string.Equals(reader.GetString(1), "SUCCEEDED", StringComparison.Ordinal) || reader.IsDBNull(2) || reader.IsDBNull(3)) return null;
        var relativePath = reader.GetString(2);
        var expectedHash = reader.GetString(3);
        var fullPath = ResolveExportPath(relativePath);
        if (!File.Exists(fullPath)) throw new P2B5ExportException("EXPORT_FILE_MISSING", "The export file is no longer available.", 409);
        var bytes = File.ReadAllBytes(fullPath);
        var observed = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(observed, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new P2B5ExportException("EXPORT_HASH_MISMATCH", "The export content hash no longer matches its manifest.", 409);
        return new(new MemoryStream(bytes, writable: false), $"netconsole-next-{id}.{format.ToLowerInvariant()}", ContentType(format));
    }

    private P2B5ExportSnapshot CaptureSnapshot(SqliteConnection connection, string siteId, string? domainId)
    {
        var allowed = new HashSet<string>(new[] { siteId }, StringComparer.Ordinal);
        var scope = new P2B5SiteScopeDecision("AUTHORIZED", false, allowed, "explicit isolated export scope");
        var parameters = new List<(string Name, object? Value)>();
        var scopeFilter = P2B5SiteScopeSql.ForDomain("d", scope, parameters, "export_scope_site_");
        var filters = $"d.lifecycle='ACTIVE' AND ($domain IS NULL OR d.id=$domain) AND {scopeFilter}";
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT d.id,d.name,d.domain_kind,d.source_kind,d.source_reference,d.revision,
                   COALESCE((SELECT group_concat(scope_reference,'|') FROM network_domain_scopes s
                     WHERE s.network_domain_id=d.id AND s.scope_kind='SITE' AND s.resolution='RESOLVED'),'')
            FROM network_domains d WHERE {filters} ORDER BY d.id LIMIT $take
            """;
        Add(command, "$domain", NullIfBlank(domainId)); Add(command, "$take", MaximumRows + 1); AddParameters(command, parameters);
        var rows = new List<P2B5ExportDomainRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (rows.Count >= MaximumRows) throw new P2B5ExportException("EXPORT_ROW_LIMIT", "The export exceeds the isolated row limit.", 413);
            rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetInt64(5), reader.GetString(6).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
        }
        var maxRevision = rows.Count == 0 ? 0 : rows.Max(row => row.Revision);
        return new("14", DateTimeOffset.UtcNow.ToString("O"), "SITE", siteId, domainId, rows, maxRevision);
    }

    private static string Render(string format, P2B5ExportSnapshot snapshot) => format switch
    {
        "JSON" => JsonSerializer.Serialize(snapshot, JsonOptions) + "\n",
        "CSV" => RenderCsv(snapshot),
        "HTML" => RenderHtml(snapshot),
        "TXT" => RenderText(snapshot),
        _ => throw new P2B5ExportException("EXPORT_FORMAT_INVALID", "The export format is unsupported.", 400)
    };

    private static string RenderCsv(P2B5ExportSnapshot snapshot)
    {
        var builder = new StringBuilder("domain_id,name,domain_kind,source_kind,source_reference,revision,site_references\n");
        foreach (var row in snapshot.Domains)
            builder.AppendJoin(',', new[] { row.Id, row.Name, row.DomainKind, row.SourceKind, row.SourceReference,
                row.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture), string.Join('|', row.SiteReferences) }.Select(CsvCell)).Append('\n');
        return builder.ToString();
    }

    private static string RenderHtml(P2B5ExportSnapshot snapshot)
    {
        var builder = new StringBuilder("<!doctype html><meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\"><title>NetConsole Next export</title><table><thead><tr><th>Domain</th><th>Name</th><th>Kind</th><th>Source</th><th>Revision</th><th>Sites</th></tr></thead><tbody>");
        foreach (var row in snapshot.Domains)
            builder.Append("<tr><td>").Append(Html(row.Id)).Append("</td><td>").Append(Html(row.Name)).Append("</td><td>")
                .Append(Html(row.DomainKind)).Append("</td><td>").Append(Html(row.SourceKind + ":" + row.SourceReference)).Append("</td><td>")
                .Append(row.Revision).Append("</td><td>").Append(Html(string.Join('|', row.SiteReferences))).Append("</td></tr>");
        return builder.Append("</tbody></table>\n").ToString();
    }

    private static string RenderText(P2B5ExportSnapshot snapshot) =>
        string.Join('\n', new[] { $"Schema: {snapshot.SchemaVersion}", $"Scope: {snapshot.ScopeKind}/{snapshot.ScopeReference}",
            $"Captured: {snapshot.CapturedAt}" }.Concat(snapshot.Domains.Select(row =>
            $"{row.Id}\t{row.Name}\t{row.DomainKind}\t{row.SourceKind}:{row.SourceReference}\trevision={row.Revision}\tsites={string.Join('|', row.SiteReferences)}"))) + "\n";

    private P2B5ExportStatus? FindByIdempotency(SqliteConnection connection, string actor, string key, string fingerprint)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,actor,idempotency_key,format,status,manifest_json,created_ms,updated_ms,error_code,request_fingerprint FROM p2b5_exports WHERE actor=$actor AND idempotency_key=$key";
        Add(command, "$actor", actor); Add(command, "$key", key);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (!string.Equals(reader.GetString(9), fingerprint, StringComparison.Ordinal))
            throw new P2B5ExportException("EXPORT_IDEMPOTENCY_CONFLICT", "The idempotency key was already used for a different export request.", 409);
        return ReadStatus(reader);
    }

    private static P2B5ExportStatus ReadStatus(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            JsonSerializer.Deserialize<P2B5ExportManifest>(reader.GetString(5), JsonOptions) ?? throw new InvalidDataException("Export manifest is invalid."),
            reader.GetInt64(6), reader.GetInt64(7), reader.IsDBNull(8) ? null : reader.GetString(8));

    private void EnsureAvailable()
    {
        if (!IsAuthorityAvailable()) throw new P2B5ExportException("EXPORT_UNAVAILABLE", "Export requires an isolated v14 authority and explicit test gate.", 503);
    }

    private static void ValidateRequest(P2B5ExportRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SiteId) || request.SiteId.Length > 256 || request.SiteId.Any(char.IsControl))
            throw new P2B5ExportException("EXPORT_SCOPE_INVALID", "An explicit bounded Site scope is required.", 400);
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 128 || request.IdempotencyKey.Any(char.IsControl))
            throw new P2B5ExportException("EXPORT_IDEMPOTENCY_REQUIRED", "A bounded idempotency key is required.", 400);
        if (request.DomainId is { } domain && (domain.Length > 256 || domain.Any(char.IsControl)))
            throw new P2B5ExportException("EXPORT_DOMAIN_INVALID", "The domain filter is invalid.", 400);
    }

    private static string NormalizeFormat(string format) => format.Trim().ToUpperInvariant() switch
    {
        "JSON" => "JSON", "CSV" => "CSV", "HTML" => "HTML", "TXT" => "TXT",
        _ => throw new P2B5ExportException("EXPORT_FORMAT_INVALID", "The export format must be JSON, CSV, HTML or TXT.", 400)
    };

    private void WriteAtomic(string relativePath, byte[] bytes)
    {
        var finalPath = ResolveExportPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        var part = finalPath + ".part";
        try { File.WriteAllBytes(part, bytes); File.Move(part, finalPath, true); }
        finally { TryDeleteAbsolute(part); }
    }

    private string ResolveExportPath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath) || relativePath.Contains("..", StringComparison.Ordinal))
            throw new P2B5ExportException("EXPORT_PATH_INVALID", "The export path is invalid.", 409);
        var root = Path.GetFullPath(Path.Combine(_settings.DataRoot, "exports", "p2b5"));
        var full = Path.GetFullPath(Path.Combine(_settings.DataRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new P2B5ExportException("EXPORT_PATH_INVALID", "The export path is outside the isolated export root.", 409);
        return full;
    }

    private void TryDelete(string relativePath) { try { TryDeleteAbsolute(ResolveExportPath(relativePath)); } catch { } }
    private static void TryDeleteAbsolute(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private SqliteConnection Open() { var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false, DefaultTimeout = 5 }.ToString()); connection.Open(); using var pragma = connection.CreateCommand(); pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;"; pragma.ExecuteNonQuery(); return connection; }
    private static void ValidateExportSchema(SqliteConnection connection) { using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='p2b5_exports'"; if (Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 1) throw new InvalidDataException("P2B5 export schema is missing."); }
    private static bool IsReservedRoot(string root) => string.Equals(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), Path.GetPathRoot(Path.GetFullPath(root))?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFullPath(root), Path.GetFullPath("D:\\NetConsoleData"), StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFullPath(root), Path.GetFullPath("D:\\NetConsoleData-dev"), StringComparison.OrdinalIgnoreCase);
    private static bool IsTemporaryRoot(string root) => Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string Required(string value, string label) => string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl) ? throw new P2B5ExportException("EXPORT_ID_INVALID", $"The {label} is invalid.", 400) : value;
    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    private static void AddParameters(SqliteCommand command, IEnumerable<(string Name, object? Value)> parameters) { foreach (var parameter in parameters) Add(command, parameter.Name, parameter.Value); }
    private static string CsvCell(string value) { var safe = value.Length > 512 ? value[..512] : value; if (safe.Length > 0 && safe[0] is '=' or '+' or '-' or '@') safe = "'" + safe; return "\"" + safe.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""; }
    private static string Html(string value) => value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal).Replace("'", "&#39;", StringComparison.Ordinal);
    private static string ContentType(string format) => format switch { "JSON" => "application/json; charset=utf-8", "CSV" => "text/csv; charset=utf-8", "HTML" => "text/html; charset=utf-8", _ => "text/plain; charset=utf-8" };
}

internal sealed record P2B5ExportMigrationContext(string DatabasePath, string DataRoot, bool IsolatedTest)
{
    public const string ExplicitMigrationEnvironmentVariable = "NETCONSOLE_NEXT_P2B5_EXPORT_MIGRATION_ALLOWED";
    public static P2B5ExportMigrationContext ForIsolatedTest(string databasePath, string dataRoot) => new(databasePath, dataRoot, true);
    public void Validate(SqliteConnection connection)
    {
        if (!IsolatedTest || !string.Equals(Environment.GetEnvironmentVariable(ExplicitMigrationEnvironmentVariable), "1", StringComparison.Ordinal)) throw new InvalidOperationException("P2B5 export migration requires the explicit isolated test gate.");
        var db = P2B2MigrationPathSecurity.CanonicalizeDatabasePath(DatabasePath, "migration context"); var root = P2B2MigrationPathSecurity.CanonicalizeDatabasePath(DataRoot, "data root"); var temp = P2B2MigrationPathSecurity.CanonicalizeDatabasePath(Path.GetTempPath(), "temporary root");
        P2B2MigrationPathSecurity.ValidateTemporaryDatabaseBoundary(db, temp); P2B2MigrationPathSecurity.ValidateTemporaryDatabaseBoundary(root, temp); P2B2MigrationPathSecurity.ValidateNoReparsePoints(temp); P2B2MigrationPathSecurity.ValidateNoReparsePoints(root); P2B2MigrationPathSecurity.ValidateNoReparsePoints(db);
        if (!P2B2MigrationPathSecurity.PathsEqual(P2B2MigrationPathSecurity.CanonicalizeDatabasePath(connection.DataSource, "open connection"), db)) throw new InvalidOperationException("P2B5 export migration connection path does not match.");
    }
}

internal static class P2B5ExportMigration
{
    public static void Apply(SqliteConnection connection, P2B5ExportMigrationContext context)
    {
        context.Validate(connection); using var version = connection.CreateCommand(); version.CommandText = "SELECT COALESCE(MAX(version),0) FROM schema_versions"; var schemaVersion = Convert.ToInt32(version.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture); if (schemaVersion != 14) throw new InvalidDataException($"P2B5 export migration requires Schema v14, found {schemaVersion}.");
        using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable); using var resource = typeof(P2B5ExportMigration).Assembly.GetManifestResourceStream("schema_p2b5_export.sql") ?? throw new InvalidOperationException("Embedded schema_p2b5_export.sql was not found."); using var reader = new StreamReader(resource); using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = reader.ReadToEnd(); command.ExecuteNonQuery(); transaction.Commit();
    }
}
