using System.Text.Json;

namespace NetConsole.Next.Service;

internal sealed record OperationalMetric(string State, double? Value, string Unit, long? CapturedMs,
    IReadOnlyList<string> EvidenceIds, string? ResourceKey = null);
internal sealed record OperationalDeviceHealth(string DeviceId, string Name, string SiteId, string? ProfileId,
    string State, OperationalMetric Reachability, OperationalMetric CpuPercent, OperationalMetric MemoryPercent,
    OperationalMetric InterfaceDown, OperationalMetric LldpNeighbors, IReadOnlyList<string> Warnings,
    OperationalMetric? PingDelayMs = null, OperationalMetric? PacketLossPercent = null,
    OperationalMetric? RouteReachability = null);
internal sealed record OperationalFaultEvent(string Id, string DeviceId, string SiteId, string Kind,
    string Severity, string State, long AtMs, string EvidenceType, string EvidenceId, string Detail,
    string? ResourceKey = null, string? SourceAuthority = null, string? CapturedScopeKey = null);
internal sealed record OperationalHealthSnapshot(long AsOfMs, long FreshnessWindowMs, int Page, long TotalDevices,
    bool HasMore, IReadOnlyList<OperationalDeviceHealth> Devices, IReadOnlyList<OperationalFaultEvent> Events,
    IReadOnlyList<string> Limitations);
internal sealed record OperationalRecoveryRequest(string DeviceId, string ExpectedProfileId);
internal sealed record OperationalRecoveryAttempt(int Ordinal, string Status, long AtMs);
internal sealed record OperationalRecoveryReport(string Id, string DeviceId, string ProfileId, string SiteId,
    string ActorUserId, long StartedMs, long CompletedMs, string State,
    IReadOnlyList<OperationalRecoveryAttempt> Attempts, string? PlanId, string? RecollectionState);

// This projection never infers device offline from an unsuccessful collection.
// Verified Raw-backed observations are separate from an authenticated terminal's state.
internal static class OperationalHealthProjection
{
    public const long FreshnessWindowMs = 300_000;
    public static string ProbeResource(string protocol, string address, int? port) =>
        JsonSerializer.Serialize(new object?[] { protocol, address, port });
    public static OperationalMetric RouteMetric(NetworkDiagnosticItemSnapshot item, string runId,
        DeviceInventoryItem device, long now)
    {
        if (item.Kind != "TRACEROUTE" || item.DeviceId != device.Id || item.SiteId != device.SiteId ||
            item.State is not ("SUCCEEDED" or "FAILED") || item.EndedMs is not { } at || at > now ||
            now - at > FreshnessWindowMs || item.TraceRoute is not { Hops.Count: > 0 } trace ||
            trace.DestinationAddress != device.ManagementIp) return Unknown("route");
        var reached = item.State == "SUCCEEDED" && item.Outcome == "OK" && trace.DestinationReached;
        var failed = item.State == "FAILED" && item.Outcome is "TRACEROUTE_UNREACHABLE" or "TRACEROUTE_MAX_HOPS" && !trace.DestinationReached;
        return reached || failed ? new("OBSERVED", reached ? 1 : 0, "route", at, [runId],
            ProbeResource("TRACEROUTE", trace.DestinationAddress, null)) : Unknown("route");
    }
    public static OperationalDeviceHealth Device(DeviceInventoryItem device,
        IReadOnlyList<H3cFactSnapshot> facts, long now)
    {
        var verified = facts.Where(f => f.CollectedRawVerified && f.IsCurrent && f.CapturedMs <= now &&
            f.Status is not ("FAILED" or "PARTIAL" or "UNSUPPORTED" or "EMPTY")).ToArray();
        var fresh = verified.Where(f => now - f.CapturedMs <= FreshnessWindowMs).ToArray();
        var warnings = new List<string>();
        if (!device.IsValid) warnings.Add("INVENTORY_QUALITY_ISSUE");
        if (verified.Length > 0 && fresh.Length == 0) warnings.Add("VERIFIED_EVIDENCE_STALE");
        if (fresh.Length == 0) warnings.Add("FRESH_VERIFIED_EVIDENCE_UNAVAILABLE");
        var interfaces = fresh.Where(f => f.Kind == "interface").ToArray();
        var states = interfaces.Select(f => (Fact: f, State: LinkState(f.PayloadJson))).ToArray();
        var known = states.Where(f => f.State is not null).ToArray();
        var down = known.Count(f => f.State == "DOWN");
        // Missing/truncated interface observations cannot prove a total of zero faults.
        var interfaceMetric = known.Length == 0 ? Unknown("interfaces") : new OperationalMetric(
            "OBSERVED", down, "interfaces", known.Max(f => f.Fact.CapturedMs), known.Select(f => f.Fact.Id).ToArray());
        var lldp = fresh.Where(f => f.Kind == "lldp.neighbor").ToArray();
        var reachable = fresh.Length == 0 ? Unknown("state") : new OperationalMetric("OBSERVED", 1, "state",
            fresh.Max(f => f.CapturedMs), fresh.Select(f => f.Id).Take(10).ToArray());
        var state = !device.Enabled ? "DISABLED" : !device.IsValid || down > 0 ? "ATTENTION" :
            fresh.Length > 0 ? "OBSERVED" : "UNKNOWN";
        return new(device.Id, device.Name, device.SiteId, device.TerminalProfileId, state, reachable,
            Unknown("%"), Unknown("%"), interfaceMetric,
            lldp.Length == 0 ? Unknown("neighbors") : new("OBSERVED", lldp.Length, "neighbors",
                lldp.Max(f => f.CapturedMs), lldp.Select(f => f.Id).ToArray()), warnings);
    }

