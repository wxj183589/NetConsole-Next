using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NetConsole.Next.Service;

internal sealed record CompleteConfigurationBackup(string Id, string ProfileId, string DeviceId, string SiteId,
    long CapturedMs, string TaskId, string RawSha256, long RawByteCount, string Model, string SoftwareVersion,
    string Integrity, bool Restorable, string State, string Host, int Port, string Protocol,
    string RedactedSha256, long RedactedByteCount, int Version = 1, int FormatVersion = 1);
internal sealed record CompleteConfigurationView(CompleteConfigurationBackup Backup, string RedactedConfiguration);
internal sealed record SimulatorDescriptionChangeRequest(string ProfileId, string ExpectedBackupId,
    string InterfaceName, string? Description);
internal sealed record SimulatorConfigurationChange(string Id, string ProfileId, string DeviceId, string SiteId,
    string InterfaceName, string State, string? BeforeBackupId, string? AfterBackupId, string? RollbackBackupId,
    string? Error, long CreatedMs, long? CompletedMs, string ActorUserId);

internal interface ISimulatorConfigurationSession : IAsyncDisposable
{
    bool SupportsNativeCheckpointRestore => false;
    Task<string> ReadAsync(string command, CancellationToken cancellationToken);
    Task SetDescriptionAsync(string interfaceName, string? description, CancellationToken cancellationToken);
    Task<string> CommitConfigurationCheckpointAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("Device-native checkpoints are unavailable in this session.");
    Task<string> RestoreConfigurationCheckpointAsync(string commitId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Device-native checkpoint replacement is unavailable in this session.");
}
internal interface ISimulatorConfigurationSessionFactory
{
    Task<ISimulatorConfigurationSession> OpenAsync(H3cDeviceProfileSnapshot profile, CancellationToken cancellationToken);
}

// Reuses the same Netmiko worker, worker budget and catalog host-key policy as
// the collector. Configuration reads deliberately bypass the sanitized Raw
// sink: the private original and the public redacted copy are separate files.
internal sealed class NetmikoConfigurationSessionFactory(Catalog catalog) : ISimulatorConfigurationSessionFactory
{
    public async Task<ISimulatorConfigurationSession> OpenAsync(H3cDeviceProfileSnapshot profile, CancellationToken cancellationToken)
    {
        var password = catalog.GetH3cCredentialPassword(profile.Id) ?? throw new H3cCredentialUnavailableException();
        var transport = new NetmikoWorkerH3cSshTransport(profile, password, catalog: catalog);
        string? correlation = null;
        try
        {
            await transport.ConnectAsync((type, fingerprint) =>
            {
                var decision = catalog.CheckSshHostKey(profile, type, fingerprint);
                correlation = decision.CorrelationId;
                return decision.Accepted;
            }, cancellationToken).ConfigureAwait(false);
            if (correlation is not null && !catalog.CommitSshHostKey(correlation)) throw new H3cHostKeyMismatchException();
            return new Session(transport);
        }
        catch
        {
            if (correlation is not null) catalog.FailSshHostKey(correlation, "CONFIGURATION_CONNECT_FAILED");
            transport.Dispose();
            throw;
        }
    }
    private sealed class Session(NetmikoWorkerH3cSshTransport transport) : ISimulatorConfigurationSession
    {
        public Task<string> ReadAsync(string command, CancellationToken cancellationToken) => transport.ExecuteAsync(command, cancellationToken);
        public Task SetDescriptionAsync(string interfaceName, string? description, CancellationToken cancellationToken) =>
            transport.ConfigureDescriptionAsync(interfaceName, description, cancellationToken);
        public Task<string> CommitConfigurationCheckpointAsync(CancellationToken cancellationToken) =>
            transport.CommitConfigurationCheckpointAsync(cancellationToken);
        public Task<string> RestoreConfigurationCheckpointAsync(string commitId, CancellationToken cancellationToken) =>
            transport.RestoreConfigurationCheckpointAsync(commitId, cancellationToken);
        public ValueTask DisposeAsync() { transport.Dispose(); return ValueTask.CompletedTask; }
    }
}

internal sealed partial class SimulatorConfigurationService
{
    private readonly Catalog _catalog;
    private readonly TaskSiteScopeAuthorizer _scopes;
    private readonly ISimulatorConfigurationSessionFactory _sessions;
    private readonly string _root;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Regex InterfacePattern = new(@"\A(GigabitEthernet|Ten-GigabitEthernet|Bridge-Aggregation)[0-9]+(/[0-9]+){0,3}\z", RegexOptions.CultureInvariant);
    private static readonly Regex DescriptionPattern = new(@"\A[A-Za-z0-9][A-Za-z0-9 _.,:/()\-]{0,79}\z", RegexOptions.CultureInvariant);

