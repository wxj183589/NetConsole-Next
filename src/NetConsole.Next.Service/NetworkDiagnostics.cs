using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace NetConsole.Next.Service;

internal sealed record NetworkDiagnosticBatchRequest(
    IReadOnlyList<string>? DeviceIds,
    IReadOnlyList<string>? Kinds,
    int TimeoutMs = 2_000,
    string? SiteId = null,
    int SampleCount = 10, int IntervalMs = 250, string QuickPingProtocol = "ICMP", int LongPingDurationSeconds = 60);

internal sealed record NetworkDiagnosticItemSnapshot(
    string Id,
    string DeviceId,
    string DeviceName,
    string SiteId,
    string Kind,
    string State,
    string? Outcome,
    string? Detail,
    long? StartedMs,
    long? EndedMs,
    long? LatencyMs,
    int EvidenceCount,
    IReadOnlyList<string> EvidenceSources,
    NetworkTracerouteSnapshot? TraceRoute = null,
    QuickPingSnapshot? QuickPing = null,
    NetworkDnsSnapshot? Dns = null,
    int ConfirmationStartSequence = 1);

internal sealed record NetworkDnsSnapshot(string Query, string ResolutionMode,
    IReadOnlyList<string> Addresses, long CapturedMs);

internal sealed record NetworkDiagnosticRunSnapshot(
    string Id,
    string Actor,
    string State,
    string? SiteId,
    int TimeoutMs,
    int TotalDevices,
    int TotalChecks,
    int CompletedChecks,
    int SucceededChecks,
    int FailedChecks,
    int NoEvidenceChecks,
    int CancelledChecks,
    int QueuedChecks,
    int RunningChecks,
    int ProgressPercent,
    long CreatedMs,
    long? StartedMs,
    long? EndedMs,
    string? FailureReason,
    IReadOnlyList<NetworkDiagnosticItemSnapshot> Items)
{
    public int PausedChecks => Items?.Count(item => item?.State == "PAUSED") ?? 0;
}

internal sealed record NetworkDiagnosticRunSummary(
    string Id, string State, int TotalDevices, int TotalChecks, int CompletedChecks,
    int ProgressPercent, long CreatedMs, long? StartedMs, long? EndedMs,
    IReadOnlyList<string> Kinds, IReadOnlyList<NetworkDiagnosticTargetSummary>? Targets = null);

internal sealed record NetworkDiagnosticTargetSummary(string DeviceId, string DeviceName, string SiteId);

internal sealed class NetworkDiagnosticsService : IDisposable
{
    private static readonly HashSet<string> SupportedKinds = new(StringComparer.OrdinalIgnoreCase)
        { "PING", "TCP", "DNS", "LLDP", "TRACEROUTE", "QUICK_PING", "LONG_PING" };
    private const int MaximumDevices = 128;
    private const int MaximumChecks = 512;
    private const int MaximumRuns = 128;
    private const int MaximumConcurrency = 4;
    private const int MaximumHistoryBytes = 4 * 1024 * 1024;
    private readonly Catalog _catalog;
    private readonly INetworkTracerouteProbe _tracerouteProbe;
    private readonly IQuickPingProbe _quickPingProbe;
    private readonly string _historyRoot;
    private readonly object _gate = new();
    private readonly Dictionary<string, DiagnosticRun> _runs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NetworkDiagnosticRunSnapshot> _history = new(StringComparer.Ordinal);
    private bool _disposed;

    public NetworkDiagnosticsService(Catalog catalog) : this(catalog, new IcmpNetworkTracerouteProbe()) { }

    internal NetworkDiagnosticsService(Catalog catalog, INetworkTracerouteProbe tracerouteProbe, IQuickPingProbe? quickPingProbe = null)
    {
        _catalog = catalog;
        _tracerouteProbe = tracerouteProbe;
        _quickPingProbe = quickPingProbe ?? new QuickPingProbe();
        _historyRoot = Path.Combine(catalog.DataRoot, "diagnostics");
        LoadHistory();
    }

