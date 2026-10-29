using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace NetConsole.Next.Service;

internal sealed record ZteDeviceProfileSnapshot(
    string Id, string Host, int Port, string Username, string CredentialReference,
    string HostKeySha256, bool Enabled, long CreatedMs, long UpdatedMs,
    string HostKeyUpdatePolicy = "STRICT", bool AllowLegacyAlgorithms = false,
    string TerminalEncoding = "AUTO", string SiteId = "local");

internal sealed record ZteCollectionRunSnapshot(
    string Id, string ProfileId, string State, long StartedMs, long? EndedMs,
    string? FailureReason, IReadOnlyDictionary<string, string> CommandStatuses);

internal sealed record ZteFactSnapshot(
    string ProfileId, string Kind, string Status, long CapturedMs, string PayloadJson,
    string? RunId, bool IsCurrent);

internal sealed record ZteProfileRequest(string Id, string Host, int Port, string Username,
    string CredentialReference, string HostKeySha256, bool Enabled = true,
    string HostKeyUpdatePolicy = "STRICT", bool AllowLegacyAlgorithms = false,
    string TerminalEncoding = "AUTO", string SiteId = "local");

internal sealed record ZteProfileView(string Id, string Host, int Port, string Username, bool Enabled,
    long CreatedMs, long UpdatedMs, string TerminalEncoding, string SiteId);

internal sealed class ZteCollectionRejectedException(string message) : InvalidOperationException(message);

internal sealed record ZteCollectRequest(string? ProfileId = null);

internal static class ZteCollectionSchema
{
    public static void Apply(SqliteConnection db)
    {
        using var command = db.CreateCommand();
        command.CommandText = @"
CREATE TABLE IF NOT EXISTS zte_device_profiles(
 id TEXT PRIMARY KEY, source_id TEXT NOT NULL UNIQUE REFERENCES collection_sources(id),
 host TEXT NOT NULL, port INTEGER NOT NULL, username TEXT NOT NULL,
 credential_reference TEXT NOT NULL, host_key_sha256 TEXT NOT NULL DEFAULT '', enabled INTEGER NOT NULL,
 created_ms INTEGER NOT NULL, updated_ms INTEGER NOT NULL, host_key_update_policy TEXT NOT NULL,
 allow_legacy_algorithms INTEGER NOT NULL, terminal_encoding TEXT NOT NULL, site_id TEXT NOT NULL REFERENCES sites(id));
CREATE TABLE IF NOT EXISTS zte_collection_runs(
 id TEXT PRIMARY KEY, profile_id TEXT NOT NULL REFERENCES zte_device_profiles(id), state TEXT NOT NULL,
 started_ms INTEGER NOT NULL, ended_ms INTEGER, failure_reason TEXT, command_status_json TEXT NOT NULL DEFAULT '{}');
CREATE TABLE IF NOT EXISTS zte_facts(
 id INTEGER PRIMARY KEY AUTOINCREMENT, profile_id TEXT NOT NULL REFERENCES zte_device_profiles(id),
 run_id TEXT REFERENCES zte_collection_runs(id), kind TEXT NOT NULL, status TEXT NOT NULL,
 captured_ms INTEGER NOT NULL, payload_json TEXT NOT NULL, is_current INTEGER NOT NULL CHECK(is_current IN (0,1)));
CREATE UNIQUE INDEX IF NOT EXISTS ux_zte_current_fact ON zte_facts(profile_id,kind) WHERE is_current=1;
CREATE INDEX IF NOT EXISTS ix_zte_fact_history ON zte_facts(profile_id,kind,captured_ms DESC);
";
        command.ExecuteNonQuery();
    }
}

internal sealed class ZteSshDeviceSession : IAsyncDisposable
{
    private readonly ZteDeviceProfileSnapshot _profile;
    private readonly IH3cCredentialProvider _credentials;
    private readonly IH3cSshTransportFactory _transportFactory;
    private IH3cSshTransport? _transport;