    public SimulatorConfigurationService(Catalog catalog, RuntimeSettings settings,
        ISimulatorConfigurationSessionFactory? sessions = null)
    {
        _catalog = catalog;
        _scopes = new(catalog, new P2B5SiteScopeStore(settings));
        _sessions = sessions ?? new NetmikoConfigurationSessionFactory(catalog);
        _root = Path.Combine(settings.DataRoot, "backup", "config-complete");
    }

    public async Task<CompleteConfigurationBackup> CaptureAsync(string profileId, WebUserIdentity actor,
        P2B5SiteScopeDecision scope, CancellationToken cancellationToken)
    {
        var target = Target(profileId, actor, scope);
        var id = Guid.NewGuid().ToString("N");
        var execution = new TaskExecutionScope(_scopes.CaptureDefinition(profileId, actor.UserId), actor.UserId, id);
        var gate = Gate(target);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Require(execution, "CONNECT");
            await using var session = await _sessions.OpenAsync(target, cancellationToken).ConfigureAwait(false);
            var backup = await Capture(session, target, execution, cancellationToken).ConfigureAwait(false);
            Audit(actor, "CONFIG_COMPLETE_BACKUP", backup.Id, target.Id, "SUCCEEDED");
            return backup;
        }
        catch (Exception ex)
        {
            Audit(actor, "CONFIG_COMPLETE_BACKUP", id, target.Id, "FAILED", Error(ex));
            if (ex is H3cPartialOutputException { IsCancellation: true }) throw new OperationCanceledException(cancellationToken);
            if (TransportFailure(ex) is { } mapped) throw mapped;
            throw;
        }
        finally { gate.Release(); }
    }

    public IReadOnlyList<CompleteConfigurationBackup> List(string profileId, WebUserIdentity actor, P2B5SiteScopeDecision scope)
    {
        var target = Target(profileId, actor, scope);
        var device = _catalog.GetTaskScopeTarget(profileId, scope)!;
        var directory = DirectoryFor(profileId, false);
        if (!Directory.Exists(directory)) return Array.Empty<CompleteConfigurationBackup>();
        return Directory.EnumerateFiles(directory, "*.json").Select(path => Read(Path.GetFileNameWithoutExtension(path), profileId))
            .Where(item => item is not null).Cast<CompleteConfigurationBackup>()
            .Select(item => item with { Restorable = SameIdentity(item, target, device) })
            .OrderByDescending(item => item.CapturedMs).Take(100).ToArray();
    }

    public CompleteConfigurationView? View(string backupId, WebUserIdentity actor, P2B5SiteScopeDecision scope)
    {
        var backup = Find(backupId);
        if (backup is null) return null;
        var target = Target(backup.ProfileId, actor, scope);
        Match(backup, target, _catalog.GetTaskScopeTarget(target.Id, scope)!);
        return new(backup, Encoding.UTF8.GetString(VerifiedBytes(backup, redacted: true)));
    }

    public IReadOnlyList<SimulatorConfigurationChange> Changes(string profileId, WebUserIdentity actor, P2B5SiteScopeDecision scope)
    {
        _ = Target(profileId, actor, scope);
        var directory = ChangeDirectory(profileId, false);
        if (!Directory.Exists(directory)) return Array.Empty<SimulatorConfigurationChange>();
        return Directory.EnumerateFiles(directory, "*.json").Select(path =>
        {
            try { CheckPath(path); return JsonSerializer.Deserialize<SimulatorConfigurationChange>(File.ReadAllText(path), Json); }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
        }).Where(item => item is not null && item.ProfileId == profileId && item.ActorUserId == actor.UserId)
            .Cast<SimulatorConfigurationChange>().OrderByDescending(item => item.CreatedMs).Take(100).ToArray();
    }

