using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NetConsole.Next.Service;

internal sealed record FaultTransition(long AtMs, string Action, string ActorUserId, string EvidenceId, string Detail);
internal sealed record FaultIncident(string Id, string DeviceId, string ProfileId, string SiteId, string Kind,
    string ResourceKey, string Severity, string State, long FirstSeenMs, long LastSeenMs, string EvidenceType,
    string EvidenceId, string Detail, int Occurrences, IReadOnlyList<FaultTransition> History);
internal sealed record FaultPolicy(string DeviceId, string FaultId, TaskScopeSnapshot Target, bool Enabled,
    int IntervalMinutes, int MaximumAttempts, int Attempts, long? NextRunMs, string State, string? PlanId,
    string? RecoveryId, string? LastError);
internal sealed record FaultDocument(int Version, IReadOnlyList<FaultIncident> Incidents, IReadOnlyList<FaultPolicy> Policies);
internal sealed record FaultQueryResult(int Page, int PageSize, int Total, bool HasMore,
    IReadOnlyList<FaultIncident> Items, IReadOnlyList<FaultPolicy> Policies, IReadOnlyList<string> Limitations);
internal sealed record FaultPolicyRequest(string FaultId, string ExpectedProfileId, bool Enabled,
    int IntervalMinutes = 5, int MaximumAttempts = 3);
internal sealed record FaultVerification(string State, string Detail, FaultIncident Incident);

// A separate optional store keeps operational lifecycle independent of the Raw
// catalog. Commit succeeds on disk before the in-memory authority advances.
internal sealed class FaultCenterStore
{
    private const int MaximumBytes = 8 * 1024 * 1024;
    internal const int MaximumIncidents = 1000;
    internal const int MaximumPolicies = 64;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    public bool Unavailable { get; private set; }
    public FaultDocument Current { get; private set; } = new(1, [], []);

    public FaultCenterStore(string dataRoot)
    {
        _path = Path.Combine(dataRoot, "operations", "fault-center.json");
        try
        {
            CheckPath();
            if (!File.Exists(_path)) return;
            using var file = File.OpenRead(_path);
            if (file.Length is < 1 or > MaximumBytes) throw new InvalidDataException();
            var document = JsonSerializer.Deserialize<FaultDocument>(file, Json);
            Validate(document);
            Current = document!;
            file.Dispose();
            // An admission interrupted across restart is never automatically
            // replayed: the existing TaskPlan remains the evidence authority.
            if (Current.Policies.Any(p => p.State == "RUNNING"))
                Commit(Current with { Policies = Current.Policies.Select(p => p.State == "RUNNING"
                    ? p with { Enabled = false, NextRunMs = null, State = "INTERRUPTED", LastError = "RESTART_REQUIRES_REVIEW" } : p).ToArray() });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        { Unavailable = true; Current = new(1, [], []); }
    }

    public void Commit(FaultDocument next)
    {
        if (Unavailable) throw new InvalidOperationException("FAULT_STORE_UNAVAILABLE");
        Validate(next);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(next, Json);
        if (bytes.Length > MaximumBytes) throw new InvalidOperationException("FAULT_STORE_CAPACITY");
        CheckPath();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var part = _path + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            using (var file = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(true); }
            File.Move(part, _path, true);
            Current = next;
        }
        finally { if (File.Exists(part)) File.Delete(part); }
    }

    private static void Validate(FaultDocument? value)
    {
        if (value is not { Version: 1, Incidents: not null, Policies: not null } ||
            value.Incidents.Count > MaximumIncidents || value.Policies.Count > MaximumPolicies ||
            value.Incidents.Any(i => i is null) || value.Policies.Any(p => p is null) ||
            value.Incidents.Select(i => i.Id).Distinct().Count() != value.Incidents.Count ||
            value.Policies.Select(p => p.DeviceId).Distinct().Count() != value.Policies.Count ||
            value.Incidents.Any(i => i is null || string.IsNullOrWhiteSpace(i.Id) || i.Id.Length > 128 ||
                string.IsNullOrWhiteSpace(i.DeviceId) || string.IsNullOrWhiteSpace(i.SiteId) ||
                i.State is not ("OPEN" or "ACKNOWLEDGED" or "RESOLVED") || i.History is null || i.History.Count is < 1 or > 50 ||
                i.FirstSeenMs <= 0 || i.LastSeenMs < i.FirstSeenMs || i.Occurrences < 1 ||
                i.History.Any(h => h is null) || i.State == "RESOLVED" && !i.History.Any(h => h.Action == "RECOVERY_VERIFIED")) ||
            value.Policies.Any(p => p is null || p.Target is null || p.DeviceId != p.Target.DeviceId ||
                p.Target.Endpoint is null || p.IntervalMinutes is < 5 or > 1440 || p.MaximumAttempts is < 1 or > 3 ||
                p.Attempts < 0 || p.Attempts > p.MaximumAttempts || (p.Enabled && p.NextRunMs is null)))
            throw new InvalidDataException("Invalid fault store.");
    }

