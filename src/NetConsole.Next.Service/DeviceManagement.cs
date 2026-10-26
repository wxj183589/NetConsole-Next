using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace NetConsole.Next.Service;

internal sealed record DeviceInventoryWriteRequest(
    string? Id,
    string Name,
    string ManagementIp,
    string? Vendor,
    string? Model,
    string? DeviceType,
    string SiteId,
    string? GroupId,
    string? Description,
    bool Enabled,
    string? TerminalProfileId);

internal sealed record DeviceGroupView(string Id, string SiteId, string Name, string Description,
    long CreatedMs, long UpdatedMs, long DeviceCount);

internal sealed record DeviceAuditView(string Id, long AtMs, string Action, string Outcome, string DetailJson);

internal sealed record DeviceInventoryDetail(
    DeviceInventoryItem Item,
    string Description,
    long CreatedMs,
    long UpdatedMs,
    string? TerminalProfileId,
    H3cDeviceProfileSnapshot? TerminalProfile,
    IReadOnlyList<TerminalSessionSnapshot> RecentSessions,
    IReadOnlyList<DeviceAuditView> AuditEvents);

internal sealed record DeviceImportPreviewRequest(string Csv);
internal sealed record DeviceImportCommitRequest(string PreviewId, bool Confirm);
internal sealed record DeviceImportRowView(int Line, string Action, string DeviceId, string Name,
    string ManagementIp, string Detail, bool CanCommit);
internal sealed record DeviceImportPreviewResult(string PreviewId, long ExpiresAtMs, int TotalRows,
    int CreateCount, int UpdateCount, int ConflictCount, int ErrorCount, IReadOnlyList<DeviceImportRowView> Rows);
internal sealed record DeviceImportCommitResult(string PreviewId, int Created, int Updated, long CommittedAtMs);

internal sealed class DeviceImportResourceLimitException(string message) : InvalidOperationException(message);

internal sealed record DeviceImportCandidate(int Line, string Action, string DeviceId, string Name,
    string ManagementIp, string Vendor, string Model, string DeviceType, string SiteId, string? GroupId,
    string Description, bool Enabled, string? TerminalProfileId, long? ExpectedUpdatedMs, string Detail);

internal sealed class DeviceImportPreviewState(DeviceImportPreviewResult result, IReadOnlyList<DeviceImportCandidate> candidates, string actor)
{
    public DeviceImportPreviewResult Result { get; } = result;
    public IReadOnlyList<DeviceImportCandidate> Candidates { get; } = candidates;
    public string Actor { get; } = actor;
    public bool Committing { get; set; }
}

internal sealed record DeviceImportCommitReceipt(string Actor, DeviceImportCommitResult Result, long ExpiresAtMs);

internal sealed partial class Catalog
{
    private const int MaximumDeviceImportBytes = 2 * 1024 * 1024;
    private const int MaximumDeviceImportRows = 10_000;
    private const int MaximumDeviceImportCellCharacters = 4_096;
    private const int MaximumDeviceImportPreviews = 32;
    private const int MaximumDeviceImportPreviewsPerActor = 8;
    private const int MaximumDeviceImportReceipts = 64;
    private static readonly TimeSpan DeviceImportLifetime = TimeSpan.FromMinutes(10);
    private readonly object _deviceImportGate = new();
    private readonly Dictionary<string, (DeviceImportPreviewState State, long ExpiresAtMs)> _deviceImportPreviews = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DeviceImportCommitReceipt> _deviceImportReceipts = new(StringComparer.Ordinal);

    public DeviceInventoryPage QueryManagedDeviceInventory(int page = 1, int pageSize = 20,
        string? search = null, string? status = null, string? siteId = null, string? groupId = null)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var filters = InventoryFilters(search, status, siteId, groupId);
        var where = filters.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", filters);
        using var db = Open();