    public async Task<SimulatorConfigurationChange> ChangeDescriptionAsync(SimulatorDescriptionChangeRequest request,
        WebUserIdentity actor, P2B5SiteScopeDecision scope, CancellationToken cancellationToken)
    {
        if (request is null || !InterfacePattern.IsMatch(request.InterfaceName ?? string.Empty) ||
            (request.Description is not null && (!DescriptionPattern.IsMatch(request.Description) || request.Description != request.Description.Trim())))
            throw Failure("CONFIG_CHANGE_INPUT_INVALID", "接口名称或描述不符合受控变更格式。", 400);
        var target = Target(request.ProfileId, actor, scope);
        var expected = Read(request.ExpectedBackupId, request.ProfileId)
            ?? throw Failure("CONFIG_COMPLETE_SOURCE_UNAVAILABLE", "完整备份不可读或校验失败。");
        Match(expected, target, _catalog.GetTaskScopeTarget(target.Id, scope)!);
        var expectedText = Encoding.UTF8.GetString(VerifiedBytes(expected, false));
        var id = Guid.NewGuid().ToString("N");
        var execution = new TaskExecutionScope(_scopes.CaptureDefinition(target.Id, actor.UserId), actor.UserId, id);
        var operation = new SimulatorConfigurationChange(id, target.Id, expected.DeviceId, expected.SiteId,
            request.InterfaceName!, "PREPARING", null, null, null, null, Now(), null, actor.UserId);
        var gate = Gate(target);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var writeAttempted = false;
        string? previous = null;
        CompleteConfigurationBackup? before = null;
        try
        {
            SaveChange(operation);
            Require(execution, "CONNECT");
            await using var session = await _sessions.OpenAsync(target, cancellationToken).ConfigureAwait(false);
            before = await Capture(session, target, execution, cancellationToken).ConfigureAwait(false);
            var beforeText = Encoding.UTF8.GetString(VerifiedBytes(before, false));
            if (Normalize(beforeText) != Normalize(expectedText) || before.Model != expected.Model || before.SoftwareVersion != expected.SoftwareVersion)
                throw Failure("CONFIG_CHANGE_CURRENT_CHANGED", "设备当前配置或版本已变化，请重新备份并比较。");
            previous = Description(beforeText, request.InterfaceName!);
            operation = operation with { BeforeBackupId = before.Id };
            if (previous == request.Description)
                return Finish(operation with { State = "NO_CHANGE", AfterBackupId = before.Id }, actor);
            // Store the uncertain boundary before entering Netmiko. A process
            // interruption leaves an explicit review state instead of success.
            operation = operation with { State = "REQUIRES_REVIEW" };
            SaveChange(operation);
            Require(execution, "WRITE");
            Audit(actor, "SIMULATOR_DESCRIPTION_WRITE_ADMISSION", operation.Id, target.Id, "REQUIRES_REVIEW");
            writeAttempted = true;
            await session.SetDescriptionAsync(request.InterfaceName!, request.Description, cancellationToken).ConfigureAwait(false);
            var after = await Capture(session, target, execution, cancellationToken).ConfigureAwait(false);
            operation = operation with { AfterBackupId = after.Id };
            var afterText = Encoding.UTF8.GetString(VerifiedBytes(after, false));
            if (Description(afterText, request.InterfaceName!) != request.Description ||
                WithoutDescription(beforeText, request.InterfaceName!) != WithoutDescription(afterText, request.InterfaceName!))
                throw Failure("CONFIG_CHANGE_READBACK_MISMATCH", "设备重读结果与预期不一致。");
            return Finish(operation with { State = "VERIFIED" }, actor);
        }
        catch (Exception error)
        {
            operation = operation with { Error = Error(error) };
            if (!writeAttempted || before is null)
                return Finish(operation with { State = "FAILED" }, actor);
            // Independent bounded recovery also runs after HTTP cancellation.
            // It still rechecks the live target and Site authority before writing.
            using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try
            {
                Require(execution, "ROLLBACK_CONNECT");
                await using var session = await _sessions.OpenAsync(target, recovery.Token).ConfigureAwait(false);
                Require(execution, "ROLLBACK_WRITE");
                await session.SetDescriptionAsync(request.InterfaceName!, previous, recovery.Token).ConfigureAwait(false);
                var rollback = await Capture(session, target, execution, recovery.Token).ConfigureAwait(false);
                operation = operation with { RollbackBackupId = rollback.Id };
                if (Normalize(Encoding.UTF8.GetString(VerifiedBytes(before, false))) !=
                    Normalize(Encoding.UTF8.GetString(VerifiedBytes(rollback, false))))
                    throw Failure("CONFIG_ROLLBACK_READBACK_MISMATCH", "回滚后的配置与变更前快照不一致。");
                return Finish(operation with { State = "ROLLBACK_VERIFIED" }, actor);
            }
            catch (Exception rollbackError)
            {
                return Finish(operation with { State = "ROLLBACK_FAILED", Error = operation.Error + ";" + Error(rollbackError) }, actor);
            }
        }
        finally { gate.Release(); }
    }

