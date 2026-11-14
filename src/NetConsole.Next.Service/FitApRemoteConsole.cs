using System.Net;
using System.Text.Json;

namespace NetConsole.Next.Service;

internal sealed record FitApConnectionSettingsRequest(
    string Scope,
    string? TargetId,
    string Protocol = "TELNET",
    int Port = 23,
    string AuthMode = "PASSWORD_ONLY",
    string Username = "",
    string? Password = null,
    bool ClearPassword = false);

internal sealed record FitApConnectionSettingsView(
    string Scope,
    string? TargetId,
    string Protocol,
    int Port,
    string AuthMode,
    string Username,
    bool PasswordConfigured,
    string Source,
    long? UpdatedMs,
    bool DefaultPasswordConfigured);

internal sealed record FitApConnectionSettingsResponse(
    FitApConnectionSettingsView Effective,
    IReadOnlyList<FitApConnectionSettingsView> Overrides);

internal sealed record FitApRemoteConsoleCandidate(
    string AcDeviceId,
    string AcName,
    string SiteId,
    string ApName,
    string? ManagementIp,
    string RawState,
    string NormalizedState,
    long CapturedMs,
    string ObservationId);

internal sealed record FitApRemoteConsolePreflight(
    string ResourceId,
    string ApName,
    string? SerialEvidence,
    string ActiveAcStatus,
    FitApRemoteConsoleCandidate? ActiveAc,
    IReadOnlyList<FitApRemoteConsoleCandidate> Candidates,
    string RemoteConsoleState,
    bool EnableRequired,
    string EnableScope,
    string? FailureReason,
    FitApConnectionSettingsView Connection);

internal sealed record FitApRemoteSessionResult(string SessionId, string ResourceId, string ApName,
    string ActiveAcName, FitApFactSnapshot? LatestFact = null);

internal sealed record FitApEnableResult(
    string ResourceId,
    string ApName,
    string ActiveAcName,
    string Scope,
    string State,
    string[] Commands);

internal sealed record FitApEnableRequest(bool ConfirmAllAp = false, string? ActiveAcDeviceId = null);

internal sealed record FitApRemoteConsoleSessionRequest(string? ActiveAcDeviceId = null);

