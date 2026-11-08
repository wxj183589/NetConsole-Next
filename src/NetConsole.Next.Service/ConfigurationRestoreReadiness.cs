using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace NetConsole.Next.Service;

internal sealed record ConfigurationRestoreCapabilities(bool NativeFileReplaceAdvertised, bool SftpAvailable,
    long? StartupFileBytes, string TransferState);
internal sealed record ConfigurationRestorePreflightRequest(IReadOnlyList<ConfigurationRestoreSource> Sources);
internal sealed record ConfigurationRestoreSource(string ProfileId, string BackupId);
internal sealed record ConfigurationRestorePhase(string Name, string State, long ElapsedMs, string? Error = null);
internal sealed record ConfigurationRestorePreflight(string Id, string BatchId, string ProfileId, string BackupId,
    string ActorUserId, string State, long CreatedMs, long? CompletedMs, long ElapsedMs,
    bool SourceIntegrityVerified, bool DeviceIdentityVerified, bool IndependentReadbackStable, bool BusinessBaselineStable,
    bool SourceMatchesCurrent, string? SourceSha256, string? FirstBackupId, string? SecondBackupId,
    string? FirstSha256, string? SecondSha256, string? BusinessBaselineSha256,
    ConfigurationRestoreCapabilities? Capabilities, string CheckpointState,
    IReadOnlyList<string> Blockers, IReadOnlyList<ConfigurationRestorePhase> Phases, string? Error = null)
{
    // Preflight reads do not constitute any post-restore acceptance phase.
    public bool WriteAllowed => false;
    public bool Restorable => false;
    public bool WritePass => false;
    public bool ReadbackPass => false;
    public bool ConfigHashPass => false;
    public bool BusinessProbePass => false;
    public bool RollbackPass => false;
    public string BusinessProbeScope => "INTERFACE_LINK_PROTOCOL_AND_LLDP_NEIGHBORS";
}
internal sealed record ConfigurationRestorePreflightBatch(string Id, string State, long CreatedMs,
    long CompletedMs, IReadOnlyList<ConfigurationRestorePreflight> Items);