    private async Task<CompleteConfigurationBackup> Capture(ISimulatorConfigurationSession session,
        H3cDeviceProfileSnapshot target, TaskExecutionScope execution, CancellationToken cancellationToken)
    {
        Require(execution, "READ_VERSION");
        var version = await session.ReadAsync(H3cCommandPolicy.DisplayVersion, cancellationToken).ConfigureAwait(false);
        var software = Regex.Match(version, @"(?im)^.*Comware.*?Version\s+([^\r\n]+)").Groups[1].Value.Trim();
        var model = Regex.Match(version, @"(?im)^\s*(?:H3C\s+)?([^\s]+)\s+uptime\s+is\b").Groups[1].Value.Trim();
        if (software.Length == 0 || model.Length == 0)
            throw Failure("CONFIG_DEVICE_IDENTITY_UNAVAILABLE", "设备型号或 Comware 版本未得到验证。");
        Require(execution, "READ_CONFIGURATION");
        var original = await session.ReadAsync(H3cCommandPolicy.DisplayCurrentConfiguration, cancellationToken).ConfigureAwait(false);
        _ = Normalize(original); // Must be complete before either file is published.
        Require(execution, "PUBLISH");
        var id = Guid.NewGuid().ToString("N");
        var raw = Encoding.UTF8.GetBytes(original);
        var redacted = Encoding.UTF8.GetBytes(ConfigBackupSanitizer.Sanitize(H3cCommandPolicy.DisplayCurrentConfiguration, original));
        var frozen = execution.CreationScope;
        var directory = DirectoryFor(target.Id, true);
        var nextVersion = checked(Directory.EnumerateFiles(directory, "*.json")
            .Select(path => Read(Path.GetFileNameWithoutExtension(path), target.Id)?.Version ?? 0).DefaultIfEmpty(0).Max() + 1);
        var backup = new CompleteConfigurationBackup(id, target.Id, frozen.DeviceId, frozen.SiteId, Now(), execution.DefinitionId,
            Hash(raw), raw.LongLength, model, software, "COMPLETE_HASH_VERIFIED", true, "SUCCEEDED", target.Host,
            target.Port, target.TerminalProtocol, Hash(redacted), redacted.LongLength, nextVersion);
        Write(Path.Combine(directory, id + ".cfg"), raw);
        Write(Path.Combine(directory, id + ".view.cfg"), redacted);
        Write(Path.Combine(directory, id + ".json"), JsonSerializer.SerializeToUtf8Bytes(backup, Json));
        return backup;
    }