    private void CheckPath()
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(_path)!); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Fault storage path is a reparse point.");
        if (File.Exists(_path) && (File.GetAttributes(_path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Fault storage file is a reparse point.");
    }
}

internal static class FaultLifecycle
{
    public static string Fingerprint(OperationalFaultEvent item, string? profileId = null) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { item.SiteId, item.DeviceId, profileId, item.Kind, item.ResourceKey ?? item.DeviceId })))).ToLowerInvariant();

    public static FaultIncident Observe(FaultIncident? prior, OperationalFaultEvent item, string profileId, string actor, long now)
    {
        if (prior is null)
            return new(Fingerprint(item, profileId), item.DeviceId, profileId, item.SiteId, item.Kind, item.ResourceKey ?? item.DeviceId,
                item.Severity, "OPEN", item.AtMs, item.AtMs, item.EvidenceType, item.EvidenceId, item.Detail, 1,
                [new(now, "DETECTED", actor, item.EvidenceId, item.Detail)]);
        if (item.AtMs <= prior.LastSeenMs || item.EvidenceId == prior.EvidenceId ||
            prior.State == "RESOLVED" && item.AtMs <= prior.History.Last(h => h.Action == "RECOVERY_VERIFIED").AtMs) return prior;
        var reopened = prior.State == "RESOLVED";
        return prior with { LastSeenMs = item.AtMs, EvidenceId = item.EvidenceId, EvidenceType = item.EvidenceType,
            Detail = item.Detail, Occurrences = prior.Occurrences + 1, State = reopened ? "OPEN" : prior.State,
            History = Append(prior, new(now, reopened ? "REOPENED" : "OBSERVED", actor, item.EvidenceId, item.Detail)) };
    }
    public static IReadOnlyList<FaultTransition> Append(FaultIncident item, FaultTransition transition) =>
        item.History.Append(transition).TakeLast(50).ToArray();

    public static H3cFactSnapshot? FreshRecoveryFact(FaultIncident item, IReadOnlyList<H3cFactSnapshot> facts, long now) =>
        facts.Where(f => f.IsCurrent && f.CollectedRawVerified && f.CapturedMs > item.LastSeenMs && f.CapturedMs <= now &&
            now - f.CapturedMs <= OperationalHealthProjection.FreshnessWindowMs &&
            f.Status is not ("FAILED" or "PARTIAL" or "UNSUPPORTED" or "EMPTY") &&
            (item.Kind == "EVIDENCE_STALE" || item.Kind == "INTERFACE_DOWN" && f.Kind == "interface" &&
                f.IdentityKey == item.ResourceKey && OperationalHealthProjection.LinkState(f.PayloadJson) == "UP"))
        .MaxBy(f => f.CapturedMs);

    public static string? FreshApRecoveryEvidence(FaultIncident item, WirelessRuntimeStateView? runtime, long now) =>
        runtime?.ResourceId == item.ResourceKey && runtime.NormalizedState == "RUNNING_MASTER" && runtime.Sources.Count > 0 &&
        runtime.Sources.All(s => s.LatestOutcome == "SUCCESS" && s.Current?.VerificationLevel == "LIVE_VERIFIED" &&
            s.Current.CapturedMs > item.LastSeenMs && s.Current.CapturedMs <= now && now - s.Current.CapturedMs <= runtime.FreshnessWindowMs) &&
        runtime.Sources.Any(s => s.SourceAcDeviceId == item.DeviceId && s.SourceSiteId == item.SiteId) ? runtime.SourceRevision : null;
}