    public static string? LinkState(string json)
    {
        try
        {
            using var payload = JsonDocument.Parse(json);
            if (!payload.RootElement.TryGetProperty("linkStatus", out var value) || value.ValueKind != JsonValueKind.String) return null;
            return value.GetString()?.Trim().ToUpperInvariant() switch
            { "UP" => "UP", "DOWN" or "ADM" or "ADMIN DOWN" or "ADMINISTRATIVELY DOWN" => "DOWN", _ => null };
        }
        catch (JsonException) { return null; }
    }
    private static OperationalMetric Unknown(string unit) => new("UNKNOWN", null, unit, null, []);
}

internal sealed class OperationalHealthService(Catalog catalog, WirelessReadModelService wireless, NetworkDiagnosticsService diagnostics)
{
    public OperationalHealthSnapshot Get(P2B5SiteScopeDecision scope, int page = 1, string? actorUserId = null)
    {
        if (!scope.IsAuthorized) throw new TaskScopeRevokedException("OPERATIONAL_SCOPE_DENIED");
        page = Math.Clamp(page, 1, 100000);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var inventory = catalog.QueryManagedDeviceInventory(page, 25, scope: scope);
        var devices = new List<OperationalDeviceHealth>();
        var events = new List<OperationalFaultEvent>();
        var visible = inventory.Items.ToDictionary(d => d.Id, StringComparer.Ordinal);
        var diagnosticItems = actorUserId is null ? [] : diagnostics.List(actorUserId, scope, 20)
            .SelectMany(run => (diagnostics.Get(run.Id, actorUserId, scope)?.Items ?? [])
                .Select(item => (RunId: run.Id, Item: item)))
            .Where(pair => (pair.Item.QuickPing is not null || pair.Item.TraceRoute is not null) && pair.Item.EndedMs is { } end && end <= now &&
                now - end <= OperationalHealthProjection.FreshnessWindowMs && pair.Item.State is "SUCCEEDED" or "FAILED")
            .OrderByDescending(pair => pair.Item.EndedMs).ToArray();
        foreach (var device in inventory.Items)
        {
            var facts = device.TerminalProfileId is { } profile
                ? catalog.QueryH3cFacts(profile, limit: 1000, scope: scope, requireCapturedScope: true) : [];
            var health = OperationalHealthProjection.Device(device, facts, now);
            var pingEvidence = diagnosticItems.FirstOrDefault(pair => pair.Item.DeviceId == device.Id && pair.Item.SiteId == device.SiteId &&
                pair.Item.QuickPing is { } sample && sample.DestinationAddress == device.ManagementIp &&
                (sample.Protocol != "TCP" || sample.Port == device.Port));
            var ping = pingEvidence.Item;
            if (ping?.QuickPing is { Sent: > 0 } sample)
            {
                health = health with
                {
                    PingDelayMs = new(sample.AverageMs is null ? "UNKNOWN" : "OBSERVED", sample.AverageMs, "ms", ping.EndedMs, [pingEvidence.RunId]),
                    PacketLossPercent = new("OBSERVED", sample.LossPercent, "%", ping.EndedMs, [pingEvidence.RunId],
                        OperationalHealthProjection.ProbeResource(sample.Protocol, sample.DestinationAddress, sample.Port))
                };
                if (sample.LossPercent > 0)
                    events.Add(new("loss:" + ping.Id, device.Id, device.SiteId, "PROBE_PACKET_LOSS", "WARNING", "OBSERVED",
                        ping.EndedMs!.Value, "DIAGNOSTIC", pingEvidence.RunId, $"{sample.Protocol} 探测样本存在丢失；不据此断定设备离线。",
                        OperationalHealthProjection.ProbeResource(sample.Protocol, sample.DestinationAddress, sample.Port)));
            }
            var routeEvidence = diagnosticItems.FirstOrDefault(pair => pair.Item.Kind == "TRACEROUTE" &&
                pair.Item.DeviceId == device.Id && pair.Item.SiteId == device.SiteId &&
                pair.Item.TraceRoute?.DestinationAddress == device.ManagementIp);
            if (routeEvidence.Item is { } route)
            {
                var metric = OperationalHealthProjection.RouteMetric(route, routeEvidence.RunId, device, now);
                health = health with { RouteReachability = metric };
                if (metric is { State: "OBSERVED", Value: 0 })
                    events.Add(new("route:" + route.Id, device.Id, device.SiteId, "TRACEROUTE_ANOMALY", "WARNING", "OBSERVED",
                        route.EndedMs!.Value, "DIAGNOSTIC", routeEvidence.RunId,
                        route.Outcome == "TRACEROUTE_UNREACHABLE" ? "路由探测返回不可达；请查看具体跳点。" : "路由探测达到跳数上限，未取得目标应答。",
                        metric.ResourceKey));
            }
            devices.Add(health);
            foreach (var fact in facts.Where(f => f.IsCurrent && f.CollectedRawVerified &&
                f.CapturedMs <= now && now - f.CapturedMs <= OperationalHealthProjection.FreshnessWindowMs &&
                f.Status is not ("FAILED" or "PARTIAL" or "UNSUPPORTED" or "EMPTY") &&
                f.Kind == "interface" && OperationalHealthProjection.LinkState(f.PayloadJson) == "DOWN"))
                events.Add(new("interface:" + fact.Id, device.Id, device.SiteId, "INTERFACE_DOWN", "WARNING",
                    "OBSERVED", fact.CapturedMs, "FACT", fact.Id, "接口链路观测为 DOWN；管理关闭也可能产生此状态。", fact.IdentityKey,
                    fact.SourceAuthority, fact.CapturedScopeKey));
            if (health.Warnings.Contains("VERIFIED_EVIDENCE_STALE"))
            {
                var latest = facts.Where(f => f.CollectedRawVerified && f.CapturedMs <= now).MaxBy(f => f.CapturedMs)!;
                events.Add(new("stale:" + latest.Id, device.Id, device.SiteId, "EVIDENCE_STALE", "WARNING", "UNKNOWN",
                    latest.CapturedMs, "FACT", latest.Id, "采集证据已超出新鲜度窗口，当前设备状态未知。", null,
                    latest.SourceAuthority, latest.CapturedScopeKey));
            }
        }
        foreach (var run in catalog.RecentTaskRuns(100, scope))
        {
            var definition = run.DefinitionId is null ? null : catalog.GetTaskDefinition(run.DefinitionId);
            if (definition?.ScopeSnapshot is not { } frozen || !visible.TryGetValue(frozen.DeviceId, out var device) ||
                device.TerminalProfileId != frozen.ProfileId || device.SiteId != frozen.SiteId) continue;
            if (run.State is "FAILED" or "TIMED_OUT" or "CANCELLED" || run.CaptureStatus is "FAILED" or "PARTIAL")
                events.Add(new("task:" + run.Id, device.Id, device.SiteId, "COLLECTION_FAILURE", "WARNING", run.State,
                    run.EndedMs ?? run.StartedMs ?? run.QueuedMs ?? now, "TASK_RUN", run.Id,
                    "采集任务未完整完成；不据此判定设备离线。"));
        }
        // Reuse the same scoped, verified wireless runtime that drives the AP
        // workbench. Keep source identity and Raw references in its drill-down.
        var wirelessSnapshot = wireless.GetSnapshot(scope);
        foreach (var resource in wirelessSnapshot.ApResources.Where(r => r.Observations.Any(o => visible.ContainsKey(o.SourceAcDeviceId))).Take(10))
        {
            var runtime = wireless.GetRuntimeState(resource.Id, scope, nowMs: now);
            var live = runtime?.Sources.Where(s => s.Current?.VerificationLevel == "LIVE_VERIFIED" &&
                s.Current.CapturedMs <= now && now - s.Current.CapturedMs <= runtime.FreshnessWindowMs &&
                s.LatestOutcome == "SUCCESS").ToArray() ?? [];
            if (live.Length == 0 || live.Length != runtime!.Sources.Count) continue;
            var evidence = live.OrderByDescending(s => s.Current!.CapturedMs).First();
            if (runtime.NormalizedState is "OFFLINE" or "CONFLICT")
                events.Add(new("ap:" + resource.Id + ":" + runtime.SourceRevision, evidence.SourceAcDeviceId,
                    evidence.SourceSiteId, "AP_" + runtime.NormalizedState, "WARNING", runtime.NormalizedState,
                    evidence.Current!.CapturedMs, "WIRELESS_RUNTIME", resource.Id,
                    $"AP {resource.DisplayName}：{runtime.NormalizedState}；依据授权 AC 的有效状态证据。", resource.Id));
            foreach (var change in (runtime.Recent10 ?? []).Where(c => c.CapturedMs <= now &&
                now - c.CapturedMs <= runtime.FreshnessWindowMs).Take(5))
                events.Add(new("ap-change:" + change.Id, evidence.SourceAcDeviceId, evidence.SourceSiteId,
                    "AP_EFFECTIVE_STATE_CHANGED", "INFO", change.NormalizedState, change.CapturedMs,
                    "WIRELESS_RUNTIME", resource.Id,
                    $"AP {resource.DisplayName}：{change.PreviousState} → {change.NormalizedState}；有效状态变化，不将缺采样视为切换。", resource.Id));
        }
        return new(now, OperationalHealthProjection.FreshnessWindowMs, page, inventory.Total, inventory.HasMore,
            devices, events.OrderByDescending(e => e.AtMs).Take(100).ToArray(),
            ["仅汇总当前页授权设备；最多读取 100 项近期任务、每设备 1000 条 Current Facts、显示 100 项事件。",
             "CPU、内存和缺少证据的指标为 UNKNOWN；接口和 LLDP 仅代表已观测条目。",
             "Ping 仅读取最近 20 个授权诊断任务中 5 分钟内完成的真实采样；无线事件最多检查本页 AC 关联的 10 个 AP。",
             "连接会话关闭或采集失败不代表设备离线。"]);
    }
}