internal sealed class FitApRemoteConsoleException(string code, string message, int statusCode = 409) : InvalidOperationException(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

internal sealed record FitApStoredSetting(
    string Scope,
    string? TargetId,
    string Protocol,
    int Port,
    string AuthMode,
    string Username,
    string? PasswordKey,
    long UpdatedMs);

internal sealed record FitApSettingsDocument(IReadOnlyList<FitApStoredSetting> Items);

internal sealed record FitApResolvedSettings(
    string Protocol,
    int Port,
    string AuthMode,
    string Username,
    string? Password,
    bool PasswordConfigured,
    string Source,
    string? TargetId,
    long? UpdatedMs,
    bool DefaultPasswordConfigured)
{
    public FitApConnectionSettingsView ToView() => new(Source == "DEFAULT" ? "DEFAULT" : Source,
        TargetId, Protocol, Port, AuthMode, Username, PasswordConfigured, Source, UpdatedMs,
        DefaultPasswordConfigured);
}

/// <summary>
/// FIT-AP settings are a small compatibility layer over the existing DPAPI
/// authority. The JSON document contains only non-secret connection metadata
/// and opaque DPAPI keys; passwords never enter the Catalog, API DTOs, facts or
/// terminal session snapshots.
/// </summary>
internal sealed class FitApConnectionSettingsStore
{
    private readonly RuntimeSettings _settings;
    private readonly IH3cCredentialAuthority _authority;
    private readonly object _gate = new();
    private readonly string _path;

    public FitApConnectionSettingsStore(RuntimeSettings settings, IH3cCredentialAuthority authority)
    {
        _settings = settings;
        _authority = authority;
        var directory = Path.Combine(settings.DataRoot, "wireless");
        Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("FIT-AP settings directory cannot be a reparse point.");
        _path = Path.Combine(directory, "fit-ap-connection-settings.json");
    }

    public FitApResolvedSettings Effective(string? acDeviceId, string? resourceId)
    {
        lock (_gate)
        {
            var items = Read().Items;
            var selected = resourceId is not null
                ? items.FirstOrDefault(item => item.Scope == "AP" && item.TargetId == resourceId)
                : null;
            var source = "AP";
            if (selected is null && acDeviceId is not null)
            {
                selected = items.FirstOrDefault(item => item.Scope == "AC" && item.TargetId == acDeviceId);
                source = "AC";
            }
            if (selected is null)
            {
                selected = items.FirstOrDefault(item => item.Scope == "GLOBAL");
                source = "GLOBAL";
            }
            if (selected is null)
            {
                var defaultPassword = _settings.FitApDefaultPassword;
                return new("TELNET", 23, "PASSWORD_ONLY", string.Empty, defaultPassword,
                    !string.IsNullOrEmpty(defaultPassword), "DEFAULT", null, null, !string.IsNullOrEmpty(defaultPassword));
            }
            var password = selected.PasswordKey is null ? null : _authority.GetPassword(selected.PasswordKey);
            return new(selected.Protocol, selected.Port, selected.AuthMode, selected.Username, password,
                !string.IsNullOrEmpty(password), source, selected.TargetId, selected.UpdatedMs,
                !string.IsNullOrEmpty(_settings.FitApDefaultPassword));
        }
    }

    public FitApConnectionSettingsResponse Describe(string? acDeviceId, string? resourceId)
    {
        lock (_gate)
        {
            var effective = Effective(acDeviceId, resourceId).ToView();
            var items = Read().Items
                .Where(item => item.Scope == "GLOBAL" ||
                    item.Scope == "AC" && item.TargetId == acDeviceId ||
                    item.Scope == "AP" && item.TargetId == resourceId)
                .Select(ToView)
                .OrderBy(item => item.Scope, StringComparer.Ordinal)
                .ToArray();
            return new(effective, items);
        }
    }

    public FitApConnectionSettingsResponse Save(FitApConnectionSettingsRequest request,
        string? acDeviceId, string? resourceId)
    {
        lock (_gate)
        {
            var normalized = Normalize(request, acDeviceId, resourceId);
            var document = Read();
            var items = document.Items.ToList();
            var index = items.FindIndex(item => item.Scope == normalized.Scope && item.TargetId == normalized.TargetId);
            var previous = index >= 0 ? items[index] : null;
            if (request.ClearPassword)
            {
                if (previous?.PasswordKey is not null) _authority.RevokePassword(previous.PasswordKey);
                if (index >= 0) items.RemoveAt(index);
                Write(new FitApSettingsDocument(items));
                return Describe(acDeviceId, resourceId);
            }
            var passwordKey = previous?.PasswordKey;
            if (request.Password is not null)
            {
                if (request.Password.Length == 0) throw new ArgumentException("Password cannot be empty when supplied.");
                passwordKey ??= Guid.NewGuid().ToString("N");
                _authority.SetPassword(passwordKey, request.Password);
            }
            var item = new FitApStoredSetting(normalized.Scope, normalized.TargetId, normalized.Protocol,
                normalized.Port, normalized.AuthMode, normalized.Username, passwordKey,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (index >= 0) items[index] = item; else items.Add(item);
            Write(new FitApSettingsDocument(items));
            return Describe(acDeviceId, resourceId);
        }
    }

    private static FitApConnectionSettingsRequest Normalize(FitApConnectionSettingsRequest request,
        string? acDeviceId, string? resourceId)
    {
        var scope = request.Scope?.Trim().ToUpperInvariant();
        if (scope is not ("GLOBAL" or "AC" or "AP")) throw new ArgumentException("Scope must be GLOBAL, AC or AP.");
        var target = scope switch
        {
            "GLOBAL" when string.IsNullOrWhiteSpace(request.TargetId) => null,
            "AC" when !string.IsNullOrWhiteSpace(request.TargetId) && request.TargetId == acDeviceId => request.TargetId,
            "AP" when !string.IsNullOrWhiteSpace(request.TargetId) && request.TargetId == resourceId => request.TargetId,
            _ => throw new ArgumentException("The settings target is not the selected wireless resource.")
        };
        var protocol = request.Protocol?.Trim().ToUpperInvariant();
        if (protocol is not ("TELNET" or "SSH")) throw new ArgumentException("FIT-AP protocol must be SSH or TELNET.");
        if (request.Port is < 1 or > 65535) throw new ArgumentException("FIT-AP port is invalid.");
        var authMode = request.AuthMode?.Trim().ToUpperInvariant();
        if (authMode is not ("PASSWORD_ONLY" or "USERNAME_PASSWORD"))
            throw new ArgumentException("FIT-AP authentication mode is invalid.");
        var username = request.Username?.Trim() ?? string.Empty;
        if (authMode == "PASSWORD_ONLY") username = string.Empty;
        if (authMode == "USERNAME_PASSWORD" && username.Length == 0)
            throw new ArgumentException("Username is required for username/password mode.");
        return request with { Scope = scope, TargetId = target, Protocol = protocol, AuthMode = authMode, Username = username };
    }

    private FitApSettingsDocument Read()
    {
        if (!File.Exists(_path)) return new([]);
        if ((File.GetAttributes(_path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("FIT-AP settings file cannot be a reparse point.");
        return JsonSerializer.Deserialize<FitApSettingsDocument>(File.ReadAllText(_path), JsonOptions) ?? new([]);
    }

    private void Write(FitApSettingsDocument document)
    {
        var temporary = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions));
        try
        {
            if (File.Exists(_path)) File.Replace(temporary, _path, null, ignoreMetadataErrors: true);
            else File.Move(temporary, _path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private FitApConnectionSettingsView ToView(FitApStoredSetting item)
    {
        var configured = item.PasswordKey is not null && _authority.HasPassword(item.PasswordKey);
        return new(item.Scope, item.TargetId, item.Protocol, item.Port, item.AuthMode, item.Username,
            configured, item.Scope, item.UpdatedMs, !string.IsNullOrEmpty(_settings.FitApDefaultPassword));
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}

internal sealed class FitApRemoteConsoleService
{
    private const long FreshnessWindowMs = 300_000;
    private readonly Catalog _catalog;
    private readonly WirelessReadModelService _wireless;
    private readonly FitApConnectionSettingsStore _settings;
    private readonly IH3cCredentialProvider _credentials;

    public FitApRemoteConsoleService(Catalog catalog, WirelessReadModelService wireless,
        FitApConnectionSettingsStore settings, IH3cCredentialProvider credentials)
    {
        _catalog = catalog;
        _wireless = wireless;
        _settings = settings;
        _credentials = credentials;
    }

    public FitApConnectionSettingsResponse GetSettings(string resourceId, P2B5SiteScopeDecision scope,
        string? selectedAcDeviceId = null)
    {
        var preflight = Inspect(resourceId, scope, selectedAcDeviceId);
        return _settings.Describe(preflight.ActiveAc?.AcDeviceId, resourceId);
    }

    public FitApConnectionSettingsResponse SaveSettings(FitApConnectionSettingsRequest request,
        string resourceId, P2B5SiteScopeDecision scope, string? selectedAcDeviceId = null)
    {
        var preflight = Inspect(resourceId, scope, selectedAcDeviceId);
        return _settings.Save(request, preflight.ActiveAc?.AcDeviceId, resourceId);
    }

    public FitApRemoteConsolePreflight Inspect(string resourceId, P2B5SiteScopeDecision scope,
        string? selectedAcDeviceId = null)
    {
        var snapshot = _wireless.GetSnapshot(scope);
        var resource = snapshot.ApResources.FirstOrDefault(item => item.Id == resourceId);
        if (resource is null) throw new KeyNotFoundException("FIT-AP resource was not found.");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var candidates = resource.Observations
            .Where(observation => observation.NormalizedState == "RUNNING_MASTER" &&
                observation.CapturedMs <= now && now - observation.CapturedMs <= FreshnessWindowMs)
            .GroupBy(observation => observation.SourceAcDeviceId, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => item.CapturedMs).First())
            .Select(observation => ToCandidate(observation, scope))
            .OrderBy(item => item.AcDeviceId, StringComparer.Ordinal)
            .ToArray();
        var selected = string.IsNullOrWhiteSpace(selectedAcDeviceId) ? null :
            candidates.FirstOrDefault(candidate => candidate.AcDeviceId == selectedAcDeviceId);
        var active = selected ?? (candidates.Length == 1 ? candidates[0] : null);
        var activeStatus = !string.IsNullOrWhiteSpace(selectedAcDeviceId)
            ? selected is null ? "SELECTION_INVALID" : "RESOLVED_EXPLICIT"
            : candidates.Length switch
        {
            0 => "UNKNOWN_OR_STALE",
            1 => "RESOLVED",
            _ => "AMBIGUOUS"
        };
        var effective = _settings.Effective(active?.AcDeviceId, resourceId);
        var consoleState = active is null ? "UNKNOWN" : DetectRemoteConsoleState(active.AcDeviceId, active.CapturedMs, active.ApName, scope);
        var failure = activeStatus == "AMBIGUOUS" ? "ACTIVE_AC_SELECTION_REQUIRED" :
            activeStatus == "SELECTION_INVALID" ? "ACTIVE_AC_SELECTION_INVALID" :
            active is null ? "ACTIVE_AC_NOT_RESOLVED" :
            string.IsNullOrWhiteSpace(active.ManagementIp) ? "AP_MANAGEMENT_ADDRESS_UNAVAILABLE" :
            !effective.PasswordConfigured ? "FIT_AP_PASSWORD_UNAVAILABLE" : null;
        return new(resourceId, resource.DisplayName, resource.SerialEvidence, activeStatus, active, candidates,
            consoleState, consoleState == "DISABLED", "ALL", failure, effective.ToView());
    }

    public Task<FitApRemoteSessionResult> CreateSessionAsync(string resourceId,
        P2B5SiteScopeDecision scope, string operatorIdentity, TerminalSessionManager terminal,
        string? selectedAcDeviceId = null)
    {
        var preflight = Inspect(resourceId, scope, selectedAcDeviceId);
        EnsureConnectable(preflight);
        var effective = _settings.Effective(preflight.ActiveAc!.AcDeviceId, resourceId);
        var fact = CollectVersion(preflight, effective);
        var sessionId = terminal.CreateDirect(
            targetId: "fit-ap:" + resourceId,
            targetName: preflight.ApName,
            host: preflight.ActiveAc.ManagementIp!,
            port: effective.Port,
            username: effective.AuthMode == "PASSWORD_ONLY" ? string.Empty : effective.Username,
            password: effective.Password!,
            protocol: effective.Protocol,
            siteName: preflight.ActiveAc.SiteId,
            operatorIdentity: operatorIdentity);
        return Task.FromResult(new FitApRemoteSessionResult(sessionId, resourceId, preflight.ApName, preflight.ActiveAc.AcName, fact));
    }

    public IReadOnlyList<FitApFactSnapshot> GetFacts(string resourceId, P2B5SiteScopeDecision scope)
    {
        var snapshot = _wireless.GetSnapshot(scope);
        if (snapshot.ApResources.All(item => item.Id != resourceId))
            throw new KeyNotFoundException("FIT-AP resource was not found.");
        return _catalog.QueryFitApFacts(resourceId, scope);
    }

    public async Task<FitApEnableResult> EnableAsync(string resourceId, P2B5SiteScopeDecision scope,
        string actor, bool confirmAllAp, CancellationToken cancellationToken, string? selectedAcDeviceId = null)
    {
        var preflight = Inspect(resourceId, scope, selectedAcDeviceId);
        if (preflight.ActiveAc is null) throw new FitApRemoteConsoleException(preflight.ActiveAcStatus == "AMBIGUOUS"
            ? "ACTIVE_AC_SELECTION_REQUIRED" : preflight.ActiveAcStatus == "SELECTION_INVALID"
                ? "ACTIVE_AC_SELECTION_INVALID" : "ACTIVE_AC_NOT_RESOLVED", "当前 AP 的主用 AC 无法唯一确定。");
        if (preflight.EnableScope == "ALL" && !confirmAllAp)
            throw new FitApRemoteConsoleException("FIT_AP_ENABLE_CONFIRMATION_REQUIRED", "当前设备只验证了 AC 侧全 AP 开启方式，需要明确确认其影响范围。");
        var detail = _catalog.GetDeviceInventoryDetail(preflight.ActiveAc.AcDeviceId, scope);
        var profile = detail?.TerminalProfile;
        if (profile is null || detail?.TerminalProfileId is null)
            throw new FitApRemoteConsoleException("ACTIVE_AC_PROFILE_UNAVAILABLE", "主用 AC 没有可用的登录资料。");
        var password = _credentials.GetPassword(profile);
        if (string.IsNullOrEmpty(password))
            throw new FitApRemoteConsoleException("ACTIVE_AC_CREDENTIAL_UNAVAILABLE", "主用 AC 密码不可用。");

        var commands = new[] { "system-view", "probe", "wlan ap-execute all exec-console enable" };
        var transport = new NetmikoWorkerH3cSshTransport(profile with { TerminalProtocol = "SSH" }, password,
            _catalog.GetSiteSshJumpHost(profile.SiteId), _catalog);
        string? correlation = null;
        try
        {
            await transport.ConnectAsync((type, fingerprint) =>
            {
                var decision = _catalog.CheckSshHostKey(profile, type, fingerprint);
                correlation = decision.CorrelationId;
                return decision.Accepted;
            }, cancellationToken).ConfigureAwait(false);
            foreach (var command in commands)
            {
                var output = await transport.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
                if (LooksRejected(output)) throw new FitApRemoteConsoleException("FIT_AP_ENABLE_REJECTED",
                    "主用 AC 拒绝了远程控制台开启命令。");
            }
            if (correlation is not null && !_catalog.CommitSshHostKey(correlation))
                throw new H3cHostKeyMismatchException();
            _catalog.TryRecordDeviceAuditEvent(actor, "FIT_AP_REMOTE_CONSOLE_ENABLE", "REQUESTED",
                preflight.ActiveAc.AcDeviceId, null, new[] { "REMOTE_CONSOLE" }, "scope=ALL");
            return new(resourceId, preflight.ApName, preflight.ActiveAc.AcName, "ALL", "ENABLE_REQUEST_SENT", commands);
        }
        catch
        {
            if (correlation is not null) _catalog.FailSshHostKey(correlation, "FIT_AP_ENABLE_FAILED");
            _catalog.TryRecordDeviceAuditEvent(actor, "FIT_AP_REMOTE_CONSOLE_ENABLE", "FAILED",
                preflight.ActiveAc.AcDeviceId, null, Array.Empty<string>(), "COMMAND_OR_CONNECTION_FAILED");
            throw;
        }
        finally { transport.Dispose(); }
    }

    private FitApRemoteConsoleCandidate ToCandidate(WirelessApObservationView observation,
        P2B5SiteScopeDecision scope)
    {
        var detail = _catalog.GetDeviceInventoryDetail(observation.SourceAcDeviceId, scope);
        var address = detail?.TerminalProfileId is null ? null :
            _catalog.QueryH3cFacts(detail.TerminalProfileId, H3cCommandPolicy.DisplayWlanApAllAddress,
                history: true, limit: 1000, scope: scope, requireCapturedScope: true)
            .Where(fact => fact.CapturedMs == observation.CapturedMs)
            .SelectMany(fact => ParseAddresses(ReadOutput(fact.PayloadJson)))
            .FirstOrDefault(item => string.Equals(item.Name, observation.ObservedApName, StringComparison.Ordinal))?.IpAddress;
        return new(observation.SourceAcDeviceId,
            detail?.Item.Name ?? observation.SourceAcDeviceId, observation.SourceSiteId, observation.ObservedApName,
            address, observation.RawState, observation.NormalizedState, observation.CapturedMs, observation.Id);
    }

    private string DetectRemoteConsoleState(string acDeviceId, long capturedMs, string apName,
        P2B5SiteScopeDecision scope)
    {
        var detail = _catalog.GetDeviceInventoryDetail(acDeviceId, scope);
        if (detail?.TerminalProfileId is null) return "UNKNOWN";
        var output = _catalog.QueryH3cFacts(detail.TerminalProfileId, H3cCommandPolicy.DisplayCurrentConfiguration,
                history: true, limit: 1000, scope: scope, requireCapturedScope: true)
            .Where(fact => fact.CapturedMs >= capturedMs)
            .OrderByDescending(fact => fact.CapturedMs)
            .Select(fact => ReadOutput(fact.PayloadJson))
            .FirstOrDefault(text => text.Length > 0);
        if (string.IsNullOrEmpty(output)) return "UNKNOWN";
        var relevant = output.Split('\n').Any(line => line.Contains(apName, StringComparison.OrdinalIgnoreCase) &&
            line.Contains("exec-console", StringComparison.OrdinalIgnoreCase));
        if (relevant && output.Contains("undo exec-console enable", StringComparison.OrdinalIgnoreCase)) return "DISABLED";
        if (relevant && output.Contains("exec-console enable", StringComparison.OrdinalIgnoreCase)) return "ENABLED";
        return "UNKNOWN";
    }

    private static void EnsureConnectable(FitApRemoteConsolePreflight preflight)
    {
        if (preflight.ActiveAcStatus is "AMBIGUOUS" or "SELECTION_INVALID")
            throw new FitApRemoteConsoleException(
                preflight.ActiveAcStatus == "SELECTION_INVALID" ? "ACTIVE_AC_SELECTION_INVALID" : "ACTIVE_AC_SELECTION_REQUIRED",
                "当前 AP 关联多个主用 AC，请明确选择一个最新候选。");
        if (preflight.ActiveAc is null)
            throw new FitApRemoteConsoleException("ACTIVE_AC_NOT_RESOLVED", "当前 AP 的主用 AC 未能从最新观测中确定。");
        if (string.IsNullOrWhiteSpace(preflight.ActiveAc.ManagementIp))
            throw new FitApRemoteConsoleException("AP_MANAGEMENT_ADDRESS_UNAVAILABLE", "当前观测未提供 FIT-AP 管理地址。");
        if (!preflight.Connection.PasswordConfigured)
            throw new FitApRemoteConsoleException("FIT_AP_PASSWORD_UNAVAILABLE", "请在连接设置中输入 FIT-AP 密码。");
        if (!preflight.Connection.Protocol.Equals("TELNET", StringComparison.OrdinalIgnoreCase))
            throw new FitApRemoteConsoleException("FIT_AP_SSH_NOT_ENABLED", "当前 FIT-AP 远程控制台仅支持已验证的 Telnet 入口。");
    }

    private static bool LooksRejected(string output) => output.Contains("unrecognized", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("invalid", StringComparison.OrdinalIgnoreCase) || output.Contains("incomplete", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("error", StringComparison.OrdinalIgnoreCase) || output.Contains("failed", StringComparison.OrdinalIgnoreCase);

    private static string ReadOutput(string payload)
    {
        try
        {
            using var json = JsonDocument.Parse(payload);
            return json.RootElement.TryGetProperty("output", out var output) ? output.GetString() ?? string.Empty : string.Empty;
        }
        catch (JsonException) { return string.Empty; }
    }

    private static IReadOnlyList<H3cApAddressFact> ParseAddresses(string output) =>
        H3cComwareParser.ParseWlanApAllAddress(output)
            .Where(row => row.IpAddress is not null && IPAddress.TryParse(row.IpAddress, out _)).ToArray();

    private FitApFactSnapshot? CollectVersion(FitApRemoteConsolePreflight preflight,
        FitApResolvedSettings effective)
    {
        var captureId = Guid.NewGuid().ToString("N");
        var capturedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var profile = new H3cDeviceProfileSnapshot(Guid.NewGuid().ToString("N"),
            preflight.ActiveAc!.ManagementIp!, effective.Port,
            effective.AuthMode == "PASSWORD_ONLY" ? string.Empty : effective.Username,
            "FIT_AP_SESSION", string.Empty, true, 0, 0, TerminalProtocol: "TELNET",
            DeviceType: "hp_comware_telnet", SiteId: preflight.ActiveAc.SiteId);
        var transport = new NetmikoWorkerH3cSshTransport(profile, effective.Password!, catalog: _catalog);
        var writer = new RawSegmentWriterFactory().CreateFitAp(_catalog, preflight.ResourceId);
        try
        {
            transport.ConnectAsync((_, _) => false, CancellationToken.None).GetAwaiter().GetResult();
            var output = transport.ExecuteAsync(H3cCommandPolicy.DisplayVersion, CancellationToken.None).GetAwaiter().GetResult();
            writer.AppendFitApCommand(preflight.ResourceId, captureId, preflight.ActiveAc.AcDeviceId,
                preflight.ActiveAc.SiteId, preflight.ApName, preflight.ActiveAc.ManagementIp!,
                H3cCommandPolicy.DisplayVersion, output, capturedMs, "COMPLETE");
            writer.SealAsync().GetAwaiter().GetResult();
            var version = H3cComwareParser.ParseVersion(output);
            if (!version.Recognized) return null;
            var payload = JsonSerializer.Serialize(new { version.Vendor, version.Version, version.Release, version.Model, recognized = version.Recognized });
            var idMaterial = $"{preflight.ResourceId}|{preflight.ActiveAc.AcDeviceId}|{capturedMs}|{writer.Sha256}";
            var id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(idMaterial))).ToLowerInvariant()[..32];
            var fact = new FitApFactSnapshot(id, preflight.ResourceId, preflight.ActiveAc.AcDeviceId,
                preflight.ActiveAc.SiteId, preflight.ApName, "device.identity", "PARSED", capturedMs,
                writer.RelativePath, writer.Sha256 ?? string.Empty, payload);
            _catalog.RecordFitApFact(fact);
            return fact;
        }
        catch
        {
            try { writer.PreserveOpenAsync().GetAwaiter().GetResult(); } catch { }
            return null;
        }
        finally { transport.Dispose(); }
    }
}
