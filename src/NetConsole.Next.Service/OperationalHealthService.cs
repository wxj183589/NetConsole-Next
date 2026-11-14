using System.Text.Json;

namespace NetConsole.Next.Service;

internal sealed record OperationalMetric(string State, double? Value, string Unit, long? CapturedMs,
    IReadOnlyList<string> EvidenceIds, string? ResourceKey = null);
internal sealed record OperationalProbeWindow(long? LastConfirmedLossMs, long? RecoveryStartedMs,
    long? RecoveryConfirmedMs, IReadOnlyList<string> EvidenceIds, string? ResourceKey);
internal sealed record OperationalDeviceHealth(string DeviceId, string Name, string SiteId, string? ProfileId,
    string State, OperationalMetric Reachability, OperationalMetric CpuPercent, OperationalMetric MemoryPercent,
    OperationalMetric InterfaceDown, OperationalMetric LldpNeighbors, IReadOnlyList<string> Warnings,
    OperationalMetric? PingDelayMs = null, OperationalMetric? PacketLossPercent = null,
    OperationalMetric? RouteReachability = null, OperationalProbeWindow? ProbeWindow = null,
    IReadOnlyList<OperationalProbeWindow>? ProbeWindows = null);
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
    internal const int WirelessFaultResourcesPerScan = 10;

    internal static bool IsCollectionFailure(string state, string? captureStatus) =>
        state is not "CANCELLED" &&
        (state is "FAILED" or "TIMED_OUT" || captureStatus is "FAILED" or "PARTIAL");

    internal static IReadOnlyList<string> SelectWirelessFaultResourceIds(
        IReadOnlyList<string> orderedResourceIds, int start, int maximum = WirelessFaultResourcesPerScan)
    {
        if (maximum <= 0 || orderedResourceIds.Count == 0) return Array.Empty<string>();
        if (orderedResourceIds.Count <= maximum) return orderedResourceIds.ToArray();
        var offset = (int)(((long)start % orderedResourceIds.Count + orderedResourceIds.Count) % orderedResourceIds.Count);
        return Enumerable.Range(0, Math.Min(maximum, orderedResourceIds.Count))
            .Select(index => orderedResourceIds[(offset + index) % orderedResourceIds.Count]).ToArray();
    }

    internal static IReadOnlyList<OperationalFaultEvent> WirelessSyslogEvents(
        IReadOnlyList<SyslogEntry> entries, IReadOnlyList<DeviceInventoryItem> devices, long now)
        => WirelessSyslogEvents(entries, devices, [], now);

    internal static IReadOnlyList<OperationalFaultEvent> WirelessSyslogEvents(
        IReadOnlyList<SyslogEntry> entries, IReadOnlyList<DeviceInventoryItem> devices,
        IReadOnlyList<WirelessApResourceView> resources, long now)
    {
        var byAddress = devices.Where(device => string.Equals(device.DeviceType, "AC", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(device.ManagementIp))
            .GroupBy(device => device.ManagementIp, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);
        var result = new List<OperationalFaultEvent>();
        foreach (var entry in entries.Where(item => item.ReceivedMs <= now && now - item.ReceivedMs <= FreshnessWindowMs &&
                     item.EventType is "MESH_LINKDOWN" or "MESH_ACTIVELINK_SWITCH" or "MESH_LINKUP" &&
                     byAddress.ContainsKey(item.SenderIp)))
        {
            var device = byAddress[entry.SenderIp];
            var peer = MeshPeer(entry.Message);
            var exactResources = peer is null ? [] : resources.Where(resource =>
                resource.Observations.Any(observation => observation.SourceAcDeviceId == device.Id &&
                    observation.SourceSiteId == device.SiteId && observation.NormalizedMacSet.Contains(peer, StringComparer.Ordinal)))
                .ToArray();
            var resource = exactResources.Length == 1 ? $"ap:{exactResources[0].Id}" :
                $"mesh:{entry.SenderIp}:{peer ?? "UNKNOWN"}";
            var severity = entry.EventType == "MESH_LINKUP" ? "INFO" : "WARNING";
            var kind = entry.EventType switch
            {
                "MESH_LINKDOWN" => "WIRELESS_MESH_LINK_DOWN",
                "MESH_ACTIVELINK_SWITCH" => "WIRELESS_MESH_ACTIVE_LINK_SWITCH",
                _ => "WIRELESS_MESH_LINK_UP"
            };
            result.Add(new("syslog:" + entry.RawReference + ":" + entry.Line, device.Id, device.SiteId, kind,
                severity, "OBSERVED", entry.ReceivedMs, "SYSLOG", entry.RawReference + ":" + entry.Line,
                exactResources.Length == 1
                    ? $"已收到经 Raw 校验的无线 Mesh Syslog（{entry.EventType}）；已按发送 AC、站点和精确 MAC 关联 AP 资源。具体载荷请查看原始记录。"
                    : $"已收到经 Raw 校验的无线 Mesh Syslog（{entry.EventType}）；AP 身份未能形成唯一精确关联，具体载荷请查看原始记录。", resource,
                "syslog-local", "local"));
        }
        return result;
    }

    private static string? MeshPeer(string message)
    {
        var marker = message.IndexOf("peer MAC", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return null;
        var equals = message.IndexOf('=', marker);
        if (equals < 0) return null;
        var value = message[(equals + 1)..].TrimStart();
        var end = value.IndexOfAny([' ', ',', ';', '\r', '\n']);
        var candidate = end < 0 ? value : value[..end];
        var normalized = WirelessProjectionEngine.NormalizeMac(candidate);
        return normalized.Length == 0 ? null : normalized;
    }

    internal static HashSet<string>? CompleteLldpNeighbors(H3cFactSnapshot fact)
    {
        if (fact.Kind != "lldp.table" || !fact.CollectedRawVerified || !fact.IsCurrent ||
            fact.CapturedScopeKey == "UNSCOPED" || fact.Status is "FAILED" or "PARTIAL" or "EMPTY" or "UNSUPPORTED") return null;
        try
        {
            var table = JsonSerializer.Deserialize<H3cLldpListTable>(fact.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (table?.Status != "PARSED" || table.Neighbors is null ||
                table.Neighbors.Any(n => n is null || string.IsNullOrWhiteSpace(n.LocalInterface) ||
                    string.IsNullOrWhiteSpace(n.NeighborInterface) || string.IsNullOrWhiteSpace(n.ChassisId))) return null;
            return table.Neighbors.Select(n => $"{n.LocalInterface}|{n.NeighborInterface}|{n.ChassisId}")
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (JsonException) { return null; }
    }

    internal static IReadOnlyList<OperationalFaultEvent> LostLldpNeighbors(DeviceInventoryItem device,
        IReadOnlyList<H3cFactSnapshot> history, IReadOnlyList<H3cFactSnapshot> tables, long now)
    {
        var events = new List<OperationalFaultEvent>();
        foreach (var table in tables.Where(t => t.ProfileId == device.TerminalProfileId &&
            t.CapturedMs <= now && now - t.CapturedMs <= FreshnessWindowMs))
        {
            var neighbors = CompleteLldpNeighbors(table);
            if (neighbors is null) continue;
            foreach (var prior in history.Where(f => f.Kind == "lldp.neighbor" && f.CollectedRawVerified &&
                f.ProfileId == table.ProfileId && f.SourceAuthority == table.SourceAuthority &&
                f.CapturedScopeKey == table.CapturedScopeKey && f.CapturedMs < table.CapturedMs &&
                f.Status is not ("FAILED" or "PARTIAL" or "EMPTY" or "UNSUPPORTED"))
                .DistinctBy(f => f.IdentityKey))
            {
                if (neighbors.Contains(prior.IdentityKey)) continue;
                events.Add(new("lldp-lost:" + table.Id + ":" + prior.Id, device.Id, device.SiteId,
                    "LLDP_NEIGHBOR_LOST", "WARNING", "OBSERVED", table.CapturedMs, "FACT", table.Id,
                    "完整 LLDP 表已不再包含此前邻居；请检查邻居与接口，不据此判断设备离线。", prior.IdentityKey,
                    table.SourceAuthority, table.CapturedScopeKey));
            }
        }
        return events;
    }
    public static string ProbeResource(string protocol, string address, int? port) =>
        JsonSerializer.Serialize(new object?[] { protocol, address, port });
    internal static long? DiagnosticObservationMs(NetworkDiagnosticItemSnapshot item) =>
        item.Kind == "LONG_PING" && item.State == "RUNNING" && item.EndedMs is null && item.Outcome is null
            ? item.QuickPing?.Samples.LastOrDefault()?.CapturedMs
            : item.State is "SUCCEEDED" or "FAILED" ? item.EndedMs : null;
    internal static OperationalProbeWindow ProbeWindow(NetworkDiagnosticItemSnapshot item, string runId,
        DeviceInventoryItem device, long now)
    {
        var unknown = new OperationalProbeWindow(null, null, null, [], null);
        var active = item.Kind == "LONG_PING" && item.State == "RUNNING" && item.EndedMs is null && item.Outcome is null;
        if (item.Kind is not ("QUICK_PING" or "LONG_PING") || item.DeviceId != device.Id || item.SiteId != device.SiteId ||
            item.StartedMs is not { } started || DiagnosticObservationMs(item) is not { } ended || started > ended || ended > now ||
            now - ended > FreshnessWindowMs || item.QuickPing is not { Sent: > 0, Samples.Count: > 0 and <= 3600 } ping ||
            ping.DestinationAddress != device.ManagementIp ||
            !(ping.Protocol == "ICMP" && ping.Port is null || ping.Protocol == "TCP" && ping.Port == device.Port) ||
            !(active || item.State == "SUCCEEDED" && item.Outcome is "OK" or "LONG_PING_WINDOW_ENDED" && ping.Lost == 0 ||
              item.State == "FAILED" && item.Outcome == (item.Kind == "QUICK_PING" ? "QUICK_PING_LOSS" : "LONG_PING_LOSS") && ping.Lost > 0) ||
            ping.Sent != ping.Samples.Count || ping.RequestedSamples < ping.Sent ||
            item.ConfirmationStartSequence is < 1 or > 3602) return unknown;
        var losses = 0; var replies = 0; var received = 0; long? lossAt = null; var previousAt = started;
        for (var index = 0; index < ping.Samples.Count; index++)
        {
            var sample = ping.Samples[index];
            if (sample is null || sample.Sequence != index + 1 || sample.CapturedMs < previousAt || sample.CapturedMs > ended ||
                !(sample.Status == "REPLIED" && sample.LatencyMs is >= 0 ||
                  sample.Status is "TIMED_OUT" or "UNREACHABLE" && sample.LatencyMs is null)) return unknown;
            previousAt = sample.CapturedMs;
            if (sample.Status == "REPLIED") received++;
            // Older samples remain cumulative statistics, but cannot confirm a current fault or recovery.
            if (sample.Sequence < item.ConfirmationStartSequence || now - sample.CapturedMs > FreshnessWindowMs)
            { losses = 0; replies = 0; continue; }
            if (sample.Status == "REPLIED") { replies++; losses = 0; }
            else { losses++; replies = 0; if (losses >= 3) lossAt = sample.CapturedMs; }
        }
        if (ping.Received != received || ping.Lost != ping.Sent - received) return unknown;
        return new(lossAt, replies >= 3 ? ping.Samples[^3].CapturedMs : null,
            replies >= 3 ? ping.Samples[^1].CapturedMs : null, [runId], ProbeResource(ping.Protocol, ping.DestinationAddress, ping.Port));
    }
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
        var emptyLldpTables = fresh.Where(f => CompleteLldpNeighbors(f) is { Count: 0 }).ToArray();
        var lldpEvidence = lldp.Length > 0 ? lldp : emptyLldpTables;
        var reachable = fresh.Length == 0 ? Unknown("state") : new OperationalMetric("OBSERVED", 1, "state",
            fresh.Max(f => f.CapturedMs), fresh.Select(f => f.Id).Take(10).ToArray());
        var state = !device.Enabled ? "DISABLED" : !device.IsValid || down > 0 ? "ATTENTION" :
            fresh.Length > 0 ? "OBSERVED" : "UNKNOWN";
        return new(device.Id, device.Name, device.SiteId, device.TerminalProfileId, state, reachable,
            Unknown("%"), Unknown("%"), interfaceMetric,
            lldpEvidence.Length == 0 ? Unknown("neighbors") : new("OBSERVED", lldp.Length, "neighbors",
                lldpEvidence.Max(f => f.CapturedMs), lldpEvidence.Select(f => f.Id).ToArray()), warnings);
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

internal sealed class OperationalHealthService(Catalog catalog, WirelessReadModelService wireless, NetworkDiagnosticsService diagnostics,
    SimulatorConfigurationService? configurations = null)
{
    private int _wirelessFaultScanCursor;

    internal CompleteConfigurationDrift? ConfigurationDrift(string profileId, P2B5SiteScopeDecision scope)
    {
        if (configurations is null || !scope.IsAuthorized) return null;
        try
        {
            // Internal read-only monitor: returns only verified metadata, never Raw or a write session.
            // DetectDrift retains its exact simulator endpoint, inventory and Site guards.
            return configurations.DetectDrift(profileId,
                new WebUserIdentity("fault-monitor", "fault-monitor", "SUPERADMIN", "fault-monitor"), scope);
        }
        catch (Exception ex) when (ex is ConfigRestoreException or IOException or UnauthorizedAccessException)
        { return null; }
    }

    public OperationalHealthSnapshot Get(P2B5SiteScopeDecision scope, int page = 1, string? actorUserId = null)
    {
        if (!scope.IsAuthorized) throw new TaskScopeRevokedException("OPERATIONAL_SCOPE_DENIED");
        page = Math.Clamp(page, 1, 100000);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var inventory = catalog.QueryManagedDeviceInventory(page, 25, scope: scope);
        var devices = new List<OperationalDeviceHealth>();
        var events = new List<OperationalFaultEvent>();
        var limitations = new List<string>();
        var visible = inventory.Items.ToDictionary(d => d.Id, StringComparer.Ordinal);
        var diagnosticItems = actorUserId is null ? [] : diagnostics.List(actorUserId, scope, 20)
            .SelectMany(run => (diagnostics.Get(run.Id, actorUserId, scope)?.Items ?? [])
                .Select(item => (RunId: run.Id, Item: item)))
            .Where(pair => (pair.Item.QuickPing is not null || pair.Item.TraceRoute is not null) &&
                OperationalHealthProjection.DiagnosticObservationMs(pair.Item) is { } at && at <= now &&
                now - at <= OperationalHealthProjection.FreshnessWindowMs)
            .OrderByDescending(pair => OperationalHealthProjection.DiagnosticObservationMs(pair.Item)).ToArray();
        foreach (var device in inventory.Items)
        {
            if (device.TerminalProfileId is { } configurationProfile &&
                CompleteConfigurationFaultProjection.Changed(ConfigurationDrift(configurationProfile, scope), device, now) is { } change)
                events.Add(change);
            var facts = device.TerminalProfileId is { } profile
                ? catalog.QueryH3cFacts(profile, limit: 1000, scope: scope, requireCapturedScope: true) : [];
            var health = OperationalHealthProjection.Device(device, facts, now);
            if (device.TerminalProfileId is { } lldpProfile)
                events.AddRange(OperationalHealthProjection.LostLldpNeighbors(device,
                    catalog.QueryLatestLldpNeighborHistory(lldpProfile, scope),
                    catalog.QueryH3cFacts(lldpProfile, "lldp.table", limit: 1000, scope: scope, requireCapturedScope: true), now));
            var pingEvidence = diagnosticItems.Where(pair => pair.Item.DeviceId == device.Id && pair.Item.SiteId == device.SiteId &&
                pair.Item.QuickPing is { } sample && sample.DestinationAddress == device.ManagementIp &&
                (sample.Protocol == "ICMP" && sample.Port is null || sample.Protocol == "TCP" && sample.Port == device.Port))
                .DistinctBy(pair => OperationalHealthProjection.ProbeResource(pair.Item.QuickPing!.Protocol,
                    pair.Item.QuickPing.DestinationAddress, pair.Item.QuickPing.Port)).ToArray();
            var windows = new List<OperationalProbeWindow>();
            foreach (var evidence in pingEvidence)
            {
                var ping = evidence.Item;
                var sample = ping.QuickPing!;
                var window = OperationalHealthProjection.ProbeWindow(ping, evidence.RunId, device, now);
                if (window.ResourceKey is null) continue;
                windows.Add(window);
                // The scalar display remains the latest valid resource; recovery keeps every exact protocol/port partition.
                if (windows.Count == 1) health = health with
                {
                    PingDelayMs = new(sample.AverageMs is null ? "UNKNOWN" : "OBSERVED", sample.AverageMs, "ms",
                        sample.Samples[^1].CapturedMs, [evidence.RunId], window.ResourceKey),
                    PacketLossPercent = new("OBSERVED", sample.LossPercent, "%", sample.Samples[^1].CapturedMs,
                        [evidence.RunId], window.ResourceKey),
                    ProbeWindow = window
                };
                if (window.LastConfirmedLossMs is { } lossAt)
                    events.Add(new("loss:" + ping.Id, device.Id, device.SiteId, "PROBE_PACKET_LOSS", "WARNING", "OBSERVED",
                        lossAt, "DIAGNOSTIC", evidence.RunId, $"{sample.Protocol} 连续 3 次有效探测丢失；不据此断定设备离线。",
                        window.ResourceKey));
            }
            health = health with { ProbeWindows = windows };
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
            if (OperationalHealthProjection.IsCollectionFailure(run.State, run.CaptureStatus))
                events.Add(new("task:" + run.Id, device.Id, device.SiteId, "COLLECTION_FAILURE", "WARNING", run.State,
                    run.EndedMs ?? run.StartedMs ?? run.QueuedMs ?? now, "TASK_RUN", run.Id,
                    "采集任务未完整完成；按完整采集计划分别核验，不据此判定设备离线。", FaultLifecycle.CollectionResource(definition)));
        }
        // Resolve AP identities from the same scoped projection used by the
        // wireless workbench before publishing Syslog events. A missing or
        // ambiguous MAC remains an explicit UNKNOWN resource.
        var wirelessSnapshot = wireless.GetSnapshot(scope);
        var wirelessResources = wirelessSnapshot.ApResources
            .Where(r => r.Observations.Any(o => visible.ContainsKey(o.SourceAcDeviceId)))
            .OrderBy(r => r.Id, StringComparer.Ordinal).ToArray();
        try
        {
            var syslog = catalog.QuerySyslog(new(FromMs: now - OperationalHealthProjection.FreshnessWindowMs,
                ToMs: now, Limit: 500), scope);
            events.AddRange(OperationalHealthProjection.WirelessSyslogEvents(syslog.Items, inventory.Items, wirelessResources, now));
            if (syslog.Partial) limitations.Add("无线 Syslog 事件仅使用已完成校验的 sealed Raw 分段；部分分段不可读时保留已读事件并标记 PARTIAL。");
        }
        catch (Exception ex) when (ex is TaskScopeRevokedException or IOException or UnauthorizedAccessException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
        { limitations.Add($"无线 Syslog 事件暂不可用（{ex.GetType().Name}）；不以缺失日志推断故障。"); }
        // Reuse the same scoped, verified wireless runtime that drives the AP
        // workbench. Keep source identity and Raw references in its drill-down.
        var scanStart = Interlocked.Add(ref _wirelessFaultScanCursor, OperationalHealthProjection.WirelessFaultResourcesPerScan) -
            OperationalHealthProjection.WirelessFaultResourcesPerScan;
        var selectedIds = OperationalHealthProjection.SelectWirelessFaultResourceIds(
            wirelessResources.Select(r => r.Id).ToArray(), scanStart);
        var resourcesById = wirelessResources.ToDictionary(r => r.Id, StringComparer.Ordinal);
        foreach (var resourceId in selectedIds)
        {
            var resource = resourcesById[resourceId];
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
            new[] { "仅汇总当前页授权设备；最多读取 100 项近期任务、每设备 1000 条 Current Facts、显示 100 项事件。",
             "CPU、内存和缺少证据的指标为 UNKNOWN；接口和 LLDP 仅代表已观测条目。",
             "Ping 仅读取最近 20 个授权诊断任务中 5 分钟内的有效采样，含运行中的 Long Ping；暂停和取消不提供确认或恢复证据，同协议与端口采用最新任务。无线故障每次检查本页最多 10 个 AP，并在后续调用间轮转。",
             "Ping 连续 3 次有效丢失确认丢包事件；末尾连续 3 次有效应答且均晚于事件才可核验恢复，累计丢包率仍保留。",
             "LLDP 消失仅对照同来源与捕获范围的最近 1000 个独立邻居身份的最后观测和完整 Current 表；身份超限会遗漏事件。",
             "配置变化仅对授权模拟器的连续完整备份检测，最新备份须在 5 分钟内；UNKNOWN、损坏及缺失版本不确认变化或稳定。",
             "连接会话关闭或采集失败不代表设备离线。" }.Concat(limitations).Distinct(StringComparer.Ordinal).ToArray());
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
        string actorUserId, CancellationToken cancellationToken, TaskScopeSnapshot? expectedTarget = null, bool includeLldp = false)
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
                    includeLldp ? ["display version", "display interface brief", H3cCommandPolicy.DisplayLldpNeighborInformationList]
                        : ["display version", "display interface brief"]), actorUserId,
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