internal sealed class FaultCenterService : BackgroundService
{
    private readonly Catalog _catalog;
    private readonly OperationalHealthService _health;
    private readonly OperationalRecoveryService _recovery;
    private readonly WirelessReadModelService _wireless;
    private readonly P2B5SiteScopeStore _scopes;
    private readonly TaskSiteScopeAuthorizer _authorizer;
    private readonly ILogger<FaultCenterService> _logger;
    private readonly FaultCenterStore _store;
    private readonly object _gate = new();
    private int _scanPage = 1;

    public FaultCenterService(Catalog catalog, OperationalHealthService health, OperationalRecoveryService recovery,
        WirelessReadModelService wireless, P2B5SiteScopeStore scopes, TaskSiteScopeAuthorizer authorizer,
        RuntimeSettings settings, ILogger<FaultCenterService> logger)
    {
        (_catalog, _health, _recovery, _wireless, _scopes, _authorizer, _logger) =
            (catalog, health, recovery, wireless, scopes, authorizer, logger);
        _store = new(settings.DataRoot);
    }

    public FaultQueryResult Query(WebUserIdentity actor, P2B5SiteScopeDecision scope, int page = 1,
        string? state = null, string? deviceId = null, string? kind = null, long? fromMs = null, long? toMs = null)
    {
        RequireActor(actor, scope, false);
        lock (_gate)
        {
            Available();
            page = Math.Clamp(page, 1, 100000);
            var all = _store.Current.Incidents.Where(i => Visible(i, scope) &&
                (state is null || i.State == state) && (deviceId is null || i.DeviceId == deviceId) &&
                (kind is null || i.Kind == kind) && (fromMs is null || i.LastSeenMs >= fromMs) &&
                (toMs is null || i.LastSeenMs <= toMs)).OrderByDescending(i => i.LastSeenMs).ThenBy(i => i.Id).ToArray();
            return new(page, 25, all.Length, page * 25 < all.Length, all.Skip((page - 1) * 25).Take(25).ToArray(),
                _store.Current.Policies.Where(p => DeviceSiteScope.Allows(scope, p.Target.SiteId) &&
                    (actor.Role == "SUPERADMIN" || p.Target.ActorUserId == actor.UserId)).ToArray(),
                ["最多保留 1000 个资源故障、每项最近 50 次有效转换和 8 MiB 文件；数量满仅淘汰已恢复事件，未恢复事件不会自动删除。",
                 "健康检测每 15 秒轮巡一页（25 台）；单次手动同步也只检测一页，不把页面缺项视为恢复。",
                 "只读自动补采默认关闭，显式策略每轮最多 3 次，间隔至少 5 分钟；连接成功、补采成功不直接表示业务恢复。"]);
        }
    }

    public OperationalHealthSnapshot Sync(WebUserIdentity actor, P2B5SiteScopeDecision scope, int page)
    {
        RequireActor(actor, scope, true);
        var snapshot = _health.Get(scope, page, actor.UserId);
        Observe(snapshot, actor.UserId);
        return snapshot;
    }

    private void Observe(OperationalHealthSnapshot snapshot, string actorId)
    {
        lock (_gate)
        {
            Available();
            var next = _store.Current.Incidents.ToDictionary(i => i.Id, StringComparer.Ordinal);
            var changed = false;
            foreach (var item in snapshot.Events.Where(e => e.Severity != "INFO").OrderBy(e => e.AtMs))
            {
                var device = snapshot.Devices.FirstOrDefault(d => d.DeviceId == item.DeviceId);
                if (device?.ProfileId is null) continue;
                var id = FaultLifecycle.Fingerprint(item, device.ProfileId);
                next.TryGetValue(id, out var old);
                var updated = FaultLifecycle.Observe(old, item, device.ProfileId, actorId, snapshot.AsOfMs);
                if (ReferenceEquals(updated, old)) continue;
                if (old is null && next.Count >= FaultCenterStore.MaximumIncidents)
                {
                    var retired = next.Values.Where(i => i.State == "RESOLVED").MinBy(i => i.LastSeenMs);
                    if (retired is null) throw new InvalidOperationException("FAULT_ACTIVE_CAPACITY");
                    next.Remove(retired.Id);
                }
                next[id] = updated; changed = true;
            }
            if (changed) _store.Commit(_store.Current with { Incidents = next.Values.ToArray() });
        }
    }