        using var summary = db.CreateCommand();
        summary.CommandText = $@"WITH latest AS (
                SELECT target_id,state,ROW_NUMBER() OVER(PARTITION BY target_id ORDER BY started_ms DESC,id DESC) AS rn
                FROM terminal_sessions
            )
            SELECT COUNT(*),
                COALESCE(SUM(CASE WHEN TRIM(d.id)<>'' AND TRIM(d.name)<>'' AND TRIM(d.management_ip)<>'' AND TRIM(d.site_id)<>'' THEN 1 ELSE 0 END),0),
                COALESCE(SUM(CASE WHEN TRIM(d.id)='' OR TRIM(d.name)='' OR TRIM(d.management_ip)='' OR TRIM(d.site_id)='' THEN 1 ELSE 0 END),0),
                COALESCE(SUM(CASE WHEN d.enabled=1 THEN 1 ELSE 0 END),0),
                COALESCE(SUM(CASE WHEN d.enabled=0 THEN 1 ELSE 0 END),0),
                COALESCE(SUM(CASE WHEN d.enabled=1 AND s.state='CONNECTED' THEN 1 ELSE 0 END),0),
                COALESCE(SUM(CASE WHEN d.enabled=1 AND s.state IN ('DISCONNECTED','FAILED','CLOSED') THEN 1 ELSE 0 END),0),
                COALESCE(SUM(CASE WHEN d.enabled=1 AND (s.state IS NULL OR s.state NOT IN ('CONNECTED','DISCONNECTED','FAILED','CLOSED')) THEN 1 ELSE 0 END),0)
            FROM device_inventory d
            LEFT JOIN sites st ON st.id=d.site_id
            LEFT JOIN device_groups g ON g.id=d.group_id
            LEFT JOIN h3c_device_profiles p ON p.id=d.terminal_profile_id
            LEFT JOIN latest s ON s.target_id=p.id AND s.rn=1
            {where}";
        AddInventoryParameters(summary, search, status, siteId, groupId);
        using var summaryReader = summary.ExecuteReader();
        summaryReader.Read();
        var total = summaryReader.GetInt64(0);
        var counts = new DeviceInventorySummary(total, summaryReader.GetInt64(1), summaryReader.GetInt64(2),
            summaryReader.GetInt64(3), summaryReader.GetInt64(4), summaryReader.GetInt64(5),
            summaryReader.GetInt64(6), summaryReader.GetInt64(7));

        using var command = db.CreateCommand();
        command.CommandText = $@"WITH latest AS (
                SELECT target_id,state,started_ms,disconnect_reason,
                    ROW_NUMBER() OVER(PARTITION BY target_id ORDER BY started_ms DESC,id DESC) AS rn
                FROM terminal_sessions
            )
            SELECT d.id,d.name,d.management_ip,d.vendor,d.model,d.device_type,d.site_id,d.group_id,
                COALESCE(NULLIF(st.name,''),'—'),COALESCE(NULLIF(g.name,''),'—'),d.enabled,d.created_ms,d.updated_ms,
                p.id,p.host,p.port,p.username,p.credential_reference,p.host_key_update_policy,p.terminal_encoding,p.terminal_protocol,
                s.state,s.started_ms,s.disconnect_reason,d.description
            FROM device_inventory d
            LEFT JOIN sites st ON st.id=d.site_id
            LEFT JOIN device_groups g ON g.id=d.group_id
            LEFT JOIN h3c_device_profiles p ON p.id=d.terminal_profile_id
            LEFT JOIN latest s ON s.target_id=p.id AND s.rn=1
            {where}
            ORDER BY d.updated_ms DESC,d.id ASC
            LIMIT $limit OFFSET $offset";
        AddInventoryParameters(command, search, status, siteId, groupId);
        command.Parameters.AddWithValue("$limit", pageSize);
        command.Parameters.AddWithValue("$offset", (page - 1) * pageSize);
        using var reader = command.ExecuteReader();
        var items = new List<DeviceInventoryItem>();
        while (reader.Read()) items.Add(ReadManagedInventoryItem(reader));
        return new DeviceInventoryPage(page, pageSize, total, (long)page * pageSize < total, counts, items);
    }

    public DeviceInventoryDetail? GetDeviceInventoryDetail(string id)
    {
        var page = QueryManagedDeviceInventory(1, 100, id, null, null, null);
        var item = page.Items.FirstOrDefault(candidate => candidate.Id == id);
        if (item is null) return null;
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT description,created_ms,updated_ms,terminal_profile_id FROM device_inventory WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var description = reader.GetString(0);
        var created = reader.GetInt64(1);
        var updated = reader.GetInt64(2);
        var profileId = reader.IsDBNull(3) ? null : reader.GetString(3);
        reader.Close();
        var profile = profileId is null ? null : GetH3cDeviceProfile(profileId);
        var sessions = profileId is null ? Array.Empty<TerminalSessionSnapshot>() : RecentTerminalSessions(profileId, 20);
        var audits = ReadDeviceAudits(db, id, 50);
        return new DeviceInventoryDetail(item, description, created, updated, profileId, profile, sessions, audits);
    }

    public IReadOnlyList<DeviceGroupView> QueryDeviceGroups(string? siteId = null)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = @"SELECT g.id,g.site_id,g.name,g.description,g.created_ms,g.updated_ms,COUNT(d.id)
            FROM device_groups g LEFT JOIN device_inventory d ON d.group_id=g.id
            WHERE ($site IS NULL OR g.site_id=$site)
            GROUP BY g.id,g.site_id,g.name,g.description,g.created_ms,g.updated_ms
            ORDER BY g.site_id,g.name";
        command.Parameters.AddWithValue("$site", (object?)NullIfBlank(siteId) ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        var result = new List<DeviceGroupView>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6)));
        return result;
    }

    public DeviceGroupView CreateDeviceGroup(string siteId, string name, string? description, string actor)
    {
        siteId = RequiredText(siteId, "siteId", 80);
        name = RequiredText(name, "name", 100);
        description = (description ?? string.Empty).Trim();
        if (description.Length > 500) throw new ArgumentException("Group description is too long.", nameof(description));
        var now = UtcNowMs();
        var id = "grp-" + Guid.NewGuid().ToString("N");
        using var db = Open();
        using var transaction = db.BeginTransaction(System.Data.IsolationLevel.Serializable);
        EnsureSite(db, transaction, siteId);
        using (var insert = db.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO device_groups(id,site_id,name,description,created_ms,updated_ms) VALUES($id,$site,$name,$description,$created,$updated)";
            insert.Parameters.AddWithValue("$id", id); insert.Parameters.AddWithValue("$site", siteId);
            insert.Parameters.AddWithValue("$name", name); insert.Parameters.AddWithValue("$description", description);
            insert.Parameters.AddWithValue("$created", now); insert.Parameters.AddWithValue("$updated", now);
            try { insert.ExecuteNonQuery(); } catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { throw new InvalidOperationException("A group with this name already exists at the site.", ex); }
        }
        InsertDeviceAudit(db, transaction, actor, "DEVICE_GROUP_CREATE", id, new { siteId, name });
        transaction.Commit();
        return QueryDeviceGroups(siteId).Single(group => group.Id == id);
    }

    public DeviceInventoryDetail CreateDeviceInventory(DeviceInventoryWriteRequest request, string actor)
    {
        var normalized = NormalizeDeviceInput(request, null);
        var now = UtcNowMs();
        using var db = Open();
        using var transaction = db.BeginTransaction(System.Data.IsolationLevel.Serializable);
        EnsureSite(db, transaction, normalized.SiteId);
        EnsureGroup(db, transaction, normalized.GroupId, normalized.SiteId);
        EnsureTerminalProfile(db, transaction, normalized.TerminalProfileId, normalized.Id);
        EnsureUniqueManagementIp(db, transaction, normalized.SiteId, normalized.ManagementIp, normalized.Id);
        using (var insert = db.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = @"INSERT INTO device_inventory
                (id,name,management_ip,vendor,model,device_type,site_id,group_id,description,enabled,created_ms,updated_ms,terminal_profile_id)
                VALUES($id,$name,$ip,$vendor,$model,$type,$site,$group,$description,$enabled,$created,$updated,$profile)";
            AddDeviceParameters(insert, normalized, now, now);
            insert.ExecuteNonQuery();
        }
        InsertDeviceAudit(db, transaction, actor, "DEVICE_CREATE", normalized.Id!, new { normalized.Name, normalized.ManagementIp, normalized.SiteId, normalized.GroupId, normalized.Enabled, normalized.TerminalProfileId });
        transaction.Commit();
        return GetDeviceInventoryDetail(normalized.Id!) ?? throw new InvalidOperationException("Created device could not be reloaded.");
    }

    public DeviceInventoryDetail UpdateDeviceInventory(string id, DeviceInventoryWriteRequest request, string actor)
    {
        var normalized = NormalizeDeviceInput(request with { Id = id }, id);
        var now = UtcNowMs();
        using var db = Open();
        using var transaction = db.BeginTransaction(System.Data.IsolationLevel.Serializable);
        EnsureSite(db, transaction, normalized.SiteId);
        EnsureGroup(db, transaction, normalized.GroupId, normalized.SiteId);
        EnsureTerminalProfile(db, transaction, normalized.TerminalProfileId, id);
        EnsureUniqueManagementIp(db, transaction, normalized.SiteId, normalized.ManagementIp, id);
        using (var update = db.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = @"UPDATE device_inventory SET name=$name,management_ip=$ip,vendor=$vendor,model=$model,
                device_type=$type,site_id=$site,group_id=$group,description=$description,enabled=$enabled,
                updated_ms=CASE WHEN $updated<=updated_ms THEN updated_ms+1 ELSE $updated END,terminal_profile_id=$profile WHERE id=$id";
            AddDeviceParameters(update, normalized, now, now);
            if (update.ExecuteNonQuery() != 1) throw new KeyNotFoundException("Device inventory record was not found.");
        }
        InsertDeviceAudit(db, transaction, actor, normalized.Enabled ? "DEVICE_UPDATE" : "DEVICE_DISABLE", id,
            new { normalized.Name, normalized.ManagementIp, normalized.SiteId, normalized.GroupId, normalized.Enabled, normalized.TerminalProfileId });
        transaction.Commit();
        return GetDeviceInventoryDetail(id) ?? throw new InvalidOperationException("Updated device could not be reloaded.");
    }

    public DeviceImportPreviewResult PreviewDeviceImport(string csv, string actor)
    {
        if (string.IsNullOrWhiteSpace(csv)) throw new ArgumentException("CSV content is required.", nameof(csv));
        actor = RequiredText(actor, "actor", 128);
        if (Encoding.UTF8.GetByteCount(csv) > MaximumDeviceImportBytes) throw new DeviceImportResourceLimitException("CSV content exceeds 2 MiB.");
        var parsed = ParseCsv(csv);
        if (parsed.Rows.Count > MaximumDeviceImportRows)
            throw new DeviceImportResourceLimitException($"CSV contains more than {MaximumDeviceImportRows} data rows.");
        var candidates = new List<DeviceImportCandidate>();
        var views = new List<DeviceImportRowView>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenProfiles = new HashSet<string>(StringComparer.Ordinal);
        using var db = Open();
        var existing = ReadInventoryImportState(db);
        foreach (var row in parsed.Rows)
        {
            var errors = new List<string>();
            var name = Text(row.Get("name"));
            var ipText = Text(row.Get("managementIp"));
            var site = Text(row.Get("siteId"));
            if (site.Length == 0) site = "local";
            var normalizedIp = TryNormalizeIp(ipText, out var ip) ? ip : string.Empty;
            if (name.Length is < 1 or > 120 || name.Any(char.IsControl)) errors.Add("NAME_REQUIRED_OR_INVALID");
            if (normalizedIp.Length == 0) errors.Add("MANAGEMENT_IP_INVALID");
            if (!SiteExists(db, site)) errors.Add("SITE_NOT_FOUND");
            var id = Text(row.Get("deviceId"));
            if (id.Length > 0 && !IsValidDeviceId(id)) errors.Add("DEVICE_ID_INVALID");
            if (id.Length > 0 && !seenIds.Add(id)) errors.Add("DUPLICATE_IMPORT_DEVICE_ID");
            var type = Text(row.Get("deviceType"));
            if (type.Length == 0) type = "NETWORK_DEVICE";
            if (!IsValidToken(type, 48)) errors.Add("DEVICE_TYPE_INVALID");
            var groupName = Text(row.Get("groupName"));
            if (groupName.Length > 100 || groupName.Any(char.IsControl)) errors.Add("GROUP_NAME_INVALID");
            var groupId = ResolveGroupByName(db, site, groupName, errors);
            var profileId = Text(row.Get("terminalProfileId"));
            if (profileId.Length > 0 && !TerminalProfileExists(db, profileId)) errors.Add("TERMINAL_PROFILE_NOT_FOUND");
            if (profileId.Length > 0 && !IsValidDeviceId(profileId)) errors.Add("TERMINAL_PROFILE_ID_INVALID");
            if (profileId.Length > 0 && !seenProfiles.Add(profileId)) errors.Add("DUPLICATE_IMPORT_TERMINAL_PROFILE");
            if (!TryParseEnabled(Text(row.Get("enabled")), out var enabled)) errors.Add("ENABLED_INVALID");
            var vendor = Text(row.Get("vendor"));
            var model = Text(row.Get("model"));
            var description = Text(row.Get("description"));
            if (vendor.Length > 80 || vendor.Any(char.IsControl)) errors.Add("VENDOR_INVALID");
            if (model.Length > 80 || model.Any(char.IsControl)) errors.Add("MODEL_INVALID");
            if (description.Length > 500 || description.Any(char.IsControl)) errors.Add("DESCRIPTION_INVALID");
            if (row.Values.Any(item => IsCsvFormulaValue(item.Value))) errors.Add("CSV_FORMULA_VALUE");
            if (normalizedIp.Length > 0)
            {
                var key = site + "\n" + normalizedIp;
                if (!seenKeys.Add(key)) errors.Add("DUPLICATE_IMPORT_ADDRESS");
            }
            var byId = id.Length == 0 ? null : existing.FirstOrDefault(item => item.Id == id);
            var byIp = normalizedIp.Length == 0 ? null : existing.FirstOrDefault(item => item.SiteId == site && item.ManagementIp == normalizedIp);
            if ((byId is not null && byIp is not null && byId.Id != byIp.Id) ||
                (id.Length > 0 && byIp is not null && byIp.Id != id)) errors.Add("ID_IP_TARGET_CONFLICT");
            var target = byId ?? byIp;
            if (target is not null && profileId.Length > 0 && existing.Any(item => item.TerminalProfileId == profileId && item.Id != target.Id)) errors.Add("TERMINAL_PROFILE_ALREADY_LINKED");
            var action = target is null ? "CREATE" : "UPDATE";
            if (errors.Count == 0 && normalizedIp.Length > 0)
            {
                candidates.Add(new DeviceImportCandidate(row.Line, action, target?.Id ?? (id.Length == 0 ? "dev-" + Guid.NewGuid().ToString("N") : id),
                    name, normalizedIp, vendor, model, type, site, groupId,
                    description, enabled, profileId.Length == 0 ? null : profileId, target?.UpdatedMs,
                    action == "CREATE" ? "New inventory record" : "Existing management IP will be updated"));
            }
            var detail = errors.Count == 0 ? (action == "CREATE" ? "New device" : "Existing device matched by ID/IP") : string.Join(",", errors);
            views.Add(new DeviceImportRowView(row.Line, errors.Count == 0 ? action : "CONFLICT", target?.Id ?? id, name, ipText, detail, errors.Count == 0));
        }
        var previewId = Guid.NewGuid().ToString("N");
        var result = new DeviceImportPreviewResult(previewId, UtcNowMs() + (long)DeviceImportLifetime.TotalMilliseconds, views.Count,
            views.Count(item => item.Action == "CREATE"), views.Count(item => item.Action == "UPDATE"),
            views.Count(item => item.Action == "CONFLICT"), views.Count(item => !item.CanCommit), views);
        lock (_deviceImportGate)
        {
            var now = UtcNowMs();
            CleanupImportStateLocked(now);
            if (_deviceImportPreviews.Count >= MaximumDeviceImportPreviews ||
                _deviceImportPreviews.Values.Count(item => item.State.Actor == actor) >= MaximumDeviceImportPreviewsPerActor)
                throw new DeviceImportResourceLimitException("Too many active device import previews; commit or wait for an existing preview to expire.");
            _deviceImportPreviews[previewId] = (new DeviceImportPreviewState(result, candidates, actor), result.ExpiresAtMs);
        }
        return result;
    }

    public DeviceImportCommitResult CommitDeviceImport(string previewId, bool confirm, string actor)
    {
        if (!confirm) throw new InvalidOperationException("Import commit requires explicit confirmation.");
        actor = RequiredText(actor, "actor", 128);
        DeviceImportPreviewState state;
        lock (_deviceImportGate)
        {
            var checkedAtMs = UtcNowMs();
            CleanupImportStateLocked(checkedAtMs);
            if (_deviceImportReceipts.TryGetValue(previewId, out var receipt))
            {
                if (!string.Equals(receipt.Actor, actor, StringComparison.Ordinal)) throw new UnauthorizedAccessException("Import preview belongs to another administrator.");
                return receipt.Result;
            }
            if (!_deviceImportPreviews.TryGetValue(previewId, out var stored) || stored.ExpiresAtMs <= checkedAtMs)
                throw new KeyNotFoundException("Import preview is missing or expired.");
            state = stored.State;
            if (!string.Equals(state.Actor, actor, StringComparison.Ordinal)) throw new UnauthorizedAccessException("Import preview belongs to another administrator.");
            if (state.Committing) throw new InvalidOperationException("Import preview is already being committed.");
            if (state.Result.ConflictCount > 0 || state.Result.ErrorCount > 0)
                throw new InvalidOperationException("Resolve all import conflicts before committing.");
            state.Committing = true;
        }
        var created = 0; var updated = 0; var now = UtcNowMs();
        try
        {
            using var db = Open();
            using var transaction = db.BeginTransaction(System.Data.IsolationLevel.Serializable);
            foreach (var candidate in state.Candidates)
            {
                EnsureSite(db, transaction, candidate.SiteId);
                EnsureGroup(db, transaction, candidate.GroupId, candidate.SiteId);
                EnsureTerminalProfile(db, transaction, candidate.TerminalProfileId, candidate.DeviceId);
                EnsureUniqueManagementIp(db, transaction, candidate.SiteId, candidate.ManagementIp, candidate.Action == "UPDATE" ? candidate.DeviceId : null);
                if (candidate.Action == "CREATE")
                {
                    EnsureDeviceIdAvailable(db, transaction, candidate.DeviceId);
                    InsertDeviceCandidate(db, transaction, candidate, now);
                    created++;
                }
                else
                {
                    UpdateDeviceCandidate(db, transaction, candidate, now);
                    updated++;
                }
                InsertDeviceAudit(db, transaction, actor, "DEVICE_IMPORT_" + candidate.Action, candidate.DeviceId,
                    new { candidate.Name, candidate.ManagementIp, candidate.SiteId, candidate.GroupId, candidate.Enabled, candidate.TerminalProfileId });
            }
            InsertDeviceAudit(db, transaction, actor, "DEVICE_IMPORT_COMMIT", previewId, new { created, updated });
            transaction.Commit();
        }
        catch
        {
            lock (_deviceImportGate)
            {
                if (_deviceImportPreviews.TryGetValue(previewId, out var stored) && ReferenceEquals(stored.State, state))
                    state.Committing = false;
            }
            throw;
        }
        var result = new DeviceImportCommitResult(previewId, created, updated, now);
        lock (_deviceImportGate)
        {
            _deviceImportPreviews.Remove(previewId);
            CleanupImportStateLocked(now);
            _deviceImportReceipts[previewId] = new DeviceImportCommitReceipt(actor, result, now + (long)DeviceImportLifetime.TotalMilliseconds);
            while (_deviceImportReceipts.Count > MaximumDeviceImportReceipts)
            {
                var oldest = _deviceImportReceipts.OrderBy(item => item.Value.ExpiresAtMs).First().Key;
                _deviceImportReceipts.Remove(oldest);
            }
        }
        return result;
    }

    private static List<string> InventoryFilters(string? search, string? status, string? siteId, string? groupId)
    {
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(search)) filters.Add("(d.id LIKE $search OR d.name LIKE $search OR d.management_ip LIKE $search OR d.vendor LIKE $search OR d.model LIKE $search OR st.name LIKE $search)");
        if (!string.IsNullOrWhiteSpace(siteId)) filters.Add("d.site_id=$site");
        if (!string.IsNullOrWhiteSpace(groupId)) filters.Add("d.group_id=$group");
        switch (status?.Trim().ToUpperInvariant())
        {
            case "ENABLED": filters.Add("d.enabled=1"); break;
            case "DISABLED": filters.Add("d.enabled=0"); break;
            case "ONLINE": filters.Add("d.enabled=1 AND s.state='CONNECTED'"); break;
            case "OFFLINE": filters.Add("d.enabled=1 AND s.state IN ('DISCONNECTED','FAILED','CLOSED')"); break;
            case "UNKNOWN": filters.Add("d.enabled=1 AND (s.state IS NULL OR s.state NOT IN ('CONNECTED','DISCONNECTED','FAILED','CLOSED'))"); break;
        }
        return filters;
    }

    private static void AddInventoryParameters(SqliteCommand command, string? search, string? status, string? siteId, string? groupId)
    {
        if (!string.IsNullOrWhiteSpace(search)) command.Parameters.AddWithValue("$search", $"%{search.Trim()}%");
        if (!string.IsNullOrWhiteSpace(siteId)) command.Parameters.AddWithValue("$site", siteId.Trim());
        if (!string.IsNullOrWhiteSpace(groupId)) command.Parameters.AddWithValue("$group", groupId.Trim());
    }

    private static DeviceInventoryItem ReadManagedInventoryItem(SqliteDataReader reader)
    {
        var id = reader.GetString(0); var name = reader.GetString(1); var ip = reader.GetString(2);
        var type = reader.GetString(5); var enabled = reader.GetInt32(10) != 0;
        var profileId = reader.IsDBNull(13) ? null : reader.GetString(13);
        var profileHost = reader.IsDBNull(14) ? null : reader.GetString(14);
        var port = reader.IsDBNull(15) ? 0 : reader.GetInt32(15);
        var username = reader.IsDBNull(16) ? "—" : reader.GetString(16);
        var credential = reader.IsDBNull(17) ? string.Empty : reader.GetString(17);
        var policy = reader.IsDBNull(18) ? "—" : reader.GetString(18);
        var encoding = reader.IsDBNull(19) ? "—" : reader.GetString(19);
        var protocol = reader.IsDBNull(20) ? "—" : reader.GetString(20);
        var issues = new List<string>();
        if (string.IsNullOrWhiteSpace(id)) issues.Add("MISSING_ID");
        if (string.IsNullOrWhiteSpace(name)) issues.Add("MISSING_NAME");
        if (string.IsNullOrWhiteSpace(ip)) issues.Add("MISSING_ADDRESS");
        if (string.IsNullOrWhiteSpace(reader.GetString(6))) issues.Add("MISSING_SITE");
        if (profileId is not null && !string.Equals(profileHost, ip, StringComparison.OrdinalIgnoreCase)) issues.Add("TERMINAL_PROFILE_ADDRESS_DIFFERS");
        var state = reader.IsDBNull(21) ? "UNKNOWN" : reader.GetString(21);
        if (!enabled) state = "DISABLED";
        return new DeviceInventoryItem(id, string.IsNullOrWhiteSpace(name) ? "未命名设备" : name,
            string.IsNullOrWhiteSpace(ip) ? "—" : ip, port, string.IsNullOrWhiteSpace(username) ? "—" : username,
            string.IsNullOrWhiteSpace(reader.GetString(3)) ? "—" : reader.GetString(3),
            string.IsNullOrWhiteSpace(reader.GetString(4)) ? "—" : reader.GetString(4),
            string.IsNullOrWhiteSpace(type) ? "NETWORK_DEVICE" : type,
            string.IsNullOrWhiteSpace(protocol) ? "—" : protocol,
            reader.GetString(8), reader.GetString(9), enabled, state,
            reader.IsDBNull(22) ? null : reader.GetInt64(22), !string.IsNullOrWhiteSpace(credential),
            string.IsNullOrWhiteSpace(policy) ? "—" : policy, string.IsNullOrWhiteSpace(encoding) ? "—" : encoding,
            reader.IsDBNull(23) ? null : reader.GetString(23), issues.Count == 0, issues,
            id, ip, reader.GetString(6), reader.IsDBNull(7) ? "" : reader.GetString(7), reader.GetString(24),
            reader.GetInt64(11), reader.GetInt64(12), profileId);
    }

    private static void AddDeviceParameters(SqliteCommand command, DeviceInventoryWriteRequest request, long created, long updated)
    {
        command.Parameters.AddWithValue("$id", request.Id!); command.Parameters.AddWithValue("$name", request.Name);
        command.Parameters.AddWithValue("$ip", request.ManagementIp); command.Parameters.AddWithValue("$vendor", request.Vendor ?? string.Empty);
        command.Parameters.AddWithValue("$model", request.Model ?? string.Empty); command.Parameters.AddWithValue("$type", request.DeviceType ?? "NETWORK_DEVICE");
        command.Parameters.AddWithValue("$site", request.SiteId); command.Parameters.AddWithValue("$group", (object?)request.GroupId ?? DBNull.Value);
        command.Parameters.AddWithValue("$description", request.Description ?? string.Empty); command.Parameters.AddWithValue("$enabled", request.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$created", created); command.Parameters.AddWithValue("$updated", updated);
        command.Parameters.AddWithValue("$profile", (object?)request.TerminalProfileId ?? DBNull.Value);
    }

    private static DeviceInventoryWriteRequest NormalizeDeviceInput(DeviceInventoryWriteRequest request, string? existingId)
    {
        var id = string.IsNullOrWhiteSpace(request.Id) ? "dev-" + Guid.NewGuid().ToString("N") : request.Id.Trim();
        if (!IsValidDeviceId(id)) throw new ArgumentException("Device id must contain only letters, numbers, dot, underscore or hyphen.", nameof(request));
        var name = RequiredText(request.Name, "name", 120);
        if (!TryNormalizeIp(request.ManagementIp, out var ip)) throw new ArgumentException("managementIp must be a valid IP address.", nameof(request));
        var type = string.IsNullOrWhiteSpace(request.DeviceType) ? "NETWORK_DEVICE" : request.DeviceType.Trim().ToUpperInvariant();
        if (!IsValidToken(type, 48)) throw new ArgumentException("deviceType contains unsupported characters.", nameof(request));
        var site = RequiredText(request.SiteId, "siteId", 80);
        return new DeviceInventoryWriteRequest(id, name, ip, Clean(request.Vendor, 80), Clean(request.Model, 80), type, site,
            CleanNullable(request.GroupId, 80), Clean(request.Description, 500), request.Enabled, CleanNullable(request.TerminalProfileId, 80));
    }

    private static void EnsureSite(SqliteConnection db, SqliteTransaction transaction, string siteId)
    {
        using var command = db.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT 1 FROM sites WHERE id=$id"; command.Parameters.AddWithValue("$id", siteId);
        if (command.ExecuteScalar() is null) throw new KeyNotFoundException("Site was not found.");
    }

    private static void EnsureGroup(SqliteConnection db, SqliteTransaction transaction, string? groupId, string siteId)
    {
        if (groupId is null) return;
        using var command = db.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT 1 FROM device_groups WHERE id=$id AND site_id=$site";
        command.Parameters.AddWithValue("$id", groupId); command.Parameters.AddWithValue("$site", siteId);
        if (command.ExecuteScalar() is null) throw new KeyNotFoundException("Device group was not found at the selected site.");
    }

    private static void EnsureTerminalProfile(SqliteConnection db, SqliteTransaction transaction, string? profileId, string? excludeDeviceId)
    {
        if (profileId is null) return;
        using var command = db.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT 1 FROM h3c_device_profiles WHERE id=$id"; command.Parameters.AddWithValue("$id", profileId);
        if (command.ExecuteScalar() is null) throw new KeyNotFoundException("Terminal profile was not found.");
        using var linked = db.CreateCommand(); linked.Transaction = transaction;
        linked.CommandText = "SELECT id FROM device_inventory WHERE terminal_profile_id=$profile AND ($exclude IS NULL OR id<>$exclude) LIMIT 1";
        linked.Parameters.AddWithValue("$profile", profileId); linked.Parameters.AddWithValue("$exclude", (object?)excludeDeviceId ?? DBNull.Value);
        if (linked.ExecuteScalar() is string otherDevice)
            throw new InvalidOperationException($"Terminal profile is already linked to device {otherDevice}.");
    }

    private static void EnsureDeviceIdAvailable(SqliteConnection db, SqliteTransaction transaction, string deviceId)
    {
        using var command = db.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT 1 FROM device_inventory WHERE id=$id";
        command.Parameters.AddWithValue("$id", deviceId);
        if (command.ExecuteScalar() is not null) throw new InvalidOperationException($"Device id {deviceId} is already assigned.");
    }

    private static void EnsureUniqueManagementIp(SqliteConnection db, SqliteTransaction transaction, string siteId, string ip, string? excludeId)
    {
        using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT id FROM device_inventory WHERE site_id=$site AND management_ip=$ip AND ($exclude IS NULL OR id<>$exclude) LIMIT 1";
        command.Parameters.AddWithValue("$site", siteId); command.Parameters.AddWithValue("$ip", ip); command.Parameters.AddWithValue("$exclude", (object?)excludeId ?? DBNull.Value);
        if (command.ExecuteScalar() is string conflict) throw new InvalidOperationException($"Management IP is already assigned to device {conflict}.");
    }

    private static void InsertDeviceCandidate(SqliteConnection db, SqliteTransaction transaction, DeviceImportCandidate candidate, long now)
    {
        using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = @"INSERT INTO device_inventory(id,name,management_ip,vendor,model,device_type,site_id,group_id,description,enabled,created_ms,updated_ms,terminal_profile_id)
            VALUES($id,$name,$ip,$vendor,$model,$type,$site,$group,$description,$enabled,$created,$updated,$profile)";
        command.Parameters.AddWithValue("$id", candidate.DeviceId); command.Parameters.AddWithValue("$name", candidate.Name); command.Parameters.AddWithValue("$ip", candidate.ManagementIp);
        command.Parameters.AddWithValue("$vendor", candidate.Vendor); command.Parameters.AddWithValue("$model", candidate.Model); command.Parameters.AddWithValue("$type", candidate.DeviceType);
        command.Parameters.AddWithValue("$site", candidate.SiteId); command.Parameters.AddWithValue("$group", (object?)candidate.GroupId ?? DBNull.Value); command.Parameters.AddWithValue("$description", candidate.Description);
        command.Parameters.AddWithValue("$enabled", candidate.Enabled ? 1 : 0); command.Parameters.AddWithValue("$created", now); command.Parameters.AddWithValue("$updated", now); command.Parameters.AddWithValue("$profile", (object?)candidate.TerminalProfileId ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static void UpdateDeviceCandidate(SqliteConnection db, SqliteTransaction transaction, DeviceImportCandidate candidate, long now)
    {
        using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = @"UPDATE device_inventory SET name=$name,management_ip=$ip,vendor=$vendor,model=$model,device_type=$type,
            site_id=$site,group_id=$group,description=$description,enabled=$enabled,
            updated_ms=CASE WHEN $updated<=updated_ms THEN updated_ms+1 ELSE $updated END,terminal_profile_id=$profile
            WHERE id=$id AND updated_ms=$expectedUpdated";
        command.Parameters.AddWithValue("$id", candidate.DeviceId); command.Parameters.AddWithValue("$name", candidate.Name); command.Parameters.AddWithValue("$ip", candidate.ManagementIp);
        command.Parameters.AddWithValue("$vendor", candidate.Vendor); command.Parameters.AddWithValue("$model", candidate.Model); command.Parameters.AddWithValue("$type", candidate.DeviceType);
        command.Parameters.AddWithValue("$site", candidate.SiteId); command.Parameters.AddWithValue("$group", (object?)candidate.GroupId ?? DBNull.Value); command.Parameters.AddWithValue("$description", candidate.Description);
        command.Parameters.AddWithValue("$enabled", candidate.Enabled ? 1 : 0); command.Parameters.AddWithValue("$updated", now);
        command.Parameters.AddWithValue("$expectedUpdated", candidate.ExpectedUpdatedMs ?? throw new InvalidOperationException("Import preview has no update version."));
        command.Parameters.AddWithValue("$profile", (object?)candidate.TerminalProfileId ?? DBNull.Value);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("A device changed after preview; import was rolled back.");
    }

    private static void InsertDeviceAudit(SqliteConnection db, SqliteTransaction transaction, string actor, string action, string subjectId, object detail)
    {
        using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO audit_events(id,at_ms,action,outcome,detail_json) VALUES($id,$at,$action,'SUCCESS',$detail)";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N")); command.Parameters.AddWithValue("$at", UtcNowMs()); command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$detail", JsonSerializer.Serialize(new { actor, subjectId, detail })); command.ExecuteNonQuery();
    }

    private static IReadOnlyList<DeviceAuditView> ReadDeviceAudits(SqliteConnection db, string subjectId, int limit)
    {
        using var command = db.CreateCommand(); command.CommandText = "SELECT id,at_ms,action,outcome,detail_json FROM audit_events WHERE json_extract(detail_json,'$.subjectId')=$subject ORDER BY at_ms DESC LIMIT $limit";
        command.Parameters.AddWithValue("$subject", subjectId); command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader(); var result = new List<DeviceAuditView>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        return result;
    }

    private static string RequiredText(string? value, string name, int max)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length < 1 || text.Length > max || text.Any(char.IsControl)) throw new ArgumentException($"{name} is required and must be at most {max} characters.", name);
        return text;
    }

    private static string Clean(string? value, int max) { var text = (value ?? string.Empty).Trim(); if (text.Length > max || text.Any(char.IsControl)) throw new ArgumentException("Field contains unsupported characters."); return text; }
    private static string? CleanNullable(string? value, int max) { var text = (value ?? string.Empty).Trim(); return text.Length == 0 ? null : Clean(text, max); }
    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static bool IsValidDeviceId(string value) => IsValidToken(value, 96);
    private static bool IsValidToken(string value, int max) => value.Length >= 1 && value.Length <= max && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    private static string Text(string? value) => (value ?? string.Empty).Trim();
    private static bool TryNormalizeIp(string value, out string normalized)
    {
        if (IPAddress.TryParse(value.Trim(), out var address)) { normalized = address.ToString(); return true; }
        normalized = string.Empty; return false;
    }

    private sealed record ParsedCsvRow(int Line, IReadOnlyDictionary<string, string> Values)
    {
        public string? Get(string key) => Values.TryGetValue(key, out var value) ? value : null;
    }
    private sealed record ParsedCsv(IReadOnlyList<ParsedCsvRow> Rows);

    private static ParsedCsv ParseCsv(string csv)
    {
        var rows = new List<List<string>>();
        var current = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;
        var afterQuote = false;
        var fieldStarted = false;
        var rowHasContent = false;

        void Append(char value)
        {
            if (cell.Length >= MaximumDeviceImportCellCharacters)
                throw new DeviceImportResourceLimitException($"CSV field exceeds {MaximumDeviceImportCellCharacters} characters.");
            cell.Append(value);
        }

        void EndField()
        {
            current.Add(cell.ToString());
            cell.Clear();
            fieldStarted = false;
            afterQuote = false;
        }

        void EndRow()
        {
            EndField();
            if (rowHasContent || current.Count > 1) rows.Add(current);
            current = new List<string>();
            rowHasContent = false;
        }

        for (var i = 0; i < csv.Length; i++)
        {
            var ch = csv[i];
            if (inQuotes)
            {
                if (ch == '"' && i + 1 < csv.Length && csv[i + 1] == '"') { Append('"'); i++; }
                else if (ch == '"') { inQuotes = false; afterQuote = true; }
                else { Append(ch); rowHasContent = true; }
                continue;
            }

            if (afterQuote)
            {
                if (ch == ',') EndField();
                else if (ch is '\r' or '\n') { if (ch == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n') i++; EndRow(); }
                else throw new ArgumentException("CSV contains characters after a closing quote before the delimiter.");
                continue;
            }

            if (ch == '"')
            {
                if (fieldStarted) throw new ArgumentException("CSV quotes must start a field or escape a quote inside a quoted field.");
                inQuotes = true; fieldStarted = true; rowHasContent = true;
            }
            else if (ch == ',') { EndField(); rowHasContent = true; }
            else if (ch is '\r' or '\n') { if (ch == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n') i++; EndRow(); }
            else { Append(ch); fieldStarted = true; rowHasContent = true; }
        }
        if (inQuotes) throw new ArgumentException("CSV contains an unterminated quoted field.");
        if (fieldStarted || current.Count > 0 || rowHasContent) EndRow();
        if (rows.Count < 1) throw new ArgumentException("CSV must contain a header row.");

        var headers = rows[0].Select(header => header.Trim().TrimStart('\uFEFF')).ToArray();
        if (headers.Any(header => header.Length == 0)) throw new ArgumentException("CSV header contains an empty column name.");
        if (headers.GroupBy(header => header, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new ArgumentException("CSV header contains duplicate column names.");
        var allowed = new HashSet<string>(new[] { "deviceId", "name", "managementIp", "vendor", "model", "deviceType", "siteId", "groupName", "description", "enabled", "terminalProfileId" }, StringComparer.OrdinalIgnoreCase);
        var unknown = headers.Where(header => !allowed.Contains(header)).ToArray();
        if (unknown.Length > 0) throw new ArgumentException("CSV contains unsupported columns: " + string.Join(",", unknown));
        if (!headers.Any(header => header.Equals("name", StringComparison.OrdinalIgnoreCase)) || !headers.Any(header => header.Equals("managementIp", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("CSV must contain name and managementIp columns.");

        var result = new List<ParsedCsvRow>();
        for (var rowIndex = 1; rowIndex < rows.Count; rowIndex++)
        {
            if (rows[rowIndex].Count != headers.Length)
                throw new ArgumentException($"CSV row {rowIndex + 1} has {rows[rowIndex].Count} fields; expected {headers.Length}.");
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < headers.Length; index++) values.Add(headers[index], rows[rowIndex][index]);
            result.Add(new ParsedCsvRow(rowIndex + 1, values));
        }
        return new ParsedCsv(result);
    }

    private static bool TryParseEnabled(string value, out bool enabled)
    {
        if (value.Length == 0 || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("1") || value.Equals("yes", StringComparison.OrdinalIgnoreCase) || value == "启用") { enabled = true; return true; }
        if (value.Equals("false", StringComparison.OrdinalIgnoreCase) || value.Equals("0") || value.Equals("no", StringComparison.OrdinalIgnoreCase) || value == "禁用") { enabled = false; return true; }
        enabled = true; return false;
    }

    private static bool IsCsvFormulaValue(string value)
    {
        var trimmed = value.TrimStart();
        if (trimmed.Length == 0) return false;
        return trimmed[0] is '=' or '+' or '@' ||
            (trimmed[0] == '-' && trimmed.Length > 1 && (char.IsDigit(trimmed[1]) || trimmed[1] == '='));
    }

    private void CleanupImportStateLocked(long now)
    {
        foreach (var expired in _deviceImportPreviews.Where(item => item.Value.ExpiresAtMs <= now).Select(item => item.Key).ToArray())
            _deviceImportPreviews.Remove(expired);
        foreach (var expired in _deviceImportReceipts.Where(item => item.Value.ExpiresAtMs <= now).Select(item => item.Key).ToArray())
            _deviceImportReceipts.Remove(expired);
    }

    private sealed record ImportState(string Id, string SiteId, string ManagementIp, string? TerminalProfileId, long UpdatedMs);
    private static List<ImportState> ReadInventoryImportState(SqliteConnection db)
    {
        using var command = db.CreateCommand(); command.CommandText = "SELECT id,site_id,management_ip,terminal_profile_id,updated_ms FROM device_inventory";
        using var reader = command.ExecuteReader(); var result = new List<ImportState>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), NormalizeForCompare(reader.GetString(2)), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt64(4)));
        return result;
    }

    private static string NormalizeForCompare(string value) => TryNormalizeIp(value, out var ip) ? ip : value.Trim().ToLowerInvariant();
    private static bool SiteExists(SqliteConnection db, string siteId) { using var command = db.CreateCommand(); command.CommandText = "SELECT 1 FROM sites WHERE id=$id"; command.Parameters.AddWithValue("$id", siteId); return command.ExecuteScalar() is not null; }
    private static bool TerminalProfileExists(SqliteConnection db, string id) { using var command = db.CreateCommand(); command.CommandText = "SELECT 1 FROM h3c_device_profiles WHERE id=$id"; command.Parameters.AddWithValue("$id", id); return command.ExecuteScalar() is not null; }
    private static string? ResolveGroupByName(SqliteConnection db, string siteId, string name, List<string> errors)
    {
        if (name.Length == 0) return null;
        using var command = db.CreateCommand(); command.CommandText = "SELECT id FROM device_groups WHERE site_id=$site AND name=$name"; command.Parameters.AddWithValue("$site", siteId); command.Parameters.AddWithValue("$name", name);
        var id = command.ExecuteScalar()?.ToString(); if (id is null) errors.Add("GROUP_NOT_FOUND"); return id;
    }
}
