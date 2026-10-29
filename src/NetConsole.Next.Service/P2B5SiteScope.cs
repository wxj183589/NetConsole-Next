using Microsoft.Data.Sqlite;

namespace NetConsole.Next.Service;

internal static class P2B5SiteScopeHttpContextKeys
{
    public const string Decision = "NetConsole.Next.P2B5SiteScopeDecision";
}

internal sealed record P2B5SiteScopeDecision(
    string State,
    bool IsGlobal,
    IReadOnlySet<string> AllowedSiteReferences,
    string Detail)
{
    public bool IsAuthorized => IsGlobal || string.Equals(State, "AUTHORIZED", StringComparison.Ordinal);

    public static P2B5SiteScopeDecision Global { get; } = new(
        "GLOBAL", true, new HashSet<string>(StringComparer.Ordinal), "SUPERADMIN global scope.");

    public static P2B5SiteScopeDecision Unavailable(string detail) => new(
        "UNAVAILABLE", false, new HashSet<string>(StringComparer.Ordinal), detail);

    public static P2B5SiteScopeDecision Denied(string detail) => new(
        "DENIED", false, new HashSet<string>(StringComparer.Ordinal), detail);

    public static P2B5SiteScopeDecision Conflict(string detail) => new(
        "CONFLICT", false, new HashSet<string>(StringComparer.Ordinal), detail);
}

internal sealed class P2B5SiteScopeStore
{
    private readonly RuntimeSettings _settings;
    private string DatabasePath => Path.Combine(_settings.DataRoot, "catalog.db");

    public P2B5SiteScopeStore(RuntimeSettings settings) => _settings = settings;