    private H3cDeviceProfileSnapshot Target(string profileId, WebUserIdentity actor, P2B5SiteScopeDecision scope)
    {
        if (actor.Role != "SUPERADMIN") throw Failure("CONFIG_CHANGE_PERMISSION_DENIED", "需要 SUPERADMIN 权限。", 403);
        if (!scope.IsAuthorized || !_catalog.H3cProfileIsInScope(profileId, scope))
            throw Failure("CONFIG_CHANGE_TARGET_NOT_VISIBLE", "设备不在当前 Site 范围内。", 404);
        var target = _catalog.GetH3cDeviceProfile(profileId);
        var device = _catalog.GetTaskScopeTarget(profileId, scope);
        if (target is null || device is null || !target.Enabled || !device.Enabled)
            throw Failure("CONFIG_CHANGE_TARGET_NOT_VISIBLE", "设备未启用或库存关联不唯一。", 404);
        var ssh = target.Host is "192.168.56.2" or "192.168.56.5" or "192.168.56.3";
        if (_catalog.GetNetmikoVendor(profileId) != "h3c" ||
            !(ssh && target.Port == 22 && target.TerminalProtocol == "SSH" ||
              target.Host == "192.168.56.4" && target.Port == 23 && target.TerminalProtocol == "TELNET") ||
            _catalog.GetSiteSshJumpHost(target.SiteId) is { Enabled: true })
            throw Failure("CONFIG_CHANGE_TARGET_NOT_AUTHORIZED", "此配置流程只允许四台授权模拟器直连。", 403);
        return target;
    }

    private void Require(TaskExecutionScope execution, string boundary)
    {
        _scopes.Require(execution, execution.CreationScope.ProfileId, boundary);
        var actor = _catalog.GetActiveTaskActor(execution.ActorUserId)!;
        // Also recheck protocol, vendor and jump policy, not just the endpoint.
        var target = Target(execution.CreationScope.ProfileId, actor, P2B5SiteScopeDecision.Global);
        var expected = execution.CreationScope.Endpoint!;
        if (target.Host != expected.Host || target.Port != expected.Port)
            throw Failure("CONFIG_CHANGE_TARGET_CHANGED", "目标已变化。");
    }

    private void Match(CompleteConfigurationBackup backup, H3cDeviceProfileSnapshot target, DeviceInventoryItem device)
    {
        if (!SameIdentity(backup, target, device))
            throw Failure("CONFIG_COMPLETE_IDENTITY_MISMATCH", "备份与当前设备身份不匹配。");
    }

    private static bool SameIdentity(CompleteConfigurationBackup backup, H3cDeviceProfileSnapshot target, DeviceInventoryItem device) =>
        backup.ProfileId == target.Id && backup.DeviceId == device.Id && backup.SiteId == device.SiteId &&
        backup.Host == target.Host && backup.Port == target.Port && backup.Protocol == target.TerminalProtocol;

    private CompleteConfigurationBackup? Read(string id, string profileId)
    {
        if (!Safe(id) || !Safe(profileId)) return null;
        try
        {
            var path = Path.Combine(DirectoryFor(profileId, false), id + ".json");
            CheckPath(path);
            var item = JsonSerializer.Deserialize<CompleteConfigurationBackup>(File.ReadAllText(path), Json);
            if (item is null || item.Id != id || item.ProfileId != profileId || item.FormatVersion != 1 || item.Version <= 0 ||
                item.State != "SUCCEEDED" || item.Integrity != "COMPLETE_HASH_VERIFIED" ||
                string.IsNullOrWhiteSpace(item.Model) || string.IsNullOrWhiteSpace(item.SoftwareVersion)) return null;
            _ = Normalize(Encoding.UTF8.GetString(VerifiedBytes(item, false)));
            var publicBytes = VerifiedBytes(item, true);
            var sanitized = ConfigBackupSanitizer.Sanitize(H3cCommandPolicy.DisplayCurrentConfiguration,
                Encoding.UTF8.GetString(VerifiedBytes(item, false)));
            if (Encoding.UTF8.GetString(publicBytes) != sanitized) return null;
            return item with { Restorable = true };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ConfigRestoreException) { return null; }
    }