    public ZteSshDeviceSession(ZteDeviceProfileSnapshot profile, IH3cCredentialProvider credentials,
        IH3cSshTransportFactory transportFactory)
    {
        _profile = profile;
        _credentials = credentials;
        _transportFactory = transportFactory;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var password = _credentials.GetPassword(_profile.CredentialReference);
        if (string.IsNullOrEmpty(password)) throw new H3cCredentialUnavailableException();
        var h3cProfile = new H3cDeviceProfileSnapshot(_profile.Id, _profile.Host, _profile.Port, _profile.Username,
            _profile.CredentialReference, _profile.HostKeySha256, _profile.Enabled, _profile.CreatedMs, _profile.UpdatedMs,
            _profile.HostKeyUpdatePolicy, _profile.AllowLegacyAlgorithms, _profile.TerminalEncoding, "SSH", false);
        _transport = _transportFactory.Create(h3cProfile, password);
        await _transport.ConnectAsync((_, fingerprint) => H3cHostKeyFingerprint.Matches(_profile.HostKeySha256, fingerprint), cancellationToken);
    }

    public async Task<string> ExecuteAsync(string command, CancellationToken cancellationToken)
    {
        if (!ZteCommandPolicy.IsAllowed(command)) throw new ArgumentException("Only allowlisted ZTE read-only commands are allowed.", nameof(command));
        if (_transport is null || !_transport.IsConnected) throw new InvalidOperationException("ZTE SSH session is not connected.");
        return await _transport.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _transport?.Dispose();
        _transport = null;
        return ValueTask.CompletedTask;
    }
}

internal sealed class ZteCollectionService
{
    private static readonly string[] Commands =
    {
        ZteCommandPolicy.ShowVersion, ZteCommandPolicy.ShowInterfaceBrief,
        ZteCommandPolicy.ShowOpticalInfoBrief, ZteCommandPolicy.ShowLldpNeighborBrief,
        ZteCommandPolicy.ShowLldpEntry
    };
    private readonly Catalog _catalog;
    private readonly IH3cCredentialProvider _credentials;
    private readonly IH3cSshTransportFactory _transportFactory;
    private readonly ConcurrentDictionary<string, ZteCollectionRunSnapshot> _runs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _tasks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _reservedProfiles = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _slots = new(2, 2);
    private readonly CancellationTokenSource _shutdown = new();
    private const int QueueCapacity = 8;
    private int _queued;

    public ZteCollectionService(Catalog catalog, IH3cCredentialProvider credentials,
        IH3cSshTransportFactory? transportFactory = null)
    {
        _catalog = catalog;
        _credentials = credentials;
        _transportFactory = transportFactory ?? new SshNetH3cSshTransportFactory();
    }

    public IReadOnlyList<ZteProfileView> Profiles(P2B5SiteScopeDecision scope, int limit = 100) =>
        _catalog.RecentZteDeviceProfiles(limit, scope.IsGlobal ? null : scope.AllowedSiteReferences)
            .Where(profile => scope.IsGlobal || scope.AllowedSiteReferences.Contains(profile.SiteId))
            .Select(ToView)
            .ToArray();

    public ZteProfileView? Profile(string id) => _catalog.GetZteDeviceProfile(id) is { } profile ? ToView(profile) : null;

    public bool ProfileIsInScope(string id, P2B5SiteScopeDecision scope) =>
        _catalog.GetZteDeviceProfile(id) is { } profile &&
        (scope.IsGlobal || scope.AllowedSiteReferences.Contains(profile.SiteId));

    private static ZteProfileView ToView(ZteDeviceProfileSnapshot profile) => new(profile.Id, profile.Host,
        profile.Port, profile.Username, profile.Enabled, profile.CreatedMs, profile.UpdatedMs,
        profile.TerminalEncoding, profile.SiteId);
    public ZteCollectionRunSnapshot? GetRun(string id) => _runs.TryGetValue(id, out var run) ? run : _catalog.GetZteCollectionRun(id);