    public NetworkDiagnosticRunSnapshot Start(NetworkDiagnosticBatchRequest request, string actor,
        P2B5SiteScopeDecision scope)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!scope.IsAuthorized) throw new DeviceSiteScopeException("P2B_SITE_SCOPE_DENIED", scope.Detail);
        if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("A task actor is required.", nameof(actor));
        var deviceIds = NormalizeIds(request.DeviceIds);
        if (deviceIds.Count is < 1 or > MaximumDevices)
            throw new ArgumentException($"A diagnostic batch must contain 1-{MaximumDevices} device IDs.", nameof(request));
        var requestedKinds = request.Kinds?.Select(value => value?.Trim().ToUpperInvariant() ?? string.Empty).ToArray()
            ?? Array.Empty<string>();
        if (requestedKinds.Any(kind => !SupportedKinds.Contains(kind)))
            throw new ArgumentException("Diagnostic kind is not supported.", nameof(request));
        var kinds = NormalizeKinds(requestedKinds);
        if (kinds.Count is < 1 or > 7)
            throw new ArgumentException("At least one supported diagnostic kind is required.", nameof(request));
        if (request.TimeoutMs is < 100 or > 30_000)
            throw new ArgumentException("timeoutMs must be between 100 and 30000.", nameof(request));
        var siteId = NormalizeOptional(request.SiteId);
        if (siteId is not null && !scope.IsGlobal && !scope.AllowedSiteReferences.Contains(siteId, StringComparer.Ordinal))
            throw new DeviceSiteScopeException("DEVICE_SITE_SCOPE_DENIED", "The requested Site is outside the current user scope.");

        var targets = new List<DiagnosticTarget>(deviceIds.Count);
        foreach (var deviceId in deviceIds)
        {
            var detail = _catalog.GetDeviceInventoryDetail(deviceId, scope)
                ?? throw new DeviceSiteScopeException("DEVICE_SITE_SCOPE_DENIED", $"Device '{deviceId}' is not visible in the current Site scope.");
            if (!detail.Item.Enabled) throw new ArgumentException($"Device '{deviceId}' is disabled.", nameof(request));
            if (siteId is not null && !string.Equals(siteId, detail.Item.SiteId, StringComparison.Ordinal))
                throw new ArgumentException("All diagnostic targets must belong to the requested Site.", nameof(request));
            var host = detail.TerminalProfile?.Host ?? detail.Item.ManagementIp;
            var port = detail.TerminalProfile?.Port ?? (detail.Item.Port is > 0 ? detail.Item.Port : 22);
            if (!IsSafeHost(host) || port is < 1 or > 65535)
                throw new ArgumentException($"Device '{deviceId}' has an invalid management endpoint.", nameof(request));
            targets.Add(new(deviceId, detail.Item.Name, detail.Item.SiteId, host, port, detail.TerminalProfileId));
        }

        if (targets.Count * kinds.Count > MaximumChecks)
            throw new ArgumentException($"A diagnostic batch cannot contain more than {MaximumChecks} checks.", nameof(request));
        if (kinds.Contains("QUICK_PING"))
            NetworkQuickPing.Validate(request.SampleCount, request.IntervalMs, request.TimeoutMs, request.QuickPingProtocol, targets.Count);
        if (kinds.Contains("LONG_PING"))
            NetworkQuickPing.ValidateLongPing(request.LongPingDurationSeconds, request.IntervalMs, request.TimeoutMs, request.QuickPingProtocol, targets.Count);
        if (kinds.Contains("TRACEROUTE") || kinds.Contains("QUICK_PING") || kinds.Contains("LONG_PING"))
            foreach (var target in targets) RequireCurrentTraceTarget(actor, scope, target);
        var run = new DiagnosticRun(Guid.NewGuid().ToString("N"), actor, siteId, request.TimeoutMs, targets, kinds, scope, request.SampleCount, request.IntervalMs, request.QuickPingProtocol, request.LongPingDurationSeconds);
        lock (_gate)
        {
            ThrowIfDisposed();
            PruneRuns();
            PruneHistory(Math.Max(0, MaximumRuns - _runs.Count - 1));
            if (_runs.Count + _history.Count >= MaximumRuns) throw new InvalidOperationException("Too many diagnostic batches are retained; wait for an older batch to expire.");
            _runs.Add(run.Id, run);
            _ = ExecuteRunAsync(run);
            return Snapshot(run, scope);
        }
    }

    public NetworkDiagnosticRunSnapshot? Get(string id, P2B5SiteScopeDecision scope)
    {
        if (!scope.IsAuthorized || string.IsNullOrWhiteSpace(id)) return null;
        lock (_gate)
        {
            if (_runs.TryGetValue(id, out var run)) return FilterSnapshot(Snapshot(run, scope), scope);
            return _history.TryGetValue(id, out var snapshot) ? FilterSnapshot(snapshot, scope) : null;
        }
    }

    public NetworkDiagnosticRunSnapshot? Get(string id, string actor, P2B5SiteScopeDecision scope)
    {
        RequireHistoryReader(actor, scope);
        return Get(id, scope);
    }

    public IReadOnlyList<NetworkDiagnosticRunSummary> List(string actor, P2B5SiteScopeDecision scope, int limit = 50)
    {
        RequireHistoryReader(actor, scope);
        if (limit is < 1 or > MaximumRuns)
            throw new ArgumentException($"limit must be between 1 and {MaximumRuns}.", nameof(limit));
        lock (_gate)
        {
            // Live batches and completed files share one bounded read model.
            // Summary reads never admit a diagnostic or expose hidden child totals.
            return _history.Values.Where(item => !_runs.ContainsKey(item.Id))
                .Concat(_runs.Values.Select(run => Snapshot(run, scope)))
                .Select(item => FilterSnapshot(item, scope)).Where(item => item.Items.Count > 0)
                .OrderByDescending(item => item.CreatedMs).ThenBy(item => item.Id, StringComparer.Ordinal)
                .Take(limit).Select(item => new NetworkDiagnosticRunSummary(item.Id, item.State,
                    item.TotalDevices, item.TotalChecks, item.CompletedChecks, item.ProgressPercent,
                    item.CreatedMs, item.StartedMs, item.EndedMs,
                    item.Items.Select(child => child.Kind).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                    item.Items.GroupBy(child => child.DeviceId, StringComparer.Ordinal)
                        .Select(group => group.First())
                        .OrderBy(child => child.DeviceName, StringComparer.Ordinal)
                        .Select(child => new NetworkDiagnosticTargetSummary(child.DeviceId, child.DeviceName, child.SiteId))
                        .ToArray()))
                .ToArray();
        }
    }

    private void RequireHistoryReader(string actor, P2B5SiteScopeDecision scope)
    {
        if (!scope.IsAuthorized)
            throw new DeviceSiteScopeException("DIAGNOSTIC_SCOPE_DENIED", "当前 Site 权限不可用。");
        if (string.IsNullOrWhiteSpace(actor) || _catalog.GetActiveTaskActor(actor) is null)
            throw new DeviceSiteScopeException("DIAGNOSTIC_ACTOR_REVOKED", "当前账号不可用，请重新登录。");
    }

    public bool Cancel(string id, string actor, string role, P2B5SiteScopeDecision scope)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(id, out var run) || IsTerminalRun(run.State) || !CanOperate(run, actor, role, scope)) return false;
            run.Cancellation.Cancel();
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var run in _runs.Values) run.Cancellation.Cancel();
        }
    }

    public bool SetLongPingPaused(string id, bool paused, string actor, string role, P2B5SiteScopeDecision scope)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(id, out var run) || IsTerminalRun(run.State) || run.Cancellation.IsCancellationRequested ||
                !CanOperate(run, actor, role, scope)) return false;
            var items = run.Items.Where(item => item.Kind == "LONG_PING" && !IsTerminal(item.State)).ToArray();
            if (items.Length == 0) return false;
            // Revalidate both the operator and the admitted owner before changing any child.
            foreach (var item in items)
            {
                RequireCurrentTraceTarget(actor, scope, item.Target);
                RequireCurrentTraceTarget(run.Actor, run.Scope, item.Target);
            }
            foreach (var item in items)
                item.LongPingControl.SetPaused(paused, state => UpdateLongPingState(item, state));
            return true;
        }
    }

    private static void UpdateLongPingState(DiagnosticItem item, string state)
    {
        lock (item.Gate)
        {
            if (IsTerminal(item.State) || item.State == "QUEUED") return;
            // A resumed confirmation window must not reuse pre-pause evidence.
            // PAUSING may still own one in-flight sample: conservatively exclude it.
            if (state == "PAUSING" || state == "RUNNING" && item.State is "PAUSING" or "PAUSED")
                item.ConfirmationStartSequence = (item.QuickPing?.Sent ?? 0) +
                    (state == "PAUSING" || item.State == "PAUSING" ? 2 : 1);
            item.State = state;
            item.Detail = state == "PAUSING" ? "等待在途采样结束后暂停" : state == "PAUSED"
                ? "已暂停；原持续时长继续计时，不补记丢失采样" : null;
        }
    }

    private async Task ExecuteRunAsync(DiagnosticRun run)
    {
        run.State = "RUNNING";
        run.StartedMs = UtcNowMs();
        using var concurrency = new SemaphoreSlim(MaximumConcurrency, MaximumConcurrency);
        var work = run.Items.Select(async item =>
        {
            try
            {
                await concurrency.WaitAsync(run.Cancellation.Token);
                try { await ExecuteItemAsync(run, item, run.Cancellation.Token); }
                finally { concurrency.Release(); }
            }
            catch (OperationCanceledException)
            {
                CompleteCancelled(item);
            }
            catch
            {
                Complete(item, "FAILED", "DIAGNOSTIC_FAILED", "诊断执行失败", null, 0, Array.Empty<string>());
            }
        }).ToArray();
        try { await Task.WhenAll(work); }
        catch { /* Each item is isolated and records its own terminal outcome. */ }
        run.EndedMs = UtcNowMs();
        var completed = run.Items.All(item => IsTerminal(item.State));
        if (!completed) return;
        var cancelled = run.Items.Count(item => item.State == "CANCELLED");
        var failed = run.Items.Count(item => item.State == "FAILED");
        var noEvidence = run.Items.Count(item => item.State == "NO_EVIDENCE");
        lock (_gate)
        {
            // Publish the terminal run together with its persistence attempt.
            // A caller observing completion may immediately restart the service.
            run.State = cancelled == run.Items.Count ? "CANCELLED" : failed + noEvidence + cancelled == 0 ? "SUCCEEDED" : "PARTIAL";
            if (run.State == "PARTIAL") run.FailureReason = "部分诊断未成功；请查看设备明细。";
            if (PersistCompleted(run))
            {
                // Count a persisted run only once when it leaves the live set.
                _runs.Remove(run.Id);
                PruneHistory(Math.Max(0, MaximumRuns - _runs.Count));
            }
        }
    }

    private async Task ExecuteItemAsync(DiagnosticRun run, DiagnosticItem item, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
        item.State = "RUNNING";
        item.StartedMs = UtcNowMs();
        var started = Stopwatch.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(item.Kind switch
        {
            "QUICK_PING" => 125_000,
            "LONG_PING" => checked(run.LongPingDurationSeconds * 1_000 + 5_000),
            _ => run.TimeoutMs
        });
        try
        {
            switch (item.Kind)
            {
                case "TRACEROUTE":
                {
                    var trace = await NetworkTraceroute.RunAsync(item.Target.Host, run.TimeoutMs, _tracerouteProbe,
                        () => RequireCurrentTraceTarget(run.Actor, run.Scope, item.Target),
                        snapshot => { lock (item.Gate) item.TraceRoute = snapshot; }, timeout.Token);
                    var unreachable = trace.Hops.LastOrDefault()?.Status == "UNREACHABLE";
                    Complete(item, trace.DestinationReached ? "SUCCEEDED" : "FAILED",
                        trace.DestinationReached ? "OK" : unreachable ? "TRACEROUTE_UNREACHABLE" : "TRACEROUTE_MAX_HOPS",
                        trace.DestinationReached ? $"已通过 {trace.Hops.Count} 跳到达目标" :
                            unreachable ? "路径返回不可达；保留已观察到的跳点" : "已达到 30 跳上限；未确认到达目标",
                        ElapsedMs(started), 0, Array.Empty<string>());
                    break;
                }
                case "QUICK_PING":
                {
                    var sample = await NetworkQuickPing.RunAsync(item.Target.Host, item.Target.Port, run.QuickPingProtocol,
                        run.SampleCount, run.IntervalMs, run.TimeoutMs, _quickPingProbe,
                        () => RequireCurrentTraceTarget(run.Actor, run.Scope, item.Target),
                        snapshot => { lock (item.Gate) item.QuickPing = snapshot; }, timeout.Token);
                    Complete(item, sample.Lost == 0 ? "SUCCEEDED" : "FAILED", sample.Lost == 0 ? "OK" : "QUICK_PING_LOSS",
                        $"{sample.Protocol} 响应 {sample.Received}/{sample.Sent}，丢失 {sample.LossPercent}%", ElapsedMs(started),
                        sample.Sent, new[] { "SERVICE_HOST_" + sample.Protocol });
                    break;
                }
                case "LONG_PING":
                {
                    var sample = await NetworkQuickPing.RunForDurationAsync(item.Target.Host, item.Target.Port,
                        run.QuickPingProtocol, run.LongPingDurationSeconds, run.IntervalMs, run.TimeoutMs,
                        _quickPingProbe, () => RequireCurrentTraceTarget(run.Actor, run.Scope, item.Target),
                        snapshot => { lock (item.Gate) item.QuickPing = snapshot; }, timeout.Token,
                        item.LongPingControl, state => UpdateLongPingState(item, state));
                    Complete(item, sample.Sent == 0 ? "NO_EVIDENCE" : sample.Lost == 0 ? "SUCCEEDED" : "FAILED",
                        sample.Sent == 0 ? "LONG_PING_NO_SAMPLES" : sample.Lost != 0 ? "LONG_PING_LOSS" :
                            sample.Sent < sample.RequestedSamples ? "LONG_PING_WINDOW_ENDED" : "OK",
                        $"{sample.Protocol} 响应 {sample.Received}/{sample.Sent}；未执行 {sample.RequestedSamples - sample.Sent} 次不计丢失；原持续时长已结束", ElapsedMs(started),
                        sample.Sent, new[] { "SERVICE_HOST_" + sample.Protocol });
                    break;
                }
                case "PING":
                {
                    using var ping = new Ping();
                    var addresses = await Dns.GetHostAddressesAsync(item.Target.Host, timeout.Token);
                    if (addresses.Length == 0)
                    {
                        Complete(item, "FAILED", "PING_DNS_EMPTY", "无法解析 Ping 目标", ElapsedMs(started), 0, Array.Empty<string>());
                        break;
                    }
                    var reply = await ping.SendPingAsync(addresses[0], TimeSpan.FromMilliseconds(run.TimeoutMs), new byte[32], new PingOptions(), timeout.Token);
                    var ok = reply.Status == IPStatus.Success;
                    Complete(item, ok ? "SUCCEEDED" : "FAILED", ok ? "OK" : "PING_" + reply.Status.ToString().ToUpperInvariant(),
                        ok ? "设备可达" : "Ping 未成功", ElapsedMs(started), 0, Array.Empty<string>());
                    break;
                }
                case "TCP":
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(item.Target.Host, item.Target.Port, timeout.Token);
                    Complete(item, "SUCCEEDED", "OK", $"TCP/{item.Target.Port} 可连接", ElapsedMs(started), 0, Array.Empty<string>());
                    break;
                }
                case "DNS":
                {
                    var resolution = await ResolveDnsAsync(item.Target.Host, timeout.Token);
                    var literal = resolution.ResolutionMode == "IP_LITERAL";
                    var values = resolution.Addresses;
                    lock (item.Gate) item.Dns = resolution;
                    var detail = literal ? $"目标已是 IP 地址：{values[0]}，未发起 DNS 查询" :
                        values.Count == 0 ? "系统解析器未返回地址" : $"系统解析器返回：{string.Join("、", values)}";
                    Complete(item, values.Count == 0 ? "FAILED" : "SUCCEEDED", values.Count == 0 ? "DNS_EMPTY" : "OK",
                        detail, ElapsedMs(started), values.Count, new[] { literal ? "SERVICE_HOST_IP_LITERAL" : "SERVICE_HOST_SYSTEM_RESOLVER" });
                    break;
                }
                case "LLDP":
                {
                    if (item.Target.ProfileId is null)
                    {
                        Complete(item, "NO_EVIDENCE", "LLDP_PROFILE_MISSING", "设备没有可读取的 H3C 事实来源", ElapsedMs(started), 0, Array.Empty<string>());
                        break;
                    }
                    var facts = _catalog.QueryH3cFacts(item.Target.ProfileId, "lldp.neighbor", false, 100, run.Scope, requireCapturedScope: true);
                    var evidence = facts.Where(fact => fact.Status is not ("FAILED" or "PARTIAL" or "UNSUPPORTED"))
                        .ToArray();
                    var sources = evidence.Select(fact => fact.Source).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                    Complete(item, evidence.Length == 0 ? "NO_EVIDENCE" : "SUCCEEDED",
                        evidence.Length == 0 ? "LLDP_FACTS_EMPTY" : "OK",
                        evidence.Length == 0 ? "没有已验证的 LLDP Facts" : $"已读取 {evidence.Length} 条已验证 LLDP Facts",
                        ElapsedMs(started), evidence.Length, sources);
                    break;
                }
                default:
                    Complete(item, "FAILED", "UNSUPPORTED_KIND", "不支持的诊断类型", ElapsedMs(started), 0, Array.Empty<string>());
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            Complete(item, cancellationToken.IsCancellationRequested ? "CANCELLED" : "FAILED",
                cancellationToken.IsCancellationRequested ? "CANCELLED" : "TIMEOUT",
                cancellationToken.IsCancellationRequested ? "已取消" : "诊断超时", ElapsedMs(started), 0, Array.Empty<string>());
        }
        catch (DeviceSiteScopeException)
        {
            Complete(item, "CANCELLED", "DIAGNOSTIC_SCOPE_REVOKED", "当前账号、局点或设备目标已变化，诊断已停止",
                ElapsedMs(started), 0, Array.Empty<string>());
        }
        catch (PingException exception) when (exception.InnerException is UnauthorizedAccessException or
            System.ComponentModel.Win32Exception { NativeErrorCode: 5 } or
            SocketException { SocketErrorCode: SocketError.AccessDenied })
        {
            Complete(item, "FAILED", "ICMP_PERMISSION_DENIED",
                "Service 主机拒绝 ICMP 探测，请检查服务账户或主机策略", ElapsedMs(started), 0, Array.Empty<string>());
        }
        catch (PingException)
        {
            Complete(item, "FAILED", item.Kind == "TRACEROUTE" ? "TRACEROUTE_PROBE_FAILED" : "PING_FAILED",
                item.Kind == "TRACEROUTE" ? "ICMP 路径探测未成功" : "Ping 未成功", ElapsedMs(started), 0, Array.Empty<string>());
        }
        catch (SocketException)
        {
            Complete(item, "FAILED", item.Kind is "DNS" or "TRACEROUTE" ? "DNS_FAILED" : "TCP_UNREACHABLE", "网络连接未成功", ElapsedMs(started), 0, Array.Empty<string>());
        }
        catch
        {
            Complete(item, "FAILED", "DIAGNOSTIC_FAILED", "诊断执行失败", ElapsedMs(started), 0, Array.Empty<string>());
        }
    }

    internal static async Task<NetworkDnsSnapshot> ResolveDnsAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var literal = IPAddress.TryParse(host, out var parsed);
        var addresses = literal ? new[] { parsed! } : await Dns.GetHostAddressesAsync(host, cancellationToken);
        var values = addresses.Select(address => address.ToString()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return new(host, literal ? "IP_LITERAL" : "SYSTEM_RESOLVER", values, UtcNowMs());
    }

    private void RequireCurrentTraceTarget(string actorId, P2B5SiteScopeDecision admittedScope, DiagnosticTarget target)
    {
        var actor = _catalog.GetActiveTaskActor(actorId);
        if (actor is not { Role: "SUPERADMIN" }) Deny();
        // Diagnostics admission requires SUPERADMIN; a current actor with that
        // role has global Site scope, and a downgrade must stop the trace.
        var currentScope = P2B5SiteScopeDecision.Global;
        if (!currentScope.IsAuthorized || !DeviceSiteScope.Allows(admittedScope, target.SiteId) ||
            !DeviceSiteScope.Allows(currentScope, target.SiteId)) Deny();
        var current = _catalog.GetDeviceInventoryDetail(target.DeviceId, currentScope);
        var host = current?.TerminalProfile?.Host ?? current?.Item.ManagementIp;
        var port = current?.TerminalProfile?.Port ?? current?.Item.Port;
        if (current is null || !current.Item.Enabled || current.Item.SiteId != target.SiteId ||
            current.TerminalProfileId != target.ProfileId || host != target.Host ||
            (port is > 0 ? port : 22) != target.Port) Deny();
        static void Deny() => throw new DeviceSiteScopeException("DIAGNOSTIC_SCOPE_REVOKED",
            "当前账号、局点或设备目标已变化，无法继续诊断");
    }

    private static bool CanOperate(DiagnosticRun run, string actor, string role, P2B5SiteScopeDecision scope) =>
        (run.Actor == actor || role == "SUPERADMIN") && scope.IsAuthorized &&
        run.Targets.All(target => DeviceSiteScope.Allows(scope, target.SiteId));

    private static void CompleteCancelled(DiagnosticItem item) =>
        Complete(item, "CANCELLED", "CANCELLED", "已取消", null, 0, Array.Empty<string>());

    private static void Complete(DiagnosticItem item, string state, string outcome, string detail,
        long? latencyMs, int evidenceCount, IReadOnlyList<string> evidenceSources)
    {
        lock (item.Gate)
        {
            if (IsTerminal(item.State)) return;
            item.State = state;
            item.Outcome = outcome;
            item.Detail = detail;
            item.LatencyMs = latencyMs;
            item.EvidenceCount = evidenceCount;
            item.EvidenceSources = evidenceSources;
            item.EndedMs = UtcNowMs();
        }
    }

    private static NetworkDiagnosticRunSnapshot Snapshot(DiagnosticRun run, P2B5SiteScopeDecision scope)
    {
        var visible = run.Items.Where(item => DeviceSiteScope.Allows(scope, item.Target.SiteId)).Select(item => item.ToSnapshot()).ToArray();
        var completed = visible.Count(item => IsTerminal(item.State));
        var succeeded = visible.Count(item => item.State == "SUCCEEDED");
        var failed = visible.Count(item => item.State == "FAILED");
        var noEvidence = visible.Count(item => item.State == "NO_EVIDENCE");
        var cancelled = visible.Count(item => item.State == "CANCELLED");
        var queued = visible.Count(item => item.State == "QUEUED");
        var running = visible.Count(item => item.State is "RUNNING" or "PAUSING");
        var percent = ProgressPercent(completed, visible.Length, run.State);
        return new(run.Id, run.Actor, run.State, run.SiteId, run.TimeoutMs, run.Targets.Count, visible.Length,
            completed, succeeded, failed, noEvidence, cancelled, queued, running, percent, run.CreatedMs,
            run.StartedMs, run.EndedMs, run.FailureReason, visible);
    }

    private void PruneRuns()
    {
        foreach (var id in _runs.Values.Where(run => IsTerminalRun(run.State))
                     .OrderBy(run => run.EndedMs ?? run.CreatedMs).Take(Math.Max(0, _runs.Count - MaximumRuns + 1)).Select(run => run.Id).ToArray())
            _runs.Remove(id);
    }

    private void LoadHistory()
    {
        Directory.CreateDirectory(_historyRoot);
        if ((File.GetAttributes(_historyRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Diagnostic history directory cannot be a reparse point.");
        foreach (var path in Directory.EnumerateFiles(_historyRoot, "*.json", SearchOption.TopDirectoryOnly))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > MaximumHistoryBytes)
                continue;
            try
            {
                var snapshot = JsonSerializer.Deserialize<NetworkDiagnosticRunSnapshot>(File.ReadAllText(path));
                if (snapshot is { Id: not null, Items: not null } && IsSafeId(snapshot.Id) && IsTerminalRun(snapshot.State) &&
                    snapshot.Items.Count <= MaximumChecks && snapshot.TotalChecks == snapshot.Items.Count &&
                    snapshot.Items.All(item => item is not null && (item.QuickPing is null || item.QuickPing.Samples is { Count: <= 3_600 }) && (item.TraceRoute is null ||
                        item.TraceRoute is { MaxHops: NetworkTraceroute.MaximumHops } trace &&
                        trace.Hops is { Count: <= NetworkTraceroute.MaximumHops })))
                    _history[snapshot.Id] = snapshot;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            { /* A corrupt history file is ignored; live diagnostic collection remains available. */ }
        }
        PruneHistory(MaximumRuns);
    }

    private bool PersistCompleted(DiagnosticRun run)
    {
        var snapshot = Snapshot(run, P2B5SiteScopeDecision.Global);
        var path = Path.Combine(_historyRoot, snapshot.Id + ".json");
        var part = path + ".part";
        try
        {
            Directory.CreateDirectory(_historyRoot);
            File.WriteAllText(part, JsonSerializer.Serialize(snapshot));
            File.Move(part, path, true);
            lock (_gate)
            {
                _history[snapshot.Id] = snapshot;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { if (File.Exists(part)) File.Delete(part); } catch { }
            return false;
        }
    }

    private void PruneHistory(int maxRetained = MaximumRuns)
    {
        foreach (var item in _history.Values.OrderByDescending(value => value.CreatedMs).Skip(maxRetained).ToArray())
        {
            _history.Remove(item.Id);
            var path = Path.Combine(_historyRoot, item.Id + ".json");
            try { if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) File.Delete(path); } catch { }
        }
    }

    private static NetworkDiagnosticRunSnapshot FilterSnapshot(NetworkDiagnosticRunSnapshot snapshot,
        P2B5SiteScopeDecision scope)
    {
        var visible = snapshot.Items.Where(item => DeviceSiteScope.Allows(scope, item.SiteId)).ToArray();
        var completed = visible.Count(item => IsTerminal(item.State));
        var succeeded = visible.Count(item => item.State == "SUCCEEDED");
        var failed = visible.Count(item => item.State == "FAILED");
        var noEvidence = visible.Count(item => item.State == "NO_EVIDENCE");
        var cancelled = visible.Count(item => item.State == "CANCELLED");
        var queued = visible.Count(item => item.State == "QUEUED");
        var running = visible.Count(item => item.State is "RUNNING" or "PAUSING");
        var devices = visible.Select(item => item.DeviceId).Distinct(StringComparer.Ordinal).Count();
        return snapshot with
        {
            TotalDevices = devices, TotalChecks = visible.Length, CompletedChecks = completed,
            SucceededChecks = succeeded, FailedChecks = failed, NoEvidenceChecks = noEvidence,
            CancelledChecks = cancelled, QueuedChecks = queued, RunningChecks = running,
            ProgressPercent = ProgressPercent(completed, visible.Length, snapshot.State),
            Items = visible
        };
    }

    private static bool IsSafeId(string value) => value.Length is > 0 and <= 128 &&
        value.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_');

    private static IReadOnlyList<string> NormalizeIds(IReadOnlyList<string>? values) => values is null
        ? Array.Empty<string>()
        : values.Select(value => value?.Trim() ?? string.Empty).Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal).ToArray();

    private static IReadOnlyList<string> NormalizeKinds(IReadOnlyList<string>? values) => values is null
        ? Array.Empty<string>()
        : values.Select(value => value?.Trim().ToUpperInvariant() ?? string.Empty).Where(SupportedKinds.Contains)
            .Distinct(StringComparer.Ordinal).ToArray();

    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static bool IsSafeHost(string value) => value.Length is > 0 and <= 253 && !value.Any(char.IsControl) &&
        Uri.CheckHostName(value) is not UriHostNameType.Unknown and not UriHostNameType.Basic;
    private static bool IsTerminal(string state) => state is "SUCCEEDED" or "FAILED" or "NO_EVIDENCE" or "CANCELLED";
    private static bool IsTerminalRun(string state) => state is "SUCCEEDED" or "PARTIAL" or "FAILED" or "CANCELLED";
    private static int ProgressPercent(int completed, int total, string state)
    {
        var percent = total == 0 ? 0 : completed * 100 / total;
        // Child checks may finish before the run publishes its terminal state and history.
        return IsTerminalRun(state) ? percent : Math.Min(99, percent);
    }
    private static long? ElapsedMs(long started) => Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    private static long UtcNowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(NetworkDiagnosticsService)); }

    private sealed record DiagnosticTarget(string DeviceId, string Name, string SiteId, string Host, int Port, string? ProfileId);

    private sealed class DiagnosticItem(string id, DiagnosticTarget target, string kind)
    {
        public object Gate { get; } = new();
        public string Id { get; } = id;
        public DiagnosticTarget Target { get; } = target;
        public string Kind { get; } = kind;
        public string State { get; set; } = "QUEUED";
        public string? Outcome { get; set; }
        public string? Detail { get; set; }
        public long? StartedMs { get; set; }
        public long? EndedMs { get; set; }
        public long? LatencyMs { get; set; }
        public int EvidenceCount { get; set; }
        public IReadOnlyList<string> EvidenceSources { get; set; } = Array.Empty<string>();
        public NetworkTracerouteSnapshot? TraceRoute { get; set; }
        public QuickPingSnapshot? QuickPing { get; set; }
        public NetworkDnsSnapshot? Dns { get; set; }
        public int ConfirmationStartSequence { get; set; } = 1;
        public LongPingControl LongPingControl { get; } = new();

        public NetworkDiagnosticItemSnapshot ToSnapshot()
        {
            lock (Gate) return new(Id, Target.DeviceId, Target.Name, Target.SiteId, Kind,
                State, Outcome, Detail, StartedMs, EndedMs, LatencyMs, EvidenceCount, EvidenceSources, TraceRoute, QuickPing, Dns, ConfirmationStartSequence);
        }
    }

    private sealed class DiagnosticRun
    {
        public DiagnosticRun(string id, string actor, string? siteId, int timeoutMs,
            IReadOnlyList<DiagnosticTarget> targets, IReadOnlyList<string> kinds, P2B5SiteScopeDecision scope,
            int sampleCount, int intervalMs, string quickPingProtocol, int longPingDurationSeconds)
        {
            SampleCount = sampleCount; IntervalMs = intervalMs; QuickPingProtocol = quickPingProtocol; LongPingDurationSeconds = longPingDurationSeconds;
            Id = id; Actor = actor; SiteId = siteId; TimeoutMs = timeoutMs; Targets = targets; Scope = scope;
            Items = targets.SelectMany(target => kinds.Select(kind =>
                new DiagnosticItem($"{id}:{target.DeviceId}:{kind}", target, kind))).ToArray();
            CreatedMs = UtcNowMs();
        }
        public string Id { get; }
        public string Actor { get; }
        public string? SiteId { get; }
        public int TimeoutMs { get; }
        public int SampleCount { get; }
        public int IntervalMs { get; }
        public string QuickPingProtocol { get; }
        public int LongPingDurationSeconds { get; }
        public IReadOnlyList<DiagnosticTarget> Targets { get; }
        public P2B5SiteScopeDecision Scope { get; }
        public IReadOnlyList<DiagnosticItem> Items { get; }
        public CancellationTokenSource Cancellation { get; } = new();
        public string State { get; set; } = "QUEUED";
        public long CreatedMs { get; }
        public long? StartedMs { get; set; }
        public long? EndedMs { get; set; }
        public string? FailureReason { get; set; }
    }
}