    public FaultIncident Acknowledge(string id, WebUserIdentity actor, P2B5SiteScopeDecision scope)
    {
        RequireActor(actor, scope, true);
        lock (_gate)
        {
            var item = Find(id, scope);
            if (item.State != "OPEN") return item;
            var updated = item with { State = "ACKNOWLEDGED", History = FaultLifecycle.Append(item,
                new(Now(), "ACKNOWLEDGED", actor.UserId, item.EvidenceId, "已人工确认，尚未证明恢复。")) };
            Save(updated); return updated;
        }
    }

    public FaultVerification Verify(string id, WebUserIdentity actor, P2B5SiteScopeDecision scope)
    {
        RequireActor(actor, scope, true);
        lock (_gate)
        {
            var item = Find(id, scope);
            if (item.State == "RESOLVED") return new("ALREADY_RESOLVED", "保留已验证恢复记录。", item);
            var target = _catalog.GetDeviceInventoryDetail(item.DeviceId, scope);
            if (target?.TerminalProfileId != item.ProfileId || target.Item.SiteId != item.SiteId)
                throw new TaskScopeRevokedException("FAULT_TARGET_CHANGED");
            var now = Now(); string? evidence = null;
            var facts = _catalog.QueryH3cFacts(item.ProfileId, limit: 1000, scope: scope, requireCapturedScope: true);
            evidence = FaultLifecycle.FreshRecoveryFact(item, facts, now)?.Id;
            if (item.Kind == "PROBE_PACKET_LOSS")
            {
                // The health projection already enforces current endpoint and finished, fresh diagnostic samples.
                for (var page = 1; page <= 40; page++)
                {
                    var snapshot = _health.Get(scope, page, actor.UserId);
                    var health = snapshot.Devices.FirstOrDefault(d => d.DeviceId == item.DeviceId);
                    if (health?.PacketLossPercent is { State: "OBSERVED", Value: 0, CapturedMs: { } at } metric &&
                        at > item.LastSeenMs && metric.ResourceKey == item.ResourceKey)
                        evidence = metric.EvidenceIds.FirstOrDefault();
                    if (health is not null || !snapshot.HasMore) break;
                }
            }
            if (item.Kind is "AP_OFFLINE" or "AP_CONFLICT")
            {
                var runtime = _wireless.GetRuntimeState(item.ResourceKey, scope, nowMs: now);
                evidence = FaultLifecycle.FreshApRecoveryEvidence(item, runtime, now);
            }
            // A collection failure is resolved only by a later complete successful task with matching frozen identity.
            if (item.Kind == "COLLECTION_FAILURE")
                evidence = _catalog.RecentTaskRuns(100, scope).Where(r => r.State == "SUCCEEDED" && r.CaptureStatus == "COMPLETE" &&
                    r.EndedMs > item.LastSeenMs && r.EndedMs <= now && now - r.EndedMs <= OperationalHealthProjection.FreshnessWindowMs &&
                    r.DefinitionId is not null && _catalog.GetTaskDefinition(r.DefinitionId)?.ScopeSnapshot is { } frozen &&
                    frozen.DeviceId == item.DeviceId && frozen.SiteId == item.SiteId && frozen.ProfileId == item.ProfileId &&
                    frozen.Endpoint is not null && frozen.Endpoint.Host == target.TerminalProfile?.Host && frozen.Endpoint.Port == target.TerminalProfile?.Port)
                    .MaxBy(r => r.EndedMs)?.Id;
            if (evidence is null)
            {
                var blocked = item with { History = FaultLifecycle.Append(item, new(now, "VERIFICATION_BLOCKED", actor.UserId,
                    item.EvidenceId, "缺少同资源、更新且有效的恢复证据；状态保持。")) };
                Save(blocked); return new("RECOVERY_NOT_VERIFIED", "缺少更新且有效的恢复证据。", blocked);
            }
            var updated = item with { State = "RESOLVED", History = FaultLifecycle.Append(item,
                new(now, "RECOVERY_VERIFIED", actor.UserId, evidence, "已核验更新的同资源恢复证据。")) };
            Save(updated); return new("RECOVERY_VERIFIED", "恢复证据已保存。", updated);
        }
    }