    public string Start(string profileId)
    {
        var profile = _catalog.GetZteDeviceProfile(profileId) ?? throw new KeyNotFoundException("ZTE device profile was not found.");
        if (!profile.Enabled) throw new InvalidOperationException("ZTE device profile is disabled.");
        if (!_reservedProfiles.TryAdd(profileId, 0))
            throw new ZteCollectionRejectedException("A ZTE collection is already running for this profile.");
        if (Interlocked.Increment(ref _queued) > QueueCapacity)
        {
            Interlocked.Decrement(ref _queued);
            _reservedProfiles.TryRemove(profileId, out _);
            throw new ZteCollectionRejectedException("The bounded ZTE collection queue is full.");
        }
        var runId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _runs[runId] = new(runId, profileId, "QUEUED", now, null, null, new Dictionary<string, string>());
        try { _catalog.StartZteCollectionRun(runId, profileId, now, "QUEUED"); }
        catch
        {
            _runs.TryRemove(runId, out _);
            _reservedProfiles.TryRemove(profileId, out _);
            Interlocked.Decrement(ref _queued);
            throw;
        }
        var task = CollectAsync(runId, profile);
        _tasks[runId] = task;
        _ = task.ContinueWith(completed => { _tasks.TryRemove(runId, out var ignored); }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return runId;
    }

    public async Task StopAsync()
    {
        _shutdown.Cancel();
        var tasks = _tasks.Values.ToArray();
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (Exception) when (tasks.Length > 0) { }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _shutdown.Dispose();
        _slots.Dispose();
    }

    public IReadOnlyList<ZteFactSnapshot> Facts(string profileId, string? kind, bool history, int limit)
        => _catalog.QueryZteFacts(profileId, kind, history, limit);

    private async Task CollectAsync(string runId, ZteDeviceProfileSnapshot profile)
    {
        var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
        var started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var facts = new List<(string Kind, string Status, string Json)>();
        string? failure = null;
        var acquired = false;
        var dequeued = false;
        try
        {
            await _slots.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            acquired = true;
            Interlocked.Decrement(ref _queued);
            dequeued = true;
            _catalog.MarkZteCollectionRunRunning(runId, started);
            _runs[runId] = new(runId, profile.Id, "RUNNING", started, null, null, statuses);
            await using var session = new ZteSshDeviceSession(profile, _credentials, _transportFactory);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            await session.ConnectAsync(timeout.Token);
            foreach (var command in Commands)
            {
                try
                {
                    var output = await session.ExecuteAsync(command, timeout.Token);
                    var fact = Parse(command, output);
                    statuses[command] = fact.Status;
                    facts.Add((fact.Kind, fact.Status, fact.Json));
                    if (fact.Status is not ("PARSED" or "EMPTY")) failure ??= fact.Status;
                }
                catch (H3cPartialOutputException ex)
                {
                    statuses[command] = ex.FailureCode;
                    failure ??= ex.FailureCode;
                    facts.Add(("raw:" + command, ex.FailureCode,
                        JsonSerializer.Serialize(new { command, raw = ex.Output, captureStatus = "PARTIAL" })));
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    statuses[command] = _shutdown.IsCancellationRequested ? "INTERRUPTED" : "TIMEOUT";
                    failure ??= statuses[command];
                    break;
                }
                catch (Exception ex)
                {
                    statuses[command] = ex.GetType().Name;
                    failure ??= ex.GetType().Name;
                }
            }
            var state = failure is null ? "SUCCEEDED" : failure == "INTERRUPTED" ? "INTERRUPTED" : facts.Any(x => x.Status is "PARSED" or "EMPTY") ? "PARTIAL" : "FAILED";
            _catalog.CommitZteCollection(runId, profile.Id, state, failure, statuses, facts);
            _runs[runId] = new(runId, profile.Id, state, started, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), failure, new Dictionary<string, string>(statuses));
        }
        catch (Exception ex)
        {
            failure = ex is OperationCanceledException ? "INTERRUPTED" : ex is H3cCredentialUnavailableException ? "CREDENTIAL_UNAVAILABLE" : ex.GetType().Name;
            _catalog.CommitZteCollection(runId, profile.Id, failure == "INTERRUPTED" ? "INTERRUPTED" : "FAILED", failure, statuses, facts);
            _runs[runId] = new(runId, profile.Id, failure == "INTERRUPTED" ? "INTERRUPTED" : "FAILED", started, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), failure, new Dictionary<string, string>(statuses));
        }
        finally
        {
            if (!dequeued) Interlocked.Decrement(ref _queued);
            if (acquired) _slots.Release();
            _reservedProfiles.TryRemove(profile.Id, out _);
        }
    }

    private static (string Kind, string Status, string Json) Parse(string command, string output)
    {
        return command switch
        {
            ZteCommandPolicy.ShowVersion => Wrap("identity", ZteZxr10Parser.ParseIdentity(output).Status, ZteZxr10Parser.ParseIdentity(output).Value, output),
            ZteCommandPolicy.ShowInterfaceBrief => Wrap("interfaces", ZteZxr10Parser.ParseInterfaces(output).Status, ZteZxr10Parser.ParseInterfaces(output).Value, output),
            ZteCommandPolicy.ShowOpticalInfoBrief => Wrap("optical", ZteZxr10Parser.ParseOpticalBrief(output).Status, ZteZxr10Parser.ParseOpticalBrief(output).Value, output),
            ZteCommandPolicy.ShowLldpNeighborBrief => Wrap("lldp", ZteZxr10Parser.ParseLldpBrief(output).Status, ZteZxr10Parser.ParseLldpBrief(output).Value, output),
            ZteCommandPolicy.ShowLldpEntry => Wrap("lldp-entry", ZteZxr10Parser.ParseLldpEntries(output).Status, ZteZxr10Parser.ParseLldpEntries(output).Value, output),
            _ => throw new ArgumentException("Unsupported ZTE command.", nameof(command))
        };
    }

    private static (string Kind, string Status, string Json) Wrap<T>(string kind, string status, T parsed, string raw)
        => (kind, status, JsonSerializer.Serialize(new { raw, parsed }));
}