    private CompleteConfigurationBackup? Find(string id)
    {
        if (!Safe(id) || !Directory.Exists(_root)) return null;
        CheckPath(_root);
        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            CheckPath(directory);
            if (Read(id, Path.GetFileName(directory)) is { } result) return result;
        }
        return null;
    }

    private byte[] VerifiedBytes(CompleteConfigurationBackup item, bool redacted)
    {
        var path = Path.Combine(DirectoryFor(item.ProfileId, false), item.Id + (redacted ? ".view.cfg" : ".cfg"));
        CheckPath(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var size = redacted ? item.RedactedByteCount : item.RawByteCount;
        if (size is <= 0 or > H3cRawOutput.MaximumBytes || stream.Length != size)
            throw Failure("CONFIG_COMPLETE_INTEGRITY_FAILED", "备份大小校验失败。");
        var bytes = new byte[(int)size];
        stream.ReadExactly(bytes);
        if (Hash(bytes) != (redacted ? item.RedactedSha256 : item.RawSha256))
            throw Failure("CONFIG_COMPLETE_INTEGRITY_FAILED", "备份哈希校验失败。");
        return bytes;
    }

    internal static string Normalize(string original)
    {
        if (Encoding.UTF8.GetByteCount(original) > H3cRawOutput.MaximumBytes || original.Contains("<REDACTED>", StringComparison.Ordinal) ||
            Regex.IsMatch(original, @"(?im)(----\s*More|Unrecognized command|Incomplete command|Permission denied|^\s*%|^\s*Error:)") ||
            original.Any(ch => char.IsControl(ch) && ch is not ('\r' or '\n' or '\t')))
            throw Failure("CONFIG_COMPLETE_CAPTURE_INCOMPLETE", "配置输出不完整或包含脱敏占位符。");
        var lines = original.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var start = Array.FindIndex(lines, line => line.Trim() == "#");
        var end = Array.FindLastIndex(lines, line => line.Trim() == "return");
        if (start < 0 || end <= start || !lines.Skip(start).Take(end - start).Any(line => line.TrimStart().StartsWith("sysname ", StringComparison.Ordinal)) ||
            !lines.Skip(start).Take(end - start).Any(line => line.TrimStart().StartsWith("version ", StringComparison.Ordinal)))
            throw Failure("CONFIG_COMPLETE_CAPTURE_INCOMPLETE", "配置缺少完整 Comware 起止标记、版本或设备名。");
        if (lines.Skip(end + 1).Any(line => !string.IsNullOrWhiteSpace(line) && !Regex.IsMatch(line.Trim(), @"^<[^<>\r\n]+>$")))
            throw Failure("CONFIG_COMPLETE_CAPTURE_INCOMPLETE", "配置结束标记之后存在未知内容。");
        return string.Join('\n', lines.Skip(start).Take(end - start + 1).Select(line => line.TrimEnd()));
    }

    internal static string? Description(string text, string interfaceName)
    {
        var lines = Normalize(text).Split('\n');
        var indices = Enumerable.Range(0, lines.Length).Where(i => lines[i].Trim() == "interface " + interfaceName).ToArray();
        if (indices.Length != 1) throw Failure("CONFIG_CHANGE_INTERFACE_UNAVAILABLE", "接口配置不存在或不唯一。");
        var descriptions = lines.Skip(indices[0] + 1).TakeWhile(line => line.Trim() != "#")
            .Where(line => line.TrimStart().StartsWith("description ", StringComparison.Ordinal))
            .Select(line => line.Trim()["description ".Length..]).ToArray();
        if (descriptions.Length > 1 || descriptions.Any(value => !DescriptionPattern.IsMatch(value)))
            throw Failure("CONFIG_CHANGE_ROLLBACK_UNSUPPORTED", "原描述不符合可回滚格式。");
        return descriptions.SingleOrDefault();
    }

    private static string WithoutDescription(string text, string interfaceName)
    {
        _ = Description(text, interfaceName);
        var inside = false;
        return string.Join('\n', Normalize(text).Split('\n').Where(line =>
        {
            if (line.Trim() == "#") inside = false;
            if (line.Trim() == "interface " + interfaceName) inside = true;
            return !inside || !line.TrimStart().StartsWith("description ", StringComparison.Ordinal);
        }));
    }

    private SemaphoreSlim Gate(H3cDeviceProfileSnapshot target) => _gates.GetOrAdd(target.Host + ":" + target.Port, _ => new(1, 1));
    private string DirectoryFor(string profileId, bool create) => DirectoryAt(profileId, null, create);
    private string ChangeDirectory(string profileId, bool create) => DirectoryAt(profileId, "changes", create);
    private string DirectoryAt(string profileId, string? child, bool create)
    {
        if (!Safe(profileId)) throw Failure("CONFIG_CHANGE_INPUT_INVALID", "设备 ID 无效。", 400);
        var path = child is null ? Path.Combine(_root, profileId) : Path.Combine(_root, profileId, child);
        CheckPath(path);
        if (create) { Directory.CreateDirectory(path); CheckPath(path); }
        return path;
    }
    private void CheckPath(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw Failure("CONFIG_COMPLETE_PATH_UNSAFE", "配置存储路径不能包含链接。");
    }
    private void SaveChange(SimulatorConfigurationChange operation) => Write(Path.Combine(ChangeDirectory(operation.ProfileId, true), operation.Id + ".json"),
        JsonSerializer.SerializeToUtf8Bytes(operation, Json));
    private SimulatorConfigurationChange Finish(SimulatorConfigurationChange operation, WebUserIdentity actor)
    {
        operation = operation with { CompletedMs = Now() };
        SaveChange(operation);
        Audit(actor, "SIMULATOR_DESCRIPTION_CHANGE", operation.Id, operation.ProfileId, operation.State, operation.Error);
        return operation;
    }
    private void Write(string path, byte[] bytes)
    {
        CheckPath(path);
        var pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { stream.Write(bytes); stream.Flush(flushToDisk: true); }
        File.Move(pending, path, overwrite: true);
    }
    private void Audit(WebUserIdentity actor, string action, string id, string profileId, string state, string? detail = null) =>
        _catalog.TryRecordDeviceAuditEvent(actor.UserId, action, state, profileId, id, Array.Empty<string>(), detail ?? "simulator_configuration");
    private static string Error(Exception error) => error switch
    { ConfigRestoreException ex => ex.Code, TaskScopeRevokedException ex => ex.Reason, OperationCanceledException => "CANCELLED",
        _ => TransportFailure(error)?.Code ?? error.GetType().Name };
    private static ConfigRestoreException? TransportFailure(Exception error) => error switch
    {
        H3cCredentialUnavailableException => Failure("CONFIG_DEVICE_CREDENTIAL_UNAVAILABLE", "设备凭据未配置。", 409),
        H3cAuthenticationFailedException => Failure("CONFIG_DEVICE_AUTHENTICATION_FAILED", "设备认证失败。", 502),
        H3cHostKeyMismatchException or H3cHostKeyMissingException => Failure("CONFIG_DEVICE_HOST_KEY_FAILED", "设备主机密钥校验失败。", 409),
        TimeoutException => Failure("CONFIG_DEVICE_TIMEOUT", "设备连接或配置读取超时。", 504),
        H3cConnectionLostException or H3cCommandInterruptedException => Failure("CONFIG_DEVICE_CONNECTION_LOST", "设备连接中断。", 502),
        H3cUnsupportedAlgorithmException or H3cProtocolNegotiationException or H3cWorkerProtocolException =>
            Failure("CONFIG_DEVICE_PROTOCOL_FAILED", "设备协议协商或 Worker 响应失败。", 502),
        H3cOutputLimitException or H3cInvalidEncodingException or H3cCommandRejectedException or H3cPaginationDetectedException or H3cPartialOutputException =>
            Failure("CONFIG_DEVICE_CAPTURE_FAILED", "设备返回配置未完整通过读取校验。", 502),
        IOException or UnauthorizedAccessException => Failure("CONFIG_COMPLETE_IO_FAILED", "配置读取或存储失败。", 503),
        _ => null
    };
    private static bool Safe(string? id) => id is { Length: > 0 and <= 128 } && id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');
    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static ConfigRestoreException Failure(string code, string message, int status = 409) => new(code, message, status);
}