    public FaultPolicy SetPolicy(string deviceId, FaultPolicyRequest request, WebUserIdentity actor, P2B5SiteScopeDecision scope)
    {
        RequireActor(actor, scope, true);
        lock (_gate)
        {
            var item = Find(request.FaultId, scope);
            if (item.DeviceId != deviceId || item.ProfileId != request.ExpectedProfileId) throw new TaskScopeRevokedException("FAULT_TARGET_CHANGED");
            if (request.IntervalMinutes is < 5 or > 1440 || request.MaximumAttempts is < 1 or > 3)
                throw new ArgumentException("FAULT_POLICY_BUDGET_INVALID");
            var old = _store.Current.Policies.FirstOrDefault(p => p.DeviceId == deviceId);
            if (old?.State is "RUNNING" or "AWAITING_RECOLLECTION")
            {
                if (request.Enabled) throw new InvalidOperationException("FAULT_POLICY_BUSY");
                var disabled = old with { Enabled = false, NextRunMs = null };
                SavePolicy(disabled); return disabled;
            }
            var target = _authorizer.CaptureDefinition(item.ProfileId, actor.UserId);
            if (target.DeviceId != item.DeviceId || target.SiteId != item.SiteId || item.State == "RESOLVED" && request.Enabled)
                throw new TaskScopeRevokedException("FAULT_TARGET_CHANGED");
            var policy = new FaultPolicy(deviceId, item.Id, target, request.Enabled, request.IntervalMinutes,
                request.MaximumAttempts, 0, request.Enabled ? Now() + request.IntervalMinutes * 60000L : null,
                request.Enabled ? "SCHEDULED" : "DISABLED", null, null, null);
            SavePolicy(policy); return policy;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (_store.Unavailable) continue;
            try
            {
                // Background discovery is local read-only projection. Pick an active
                // administrator, then use that actor's current scope for all reads.
                var admin = _catalog.QueryFaultMonitorActor();
                if (admin is not null)
                {
                    var scope = _scopes.Resolve(admin);
                    if (scope.IsAuthorized)
                    {
                        var snapshot = _health.Get(scope, _scanPage, admin.UserId);
                        Observe(snapshot, admin.UserId);
                        _scanPage = snapshot.HasMore ? _scanPage + 1 : 1;
                    }
                }
                FaultPolicy[] policies;
                lock (_gate) policies = _store.Current.Policies.Where(p => p.Enabled || p.State == "AWAITING_RECOLLECTION").ToArray();
                foreach (var policy in policies) await AdvanceAsync(policy, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            { _logger.LogWarning("Fault center cycle stopped: {Reason}", ex.GetType().Name); }
        }
    }

    internal async Task AdvanceAsync(FaultPolicy candidate, CancellationToken token)
    {
        FaultPolicy policy;
        lock (_gate)
        {
            policy = _store.Current.Policies.Single(p => p.DeviceId == candidate.DeviceId);
            if (policy != candidate) return;
            if (policy.State == "AWAITING_RECOLLECTION")
            {
                try
                {
                    var actor = _catalog.GetActiveTaskActor(policy.Target.ActorUserId)
                        ?? throw new TaskScopeRevokedException("ACTOR_REVOKED");
                    _authorizer.Require(new(policy.Target, actor.UserId, "fault-policy-" + policy.FaultId), policy.Target.ProfileId, "FAULT_VERIFY");
                    var plan = policy.PlanId is null ? null : _catalog.GetTaskPlan(policy.PlanId);
                    if (plan?.State is "QUEUED" or "RUNNING" or "PARTIAL_RUNNING") return;
                    var verified = plan?.State == "SUCCEEDED" &&
                        Verify(policy.FaultId, actor, _scopes.Resolve(actor)).State is "RECOVERY_VERIFIED" or "ALREADY_RESOLVED";
                    SavePolicy(policy with { Enabled = false, NextRunMs = null,
                        State = verified ? "RECOVERY_VERIFIED" : plan?.State == "SUCCEEDED" ? "RECOLLECTED_NEEDS_VERIFY" : "RECOLLECTION_FAILED",
                        LastError = plan?.State == "SUCCEEDED" ? null : "TASK_PLAN_REVIEW_REQUIRED" });
                }
                catch (TaskScopeRevokedException ex)
                { SavePolicy(policy with { Enabled = false, NextRunMs = null, State = "BLOCKED", LastError = ex.Reason }); }
                return;
            }
            if (!policy.Enabled || policy.NextRunMs > Now()) return;
            var incident = _store.Current.Incidents.FirstOrDefault(i => i.Id == policy.FaultId);
            if (incident?.State == "RESOLVED" || incident is null)
            { SavePolicy(policy with { Enabled = false, NextRunMs = null, State = "NO_ACTIVE_FAULT" }); return; }
            if (policy.Attempts >= policy.MaximumAttempts)
            { SavePolicy(policy with { Enabled = false, NextRunMs = null, State = "ATTEMPT_LIMIT" }); return; }
            policy = policy with { State = "RUNNING", Attempts = policy.Attempts + 1, NextRunMs = Now() + policy.IntervalMinutes * 60000L };
            SavePolicy(policy); // Claim is durable before any network admission.
        }
        try
        {
            _authorizer.Require(new(policy.Target, policy.Target.ActorUserId, "fault-policy-" + policy.FaultId), policy.Target.ProfileId, "FAULT_AUTOMATION");
            var result = await _recovery.RecoverAsync(new(policy.DeviceId, policy.Target.ProfileId), policy.Target.ActorUserId, token, policy.Target);
            lock (_gate)
            {
                var latest = _store.Current.Policies.Single(p => p.DeviceId == policy.DeviceId);
                var retry = latest.Enabled && result.PlanId is null && result.Attempts.Count > 0 &&
                    OperationalRecoveryRunner.Transient(result.Attempts[^1].Status) && latest.Attempts < latest.MaximumAttempts;
                SavePolicy(latest with { Enabled = result.PlanId is not null ? latest.Enabled : retry,
                    NextRunMs = result.PlanId is not null ? latest.NextRunMs : retry ? Now() + latest.IntervalMinutes * 60000L : null,
                    State = result.PlanId is not null ? "AWAITING_RECOLLECTION" : retry ? "RETRY_WAIT" : "STOPPED",
                    PlanId = result.PlanId, RecoveryId = result.Id, LastError = result.PlanId is null ? result.Attempts.LastOrDefault()?.Status : null });
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            lock (_gate)
            {
                var latest = _store.Current.Policies.Single(p => p.DeviceId == policy.DeviceId);
                SavePolicy(latest with { Enabled = false, NextRunMs = null, State = "BLOCKED",
                    LastError = ex is TaskScopeRevokedException revoked ? revoked.Reason : ex is OperationCanceledException ? "CANCELLED" : "READ_ONLY_RECOVERY_FAILED" });
            }
        }
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private void Available() { if (_store.Unavailable) throw new InvalidOperationException("FAULT_STORE_UNAVAILABLE"); }
    private WebUserIdentity RequireActor(WebUserIdentity actor, P2B5SiteScopeDecision scope, bool write)
    {
        var current = _catalog.GetActiveTaskActor(actor.UserId);
        if (current is null || !scope.IsAuthorized || write && current.Role != "SUPERADMIN")
            throw new TaskScopeRevokedException("FAULT_SCOPE_DENIED");
        return current;
    }
    private static bool Visible(FaultIncident item, P2B5SiteScopeDecision scope) => DeviceSiteScope.Allows(scope, item.SiteId);
    private FaultIncident Find(string id, P2B5SiteScopeDecision scope)
    { Available(); return _store.Current.Incidents.FirstOrDefault(i => i.Id == id && Visible(i, scope)) ?? throw new KeyNotFoundException("FAULT_NOT_FOUND"); }
    private void Save(FaultIncident item) => _store.Commit(_store.Current with { Incidents = _store.Current.Incidents.Select(i => i.Id == item.Id ? item : i).ToArray() });
    private void SavePolicy(FaultPolicy item)
    {
        Available();
        var next = _store.Current.Policies.Where(p => p.DeviceId != item.DeviceId).Append(item).ToArray();
        if (next.Length > FaultCenterStore.MaximumPolicies) throw new InvalidOperationException("FAULT_POLICY_CAPACITY");
        _store.Commit(_store.Current with { Policies = next });
    }
}