internal static class OperationalRecoveryRunner
{
    public static bool Connected(string status) => status is "CONNECTED" or "DEVICE_INFO_UNRECOGNIZED";
    public static bool Transient(string status) => status is "CONNECT_TIMEOUT" or "UNREACHABLE" or "CONNECTION_FAILED";
    public static async Task<IReadOnlyList<OperationalRecoveryAttempt>> ProbeAsync(Func<Task<string>> probe,
        Action require, CancellationToken cancellationToken, Func<int, CancellationToken, Task>? delay = null)
    {
        var attempts = new List<OperationalRecoveryAttempt>();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            require(); // Actor, scope, device binding and endpoint must still match every retry.
            var status = await probe();
            cancellationToken.ThrowIfCancellationRequested();
            attempts.Add(new(attempt, status, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            if (Connected(status) || !Transient(status) || attempt == 3) break;
            await (delay ?? ((n, token) => Task.Delay(TimeSpan.FromSeconds(n), token)))(attempt, cancellationToken);
        }
        return attempts;
    }
}

// Recovery performs bounded login probes and an existing allowlisted collection
// plan. It never calls a Writer. The existing durable task plan is the evidence
// authority for recollection; a successful login alone is not business recovery.
internal sealed class OperationalRecoveryService(Catalog catalog, TaskSiteScopeAuthorizer authorizer,
    P2B5SiteScopeStore scopes, H3cConnectionTestService connection, H3cBatchTaskService tasks)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<OperationalRecoveryReport> RecoverAsync(OperationalRecoveryRequest request,
        string actorUserId, CancellationToken cancellationToken, TaskScopeSnapshot? expectedTarget = null)
    {
        if (!await _gate.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("OPERATIONAL_RECOVERY_BUSY");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(120));
            cancellationToken = timeout.Token;
            var actor = catalog.GetActiveTaskActor(actorUserId) ?? throw new TaskScopeRevokedException("TASK_ACTOR_REVOKED");
            var scope = scopes.Resolve(actor);
            var detail = catalog.GetDeviceInventoryDetail(request.DeviceId, scope);
            if (detail is null || string.IsNullOrWhiteSpace(request.ExpectedProfileId) ||
                detail.TerminalProfileId != request.ExpectedProfileId)
                throw new TaskScopeRevokedException("OPERATIONAL_TARGET_CHANGED");
            var frozen = expectedTarget ?? authorizer.CaptureDefinition(request.ExpectedProfileId, actorUserId);
            if (frozen.DeviceId != request.DeviceId || frozen.ProfileId != request.ExpectedProfileId || frozen.ActorUserId != actorUserId)
                throw new TaskScopeRevokedException("OPERATIONAL_TARGET_CHANGED");
            var id = "recovery-" + Guid.NewGuid().ToString("N");
            var execution = new TaskExecutionScope(frozen, actorUserId, id);
            var started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            catalog.TryRecordDeviceAuditEvent(actorUserId, "OPERATIONAL_RECOVERY", "STARTED", request.DeviceId,
                null, [], id);
            var probeOrdinal = 0;
            var attempts = await OperationalRecoveryRunner.ProbeAsync(async () =>
                {
                    var status = (await connection.TestAsync(frozen.ProfileId, cancellationToken,
                        () => authorizer.Require(execution, frozen.ProfileId, "RECOVERY_TRANSPORT")))?.Status ?? "TARGET_UNAVAILABLE";
                    catalog.TryRecordDeviceAuditEvent(actorUserId, "OPERATIONAL_RECOVERY_PROBE", status, request.DeviceId,
                        null, [], $"{id};attempt={++probeOrdinal}");
                    return status;
                },
                () => authorizer.Require(execution, frozen.ProfileId, "RECOVERY_PROBE"), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            authorizer.Require(execution, frozen.ProfileId, "RECOVERY_RECOLLECTION");
            TaskPlanSnapshot? plan = null;
            if (OperationalRecoveryRunner.Connected(attempts[^1].Status))
                plan = tasks.CreateAndSchedule(new(id, [frozen.ProfileId], "display version", 120000,
                    ["display version", "display interface brief"]), actorUserId,
                    new Dictionary<string, string> { [frozen.ProfileId] = frozen.DeviceId });
            var state = plan is null ? "CONNECTION_NOT_RECOVERED" : plan.Items.Any(i => i.RunId is not null)
                ? "RECOLLECTION_QUEUED" : "RECOLLECTION_NOT_QUEUED";
            catalog.TryRecordDeviceAuditEvent(actorUserId, "OPERATIONAL_RECOVERY", state, request.DeviceId,
                null, [], $"{id};attempts={attempts.Count};connection={attempts[^1].Status};plan={plan?.Id}");
            return new(id, frozen.DeviceId, frozen.ProfileId, frozen.SiteId, actorUserId, started,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), state, attempts, plan?.Id, plan?.State);
        }
        catch (OperationCanceledException)
        {
            catalog.TryRecordDeviceAuditEvent(actorUserId, "OPERATIONAL_RECOVERY", "CANCELLED", request.DeviceId, null, [], "Read-only recovery cancelled.");
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            catalog.TryRecordDeviceAuditEvent(actorUserId, "OPERATIONAL_RECOVERY", "FAILED", request.DeviceId,
                null, [], ex is TaskScopeRevokedException revoked ? revoked.Reason : ex.GetType().Name);
            throw;
        }
        finally { _gate.Release(); }
    }
}