internal sealed partial class Catalog
{
    public void ApplyZteSchema()
    {
        using var db = Open();
        ZteCollectionSchema.Apply(db);
    }

    public void CreateZteDeviceProfile(ZteDeviceProfileSnapshot profile)
    {
        if (!Guid.TryParseExact(profile.Id, "N", out _)) throw new ArgumentException("ZTE profile id must be a 32-character GUID.", nameof(profile));
        if (string.IsNullOrWhiteSpace(profile.Host) || Uri.CheckHostName(profile.Host) == UriHostNameType.Unknown) throw new ArgumentException("ZTE host is invalid.", nameof(profile));
        if (profile.Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(profile));
        if (!H3cCredentialReference.IsValid(profile.CredentialReference)) throw new ArgumentException("Credential reference is invalid.", nameof(profile));
        if (profile.HostKeyUpdatePolicy is not "STRICT") throw new ArgumentException("ZTE profiles require STRICT host-key verification.", nameof(profile));
        if (!TerminalTextEncoding.IsSupported(profile.TerminalEncoding)) throw new ArgumentException("Terminal encoding is invalid.", nameof(profile));
        var fingerprint = string.IsNullOrWhiteSpace(profile.HostKeySha256) ? string.Empty : H3cHostKeyFingerprint.Normalize(profile.HostKeySha256);
        if (profile.HostKeyUpdatePolicy == "STRICT" && fingerprint.Length == 0) throw new ArgumentException("STRICT ZTE profile requires a host-key fingerprint.", nameof(profile));
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var db = Open();
        using (var existing = db.CreateCommand())
        {
            existing.CommandText = "SELECT host,port,username,credential_reference,host_key_sha256,site_id FROM zte_device_profiles WHERE id=$id";
            existing.Parameters.AddWithValue("$id", profile.Id);
            using var reader = existing.ExecuteReader();
            if (reader.Read())
            {
                var same = string.Equals(reader.GetString(0), profile.Host, StringComparison.OrdinalIgnoreCase) &&
                    reader.GetInt32(1) == profile.Port && string.Equals(reader.GetString(2), profile.Username, StringComparison.Ordinal) &&
                    string.Equals(reader.GetString(3), profile.CredentialReference, StringComparison.Ordinal) &&
                    string.Equals(reader.GetString(4), fingerprint, StringComparison.Ordinal) &&
                    string.Equals(reader.GetString(5), profile.SiteId, StringComparison.Ordinal);
                if (!same) throw new InvalidOperationException("ZTE profile identity is immutable; create a new profile ID for a different target or credential.");
            }
        }
        using var tx = db.BeginTransaction();
        using (var source = db.CreateCommand()) { source.Transaction = tx; source.CommandText = "INSERT INTO collection_sources(id,site_id,kind,label,enabled) VALUES($id,$site,'ZTE_SSH',$label,$enabled) ON CONFLICT(id) DO UPDATE SET label=excluded.label,enabled=excluded.enabled"; source.Parameters.AddWithValue("$id", "zte-" + profile.Id); source.Parameters.AddWithValue("$site", profile.SiteId); source.Parameters.AddWithValue("$label", $"ZTE {profile.Host}:{profile.Port}"); source.Parameters.AddWithValue("$enabled", profile.Enabled ? 1 : 0); source.ExecuteNonQuery(); }
        using var insert = db.CreateCommand(); insert.Transaction = tx; insert.CommandText = @"INSERT INTO zte_device_profiles(id,source_id,host,port,username,credential_reference,host_key_sha256,enabled,created_ms,updated_ms,host_key_update_policy,allow_legacy_algorithms,terminal_encoding,site_id) VALUES($id,$source,$host,$port,$user,$credential,$fingerprint,$enabled,$created,$updated,$policy,$legacy,$encoding,$site) ON CONFLICT(id) DO UPDATE SET enabled=excluded.enabled,updated_ms=excluded.updated_ms,host_key_update_policy=excluded.host_key_update_policy,allow_legacy_algorithms=excluded.allow_legacy_algorithms,terminal_encoding=excluded.terminal_encoding";
        insert.Parameters.AddWithValue("$id", profile.Id); insert.Parameters.AddWithValue("$source", "zte-" + profile.Id); insert.Parameters.AddWithValue("$host", profile.Host); insert.Parameters.AddWithValue("$port", profile.Port); insert.Parameters.AddWithValue("$user", profile.Username); insert.Parameters.AddWithValue("$credential", profile.CredentialReference); insert.Parameters.AddWithValue("$fingerprint", fingerprint); insert.Parameters.AddWithValue("$enabled", profile.Enabled ? 1 : 0); insert.Parameters.AddWithValue("$created", profile.CreatedMs > 0 ? profile.CreatedMs : now); insert.Parameters.AddWithValue("$updated", now); insert.Parameters.AddWithValue("$policy", profile.HostKeyUpdatePolicy); insert.Parameters.AddWithValue("$legacy", profile.AllowLegacyAlgorithms ? 1 : 0); insert.Parameters.AddWithValue("$encoding", profile.TerminalEncoding); insert.Parameters.AddWithValue("$site", profile.SiteId); insert.ExecuteNonQuery(); tx.Commit();
    }