    public P2B5SiteScopeDecision Resolve(WebUserIdentity identity)
    {
        if (string.Equals(identity.Role, "SUPERADMIN", StringComparison.OrdinalIgnoreCase))
            return P2B5SiteScopeDecision.Global;

        if (!File.Exists(DatabasePath))
            return P2B5SiteScopeDecision.Unavailable("The isolated catalog is unavailable.");

        try
        {
            using var connection = OpenReadOnly();
            if (!HasMembershipTable(connection))
                return P2B5SiteScopeDecision.Unavailable("No explicit Site membership authority is registered.");

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT site_reference,claim_state,source_kind,source_reference,revision,valid_from_ms,valid_to_ms,id
                FROM web_user_site_memberships
                WHERE user_id=$user AND lifecycle='ACTIVE'
                ORDER BY site_reference,source_kind,source_reference,revision DESC,id DESC
                """;
            command.Parameters.AddWithValue("$user", identity.UserId);
            var claims = new List<SiteMembershipClaim>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                claims.Add(new SiteMembershipClaim(
                    reader.GetString(0).Trim(),
                    reader.GetString(1).Trim().ToUpperInvariant(),
                    reader.IsDBNull(2) ? string.Empty : reader.GetString(2).Trim(),
                    reader.IsDBNull(3) ? string.Empty : reader.GetString(3).Trim(),
                    reader.GetInt64(4),
                    reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    reader.GetString(7)));
            }

            var allowed = new HashSet<string>(StringComparer.Ordinal);
            var conflict = false;
            foreach (var sourceClaims in claims.GroupBy(claim => new SiteMembershipSource(
                         claim.SiteReference, claim.SourceKind, claim.SourceReference)))
            {
                if (sourceClaims.Key.SiteReference.Length == 0 ||
                    sourceClaims.Key.SourceKind.Length == 0 ||
                    sourceClaims.Key.SourceReference.Length == 0)
                {
                    conflict = true;
                    continue;
                }

                // Revision is resolved before validity and claim state. A newer REVOKED
                // revision therefore cannot be resurrected by an older ACTIVE row.
                var effectiveRevision = sourceClaims.Max(claim => claim.Revision);
                var effectiveClaims = sourceClaims.Where(claim => claim.Revision == effectiveRevision).ToArray();
                var states = effectiveClaims.Select(claim => claim.State).Distinct(StringComparer.Ordinal).ToArray();
                if (states.Length != 1 || states[0] is not ("ACTIVE" or "REVOKED" or "CONFLICT"))
                {
                    conflict = true;
                    continue;
                }

                if (states[0] == "CONFLICT")
                {
                    conflict = true;
                    continue;
                }

                if (states[0] == "ACTIVE" && effectiveClaims.Any(claim => claim.IsValidAt(now)))
                    allowed.Add(sourceClaims.Key.SiteReference);
            }

            if (conflict)
                return P2B5SiteScopeDecision.Conflict("The current user has conflicting active Site claims.");
            if (allowed.Count == 0)
                return P2B5SiteScopeDecision.Denied("The current user has no active Site membership.");
            return new("AUTHORIZED", false, allowed, "Explicit Site membership claims are active.");
        }
        catch (SqliteException)
        {
            return P2B5SiteScopeDecision.Unavailable("The Site membership authority cannot be read safely.");
        }
        catch (IOException)
        {
            return P2B5SiteScopeDecision.Unavailable("The Site membership authority cannot be read safely.");
        }
        catch (UnauthorizedAccessException)
        {
            return P2B5SiteScopeDecision.Unavailable("The Site membership authority cannot be read safely.");
        }
    }

    public bool HasMembershipAuthority()
    {
        try
        {
            using var connection = OpenReadOnly();
            return HasMembershipTable(connection);
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private SqliteConnection OpenReadOnly()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = 5
        }.ToString());
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA query_only=ON; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static bool HasMembershipTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='web_user_site_memberships'";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private sealed record SiteMembershipClaim(
        string SiteReference,
        string State,
        string SourceKind,
        string SourceReference,
        long Revision,
        long? ValidFromMs,
        long? ValidToMs,
        string Id)
    {
        public bool IsValidAt(long now) =>
            (!ValidFromMs.HasValue || ValidFromMs.Value <= now) &&
            (!ValidToMs.HasValue || ValidToMs.Value > now);
    }

    private sealed record SiteMembershipSource(string SiteReference, string SourceKind, string SourceReference);
}

internal static class P2B5SiteScopeSql
{
    public static string ForDomain(string domainAlias, P2B5SiteScopeDecision decision,
        List<(string Name, object? Value)> parameters, string parameterPrefix)
    {
        if (decision.IsGlobal) return "1=1";
        if (!decision.IsAuthorized || decision.AllowedSiteReferences.Count == 0) return "1=0";

        var placeholders = new List<string>();
        var index = 0;
        foreach (var site in decision.AllowedSiteReferences.Order(StringComparer.Ordinal))
        {
            var name = $"${parameterPrefix}{index++}";
            placeholders.Add(name);
            parameters.Add((name, site));
        }

        var allowed = string.Join(',', placeholders);
        return $"""
            {domainAlias}.scope_resolution='RESOLVED'
            AND EXISTS (
                SELECT 1 FROM network_domain_scopes scope_has_site
                WHERE scope_has_site.network_domain_id={domainAlias}.id
                  AND scope_has_site.scope_kind='SITE'
                  AND scope_has_site.resolution='RESOLVED')
            AND NOT EXISTS (
                SELECT 1 FROM network_domain_scopes scope_denied_site
                WHERE scope_denied_site.network_domain_id={domainAlias}.id
                  AND scope_denied_site.scope_kind='SITE'
                  AND (scope_denied_site.resolution<>'RESOLVED'
                       OR scope_denied_site.scope_reference NOT IN ({allowed})))
            """;
    }

    public static string ForDomainId(string domainExpression, P2B5SiteScopeDecision decision,
        List<(string Name, object? Value)> parameters, string parameterPrefix)
    {
        if (decision.IsGlobal) return "1=1";
        return $"EXISTS (SELECT 1 FROM network_domains scope_domain WHERE scope_domain.id={domainExpression} AND {ForDomain("scope_domain", decision, parameters, parameterPrefix)})";
    }
}

internal sealed record P2B5SiteScopeMigrationContext(string DatabasePath, bool IsolatedTest)
{
    public const string ExplicitMigrationEnvironmentVariable = "NETCONSOLE_NEXT_P2B5_SITE_SCOPE_MIGRATION_ALLOWED";

    public static P2B5SiteScopeMigrationContext ForIsolatedTest(string databasePath) => new(databasePath, true);

    public void Validate(SqliteConnection connection)
    {
        if (!IsolatedTest)
            throw new InvalidOperationException("P2B5 Site Scope migration is not authorized outside an isolated test context.");
        if (!string.Equals(Environment.GetEnvironmentVariable(ExplicitMigrationEnvironmentVariable), "1", StringComparison.Ordinal))
            throw new InvalidOperationException($"Set {ExplicitMigrationEnvironmentVariable}=1 only for an isolated Site Scope migration test.");

        var databasePath = P2B2MigrationPathSecurity.CanonicalizeDatabasePath(DatabasePath, "migration context");
        var tempRoot = P2B2MigrationPathSecurity.CanonicalizeDatabasePath(Path.GetTempPath(), "temporary root");
        P2B2MigrationPathSecurity.ValidateTemporaryDatabaseBoundary(databasePath, tempRoot);
        P2B2MigrationPathSecurity.ValidateNoReparsePoints(tempRoot);
        P2B2MigrationPathSecurity.ValidateNoReparsePoints(databasePath);
        var connectionPath = P2B2MigrationPathSecurity.CanonicalizeDatabasePath(connection.DataSource, "open connection");
        if (!P2B2MigrationPathSecurity.PathsEqual(connectionPath, databasePath))
            throw new InvalidOperationException("Site Scope migration context database path does not match the open connection.");
    }
}

internal sealed record P2B5SiteScopeMigrationResult(bool AlreadyApplied, int BaseSchemaVersion);

internal static class P2B5SiteScopeMigration
{
    public static P2B5SiteScopeMigrationResult Apply(SqliteConnection connection, P2B5SiteScopeMigrationContext context)
    {
        context.Validate(connection);
        if (connection.State != System.Data.ConnectionState.Open)
            throw new InvalidOperationException("The Site Scope migration connection must already be open.");

        var version = ReadSchemaVersion(connection);
        if (version is not (13 or 14))
            throw new InvalidDataException($"P2B5 Site Scope migration requires Schema v13 or v14, found {version}.");
        if (HasMembershipTable(connection))
        {
            Validate(connection);
            return new(true, version);
        }

        using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
        try
        {
            using var resource = typeof(P2B5SiteScopeMigration).Assembly.GetManifestResourceStream("schema_p2b5_site_scope.sql")
                ?? throw new InvalidOperationException("Embedded schema_p2b5_site_scope.sql was not found.");
            using var reader = new StreamReader(resource);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = reader.ReadToEnd();
            command.ExecuteNonQuery();
            transaction.Commit();
            Validate(connection);
            return new(false, version);
        }
        catch
        {
            try { transaction.Rollback(); } catch { }
            throw;
        }
    }

    public static void Validate(SqliteConnection connection)
    {
        if (!HasMembershipTable(connection))
            throw new InvalidDataException("P2B5 Site Scope membership table is missing.");
        using var columns = connection.CreateCommand();
        columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('web_user_site_memberships') WHERE name IN ('user_id','site_reference','claim_state','valid_from_ms','valid_to_ms')";
        if (Convert.ToInt32(columns.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 5)
            throw new InvalidDataException("P2B5 Site Scope membership columns are incomplete.");
    }

    private static bool HasMembershipTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='web_user_site_memberships'";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static int ReadSchemaVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(CASE WHEN version <= 14 THEN version END),0) FROM schema_versions";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