internal sealed partial class SimulatorConfigurationService
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _activeRestorePreflights = new(StringComparer.Ordinal);

    public async Task<ConfigurationRestorePreflightBatch> PreflightRestoresAsync(ConfigurationRestorePreflightRequest request,
        WebUserIdentity actor, P2B5SiteScopeDecision scope, CancellationToken token)
    {
        if (request?.Sources is not { Count: > 0 and <= 16 } sources ||
            sources.Any(item => item is null || !Safe(item.ProfileId) || !Safe(item.BackupId)) ||
            sources.Select(item => item.ProfileId).Distinct(StringComparer.Ordinal).Count() != sources.Count)
            throw Failure("CONFIG_RESTORE_PREFLIGHT_INPUT_INVALID", "请选择 1 至 16 台设备，每台设备一个完整历史原件。", 400);
        // Freeze every target before the first network read; later rows may not
        // inherit an endpoint or site changed while an earlier row was running.
        var frozen = sources.Select(item =>
        {
            _ = Target(item.ProfileId, actor, scope);
            return _scopes.CaptureDefinition(item.ProfileId, actor.UserId);
        }).ToArray();
        var batchId = Guid.NewGuid().ToString("N");
        var created = Now();
        var results = new List<ConfigurationRestorePreflight>();
        for (var i = 0; i < sources.Count; i++)
            results.Add(await PreflightRestore(sources[i], frozen[i], batchId, actor, scope, token).ConfigureAwait(false));
        var state = results.All(item => item.State == "BLOCKED") ? "BLOCKED"
            : results.All(item => item.State == "CANCELLED") ? "CANCELLED"
            : results.Any(item => item.State == "BLOCKED") ? "PARTIAL" : "FAILED";
        return new(batchId, state, created, Now(), results);
    }

    public IReadOnlyList<ConfigurationRestorePreflight> RestorePreflightHistory(string profileId,
        WebUserIdentity actor, P2B5SiteScopeDecision scope)
    {
        _ = Target(profileId, actor, scope);
        var directory = DirectoryAt(profileId, "restore-preflight", false);
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory, "*.json").Select(path =>
        {
            try
            {
                CheckPath(path);
                if (new FileInfo(path).Length > 64 * 1024) return null;
                var item = JsonSerializer.Deserialize<ConfigurationRestorePreflight>(File.ReadAllText(path), Json);
                if (item is null || item.ProfileId != profileId || item.ActorUserId != actor.UserId ||
                    !Safe(item.Id) || !Safe(item.BatchId) || !Safe(item.BackupId) || Path.GetFileNameWithoutExtension(path) != item.Id ||
                    item.Phases is null || item.Phases.Count > 16 || item.Phases.Any(phase => phase is null || phase.ElapsedMs < 0) ||
                    item.Blockers is null || item.Blockers.Count > 16 || item.ElapsedMs < 0 ||
                    item.State is not ("RUNNING" or "BLOCKED" or "FAILED" or "CANCELLED")) return null;
                return item.CompletedMs is null && !_activeRestorePreflights.ContainsKey(item.Id)
                    ? item with { State = "INTERRUPTED", Error = "CONFIG_RESTORE_PREFLIGHT_INTERRUPTED" } : item;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ConfigRestoreException)
            { return null; }
        }).Where(item => item is not null).Cast<ConfigurationRestorePreflight>()
            .OrderByDescending(item => item.CreatedMs).Take(100).ToArray();
    }

    private async Task<ConfigurationRestorePreflight> PreflightRestore(ConfigurationRestoreSource source,
        TaskScopeSnapshot frozen, string batchId, WebUserIdentity actor, P2B5SiteScopeDecision scope, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        var phases = new List<ConfigurationRestorePhase>();
        var operation = new ConfigurationRestorePreflight(Guid.NewGuid().ToString("N"), batchId, source.ProfileId,
            source.BackupId, actor.UserId, "RUNNING", Now(), null, 0, false, false, false, false, false,
            null, null, null, null, null, null, null, "NOT_CHECKED", [], phases);
        var execution = new TaskExecutionScope(frozen, actor.UserId, operation.Id);
        SemaphoreSlim? gate = null;
        var entered = false;
        var phase = "SOURCE_INTEGRITY";
        var phaseStarted = clock.ElapsedMilliseconds;
        _activeRestorePreflights.TryAdd(operation.Id, 0);
        try
        {
            SaveRestorePreflight(operation);
            token.ThrowIfCancellationRequested();
            Require(execution, "RESTORE_PREFLIGHT_ADMISSION");
            var target = Target(source.ProfileId, actor, scope);
            var backup = Read(source.BackupId, source.ProfileId)
                ?? throw Failure("CONFIG_COMPLETE_SOURCE_UNAVAILABLE", "完整历史原件不可读或完整性校验失败。");
            Match(backup, target, _catalog.GetTaskScopeTarget(target.Id, scope)!);
            var sourceText = Normalize(Encoding.UTF8.GetString(VerifiedBytes(backup, false)));
            operation = operation with { SourceIntegrityVerified = true, SourceSha256 = backup.RawSha256 };
            CompletePhase("PASS");
            gate = Gate(target);
            phase = "CONNECT_AND_CAPTURE"; phaseStarted = clock.ElapsedMilliseconds;
            await gate.WaitAsync(token).ConfigureAwait(false); entered = true;
            token.ThrowIfCancellationRequested();
            Require(execution, "RESTORE_PREFLIGHT_CONNECT");
            CompleteConfigurationBackup first;
            string baseline;
            await using (var session = await _sessions.OpenAsync(target, token).ConfigureAwait(false))
            {
                Require(execution, "RESTORE_PREFLIGHT_CAPTURE");
                first = await Capture(session, target, execution, token).ConfigureAwait(false);
                operation = operation with { FirstBackupId = first.Id, FirstSha256 = first.RawSha256 };
                if (first.Model != backup.Model || first.SoftwareVersion != backup.SoftwareVersion)
                    throw Failure("CONFIG_RESTORE_MODEL_OR_VERSION_MISMATCH", "历史原件与当前设备型号或软件版本不一致。");
                operation = operation with { DeviceIdentityVerified = true,
                    SourceMatchesCurrent = sourceText == Normalize(Encoding.UTF8.GetString(VerifiedBytes(first, false))) };
                CompletePhase("PASS");
                phase = "BUSINESS_BASELINE"; phaseStarted = clock.ElapsedMilliseconds;
                baseline = await CriticalServices(session, execution, token).ConfigureAwait(false);
                operation = operation with { BusinessBaselineSha256 = Hash(Encoding.UTF8.GetBytes(baseline)) };
                CompletePhase("PASS");
                phase = "NATIVE_AND_TRANSFER_CAPABILITIES"; phaseStarted = clock.ElapsedMilliseconds;
                Require(execution, "RESTORE_PREFLIGHT_CAPABILITIES");
                var capabilities = await session.ProbeRestoreCapabilitiesAsync(token).ConfigureAwait(false);
                var checkpointState = "LIST_UNRECOGNIZED";
                try
                {
                    var name = Normalize(Encoding.UTF8.GetString(VerifiedBytes(first, false))).Split('\n')
                        .Select(line => line.Trim()).Where(line => line.StartsWith("sysname ", StringComparison.Ordinal)).Single()[8..];
                    var ids = ParseCheckpointIds(await session.ReadAsync(CheckpointListCommand, token).ConfigureAwait(false), name);
                    checkpointState = ids.Count == 0 ? "NO_COMMIT_POINT" : "COMMIT_POINTS_PRESENT_NOT_RESTORE_VERIFIED";
                }
                catch (ConfigRestoreException ex) when (ex.Code == "CONFIG_CHECKPOINT_LIST_UNRECOGNIZED") { }
                operation = operation with { Capabilities = capabilities, CheckpointState = checkpointState };
                CompletePhase("READ_ONLY_COMPLETE");
            }
            // Dispose first transport before independently reading the full
            // original and business probes. No prior response is reused.
            phase = "INDEPENDENT_RECONNECT"; phaseStarted = clock.ElapsedMilliseconds;
            Require(execution, "RESTORE_PREFLIGHT_RECONNECT");
            await using (var verify = await _sessions.OpenAsync(target, token).ConfigureAwait(false))
            {
                var second = await Capture(verify, target, execution, token).ConfigureAwait(false);
                operation = operation with { SecondBackupId = second.Id, SecondSha256 = second.RawSha256 };
                if (first.RawSha256 != second.RawSha256 || first.Model != second.Model || first.SoftwareVersion != second.SoftwareVersion)
                    throw Failure("CONFIG_RESTORE_PREFLIGHT_READBACK_CHANGED", "两次独立连接之间完整配置或版本已变化。");
                operation = operation with { IndependentReadbackStable = true };
                CompletePhase("PASS");
                phase = "INDEPENDENT_BUSINESS_PROBES"; phaseStarted = clock.ElapsedMilliseconds;
                if (baseline != await CriticalServices(verify, execution, token).ConfigureAwait(false))
                    throw Failure("CONFIG_RESTORE_PREFLIGHT_BUSINESS_CHANGED", "独立连接之间接口或 LLDP 业务基线发生变化。");
                operation = operation with { BusinessBaselineStable = true };
                CompletePhase("PASS");
            }
            var blockers = new List<string>();
            if (operation.Capabilities?.NativeFileReplaceAdvertised != true) blockers.Add("NATIVE_FILE_REPLACE_NOT_ADVERTISED");
            if (operation.Capabilities?.SftpAvailable != true) blockers.Add("SFTP_SUBSYSTEM_UNAVAILABLE");
            blockers.Add("DEVICE_FILE_UPLOAD_AND_HASH_UNVERIFIED");
            blockers.Add("NATIVE_FILE_REPLACE_INTERACTION_UNVERIFIED");
            blockers.Add("POST_RESTORE_AND_ROLLBACK_UNVERIFIED");
            operation = operation with { State = "BLOCKED", Blockers = blockers };
        }
        catch (Exception error)
        {
            var cancelled = error is OperationCanceledException || token.IsCancellationRequested;
            var code = cancelled ? "CONFIG_RESTORE_PREFLIGHT_CANCELLED" : Error(error);
            phases.Add(new(phase, cancelled ? "CANCELLED" : "FAILED", clock.ElapsedMilliseconds - phaseStarted, code));
            operation = operation with { State = cancelled ? "CANCELLED" : "FAILED", Error = code, Blockers = [code] };
        }
        finally { if (entered) gate!.Release(); }
        operation = operation with { CompletedMs = Now(), ElapsedMs = clock.ElapsedMilliseconds, Phases = phases.ToArray() };
        try { SaveRestorePreflight(operation); }
        finally { _activeRestorePreflights.TryRemove(operation.Id, out _); }
        Audit(actor, "CONFIG_RESTORE_PREFLIGHT", operation.Id, operation.ProfileId, operation.State, operation.Error);
        return operation;

        void CompletePhase(string state)
        {
            phases.Add(new(phase, state, clock.ElapsedMilliseconds - phaseStarted));
            operation = operation with { Phases = phases.ToArray(), ElapsedMs = clock.ElapsedMilliseconds };
            SaveRestorePreflight(operation);
        }
    }

    private void SaveRestorePreflight(ConfigurationRestorePreflight operation)
    {
        lock (_storageGate)
        {
            var directory = DirectoryAt(operation.ProfileId, "restore-preflight", true);
            Write(Path.Combine(directory, operation.Id + ".json"), JsonSerializer.SerializeToUtf8Bytes(operation, Json));
            // Retain bounded metadata only; never retire configuration originals.
            foreach (var path in Directory.EnumerateFiles(directory, "*.json")
                .Where(path => (operation.CompletedMs is not null && Path.GetFileNameWithoutExtension(path) == operation.Id) ||
                    !_activeRestorePreflights.ContainsKey(Path.GetFileNameWithoutExtension(path)))
                .OrderByDescending(File.GetLastWriteTimeUtc).Skip(100))
            { CheckPath(path); File.Delete(path); }
        }
    }
}