    public ZteDeviceProfileSnapshot? GetZteDeviceProfile(string id) { using var db = Open(); using var c = db.CreateCommand(); c.CommandText = "SELECT id,host,port,username,credential_reference,host_key_sha256,enabled,created_ms,updated_ms,host_key_update_policy,allow_legacy_algorithms,terminal_encoding,site_id FROM zte_device_profiles WHERE id=$id"; c.Parameters.AddWithValue("$id", id); using var r = c.ExecuteReader(); return r.Read() ? new(r.GetString(0),r.GetString(1),r.GetInt32(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetInt32(6)!=0,r.GetInt64(7),r.GetInt64(8),r.GetString(9),r.GetInt32(10)!=0,r.GetString(11),r.GetString(12)) : null; }
    public IReadOnlyList<ZteDeviceProfileSnapshot> RecentZteDeviceProfiles(int limit = 100, IReadOnlySet<string>? siteReferences = null) { using var db = Open(); using var c = db.CreateCommand(); var sites = siteReferences?.Where(site => !string.IsNullOrWhiteSpace(site)).Distinct(StringComparer.Ordinal).ToArray() ?? Array.Empty<string>(); var where = sites.Length == 0 ? string.Empty : " WHERE site_id IN (" + string.Join(',', sites.Select((_, index) => "$site" + index)) + ")"; c.CommandText = "SELECT id,host,port,username,credential_reference,host_key_sha256,enabled,created_ms,updated_ms,host_key_update_policy,allow_legacy_algorithms,terminal_encoding,site_id FROM zte_device_profiles" + where + " ORDER BY updated_ms DESC LIMIT $limit"; for (var index = 0; index < sites.Length; index++) c.Parameters.AddWithValue("$site" + index, sites[index]); c.Parameters.AddWithValue("$limit", Math.Clamp(limit,1,100)); using var r=c.ExecuteReader(); var result=new List<ZteDeviceProfileSnapshot>(); while(r.Read()) result.Add(new(r.GetString(0),r.GetString(1),r.GetInt32(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetInt32(6)!=0,r.GetInt64(7),r.GetInt64(8),r.GetString(9),r.GetInt32(10)!=0,r.GetString(11),r.GetString(12))); return result; }
    public void StartZteCollectionRun(string id, string profileId, long startedMs, string state = "RUNNING") { using var db=Open(); using var c=db.CreateCommand(); c.CommandText="INSERT INTO zte_collection_runs(id,profile_id,state,started_ms) VALUES($id,$profile,$state,$started)"; c.Parameters.AddWithValue("$id",id); c.Parameters.AddWithValue("$profile",profileId); c.Parameters.AddWithValue("$state",state); c.Parameters.AddWithValue("$started",startedMs); c.ExecuteNonQuery(); }
    public void MarkZteCollectionRunRunning(string id, long startedMs) { using var db=Open(); using var c=db.CreateCommand(); c.CommandText="UPDATE zte_collection_runs SET state='RUNNING',started_ms=$started WHERE id=$id"; c.Parameters.AddWithValue("$id",id); c.Parameters.AddWithValue("$started",startedMs); c.ExecuteNonQuery(); }
    public void RecoverInterruptedZteCollectionRuns() { using var db=Open(); using var c=db.CreateCommand(); c.CommandText="UPDATE zte_collection_runs SET state='INTERRUPTED',ended_ms=$ended,failure_reason='Process terminated before ZTE collection completed.' WHERE state IN ('QUEUED','RUNNING')"; c.Parameters.AddWithValue("$ended",DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); c.ExecuteNonQuery(); }
    public ZteCollectionRunSnapshot? GetZteCollectionRun(string id) { using var db=Open(); using var c=db.CreateCommand(); c.CommandText="SELECT id,profile_id,state,started_ms,ended_ms,failure_reason,command_status_json FROM zte_collection_runs WHERE id=$id"; c.Parameters.AddWithValue("$id",id); using var r=c.ExecuteReader(); if(!r.Read()) return null; return new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetInt64(3),r.IsDBNull(4)?null:r.GetInt64(4),r.IsDBNull(5)?null:r.GetString(5),JsonSerializer.Deserialize<Dictionary<string,string>>(r.GetString(6)) ?? new()); }
    public IReadOnlyList<ZteFactSnapshot> QueryZteFacts(string profileId, string? kind, bool history, int limit) { using var db=Open(); using var c=db.CreateCommand(); c.CommandText="SELECT profile_id,kind,status,captured_ms,payload_json,run_id,is_current FROM zte_facts WHERE profile_id=$profile AND is_current=$current" + (string.IsNullOrWhiteSpace(kind)?"":" AND kind=$kind") + " ORDER BY captured_ms DESC LIMIT $limit"; c.Parameters.AddWithValue("$profile",profileId); c.Parameters.AddWithValue("$current",history?0:1); c.Parameters.AddWithValue("$limit",Math.Clamp(limit,1,500)); if(!string.IsNullOrWhiteSpace(kind)) c.Parameters.AddWithValue("$kind",kind); using var r=c.ExecuteReader(); var result=new List<ZteFactSnapshot>(); while(r.Read()) result.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetInt64(3),r.GetString(4),r.IsDBNull(5)?null:r.GetString(5),r.GetInt32(6)!=0)); return result; }
    public void CommitZteCollection(string runId, string profileId, string state, string? failure,
        IReadOnlyDictionary<string, string> statuses, IReadOnlyList<(string Kind, string Status, string Json)> facts)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using (var update = db.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = "UPDATE zte_collection_runs SET state=$state,ended_ms=$ended,failure_reason=$failure,command_status_json=$status WHERE id=$id";
            update.Parameters.AddWithValue("$state", state);
            update.Parameters.AddWithValue("$ended", now);
            update.Parameters.AddWithValue("$failure", (object?)failure ?? DBNull.Value);
            update.Parameters.AddWithValue("$status", JsonSerializer.Serialize(statuses));
            update.Parameters.AddWithValue("$id", runId);
            update.ExecuteNonQuery();
        }
        foreach (var fact in facts)
        {
            if (fact.Status == "PARSED")
            {
                using var current = db.CreateCommand();
                current.Transaction = tx;
                current.CommandText = "UPDATE zte_facts SET is_current=0 WHERE profile_id=$profile AND kind=$kind AND is_current=1";
                current.Parameters.AddWithValue("$profile", profileId);
                current.Parameters.AddWithValue("$kind", fact.Kind);
                current.ExecuteNonQuery();
            }
            InsertZteFact(db, tx, profileId, runId, fact.Kind, fact.Status, now, fact.Json, fact.Status == "PARSED");
        }
        tx.Commit();
    }

    private static void InsertZteFact(SqliteConnection db, SqliteTransaction tx, string profileId, string runId,
        string kind, string status, long captured, string payload, bool current)
    {
        using var insert = db.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = "INSERT INTO zte_facts(profile_id,run_id,kind,status,captured_ms,payload_json,is_current) VALUES($profile,$run,$kind,$status,$captured,$payload,$current)";
        insert.Parameters.AddWithValue("$profile", profileId);
        insert.Parameters.AddWithValue("$run", runId);
        insert.Parameters.AddWithValue("$kind", kind);
        insert.Parameters.AddWithValue("$status", status);
        insert.Parameters.AddWithValue("$captured", captured);
        insert.Parameters.AddWithValue("$payload", payload);
        insert.Parameters.AddWithValue("$current", current ? 1 : 0);
        insert.ExecuteNonQuery();
    }
}
