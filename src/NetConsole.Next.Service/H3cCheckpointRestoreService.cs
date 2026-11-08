using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NetConsole.Next.Service;

internal sealed record H3cCheckpointRestoreRequest(string ProfileId, string ExpectedBackupId, string InterfaceName, string Description);
internal sealed record H3cCheckpointRestoreOperation(string Id, string ProfileId, string DeviceId, string SiteId,
    string InterfaceName, string State, string? CheckpointId, bool ConfigWriteSucceeded, bool ConfigReadbackVerified,
    bool ServiceVerified, bool RollbackVerified, string ServiceVerificationScope, string? BeforeBackupId,
    string? ChangedBackupId, string? RestoredBackupId, string? RollbackBackupId, string? Error,
    long CreatedMs, long? CompletedMs, string ActorUserId);

internal sealed partial class SimulatorConfigurationService
{
    internal const string CheckpointListCommand = "display configuration commit list";
    private const string CheckpointServiceScope = "INTERFACE_LINK_PROTOCOL_AND_LLDP_NEIGHBORS";

    public IReadOnlyList<H3cCheckpointRestoreOperation> CheckpointOperations(string profileId, WebUserIdentity actor, P2B5SiteScopeDecision scope)
    {
        _ = Target(profileId, actor, scope);
        var directory = DirectoryAt(profileId, "checkpoints", false);
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory, "*.json").Select(path =>
        {
            try { CheckPath(path); return JsonSerializer.Deserialize<H3cCheckpointRestoreOperation>(File.ReadAllText(path), Json); }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return null; }
        }).Where(item => item is not null && item.ProfileId == profileId && item.ActorUserId == actor.UserId)
            .Cast<H3cCheckpointRestoreOperation>().OrderByDescending(item => item.CreatedMs).Take(100).ToArray();
    }

    public async Task<H3cCheckpointRestoreOperation> VerifyNativeCheckpointRestoreAsync(H3cCheckpointRestoreRequest request,
        WebUserIdentity actor, P2B5SiteScopeDecision scope, CancellationToken cancellationToken)
    {
        if (request is null || !InterfacePattern.IsMatch(request.InterfaceName ?? "") ||
            request.Description is null || !DescriptionPattern.IsMatch(request.Description) || request.Description != request.Description.Trim())
            throw Failure("CONFIG_CHECKPOINT_INPUT_INVALID", "受控检查点验证需要有效接口和描述。", 400);
        var target = Target(request.ProfileId, actor, scope);
        if (target.Host != "192.168.56.3" || target.Port != 22 || target.TerminalProtocol != "SSH")
            throw Failure("CONFIG_CHECKPOINT_TARGET_UNSUPPORTED", "设备原生检查点验证当前仅支持授权 SW SSH 模拟器。");
        var expected = Read(request.ExpectedBackupId, target.Id) ?? throw Failure("CONFIG_COMPLETE_SOURCE_UNAVAILABLE", "完整基线备份不可读或校验失败。");
        var device = _catalog.GetTaskScopeTarget(target.Id, scope)!;
        Match(expected, target, device);
        var id = Guid.NewGuid().ToString("N");
        var execution = new TaskExecutionScope(_scopes.CaptureDefinition(target.Id, actor.UserId), actor.UserId, id);
        var operation = new H3cCheckpointRestoreOperation(id, target.Id, device.Id, device.SiteId, request.InterfaceName!,
            "PREPARING", null, false, false, false, false, CheckpointServiceScope, null, null, null, null, null, Now(), null, actor.UserId);
        var gate = Gate(target);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        CompleteConfigurationBackup? before = null;
        string? original = null;
        string? baselineServices = null;
        var mutationAttempted = false;
        try
        {
            SaveCheckpoint(operation);
            Require(execution, "CHECKPOINT_CONNECT");
            await using (var session = await _sessions.OpenAsync(target, cancellationToken).ConfigureAwait(false))
            {
                before = await Capture(session, target, execution, cancellationToken).ConfigureAwait(false);
                operation = operation with { BeforeBackupId = before.Id }; SaveCheckpoint(operation);
                original = Encoding.UTF8.GetString(VerifiedBytes(before, false));
                if (!before.Model.StartsWith("S6850", StringComparison.OrdinalIgnoreCase) ||
                    before.Model != expected.Model || before.SoftwareVersion != expected.SoftwareVersion ||
                    Normalize(original) != Normalize(Encoding.UTF8.GetString(VerifiedBytes(expected, false))))
                    throw Failure("CONFIG_CHECKPOINT_BASELINE_CHANGED", "当前配置、型号或版本与基线不一致，或不支持原生检查点。");
                var oldDescription = Description(original, request.InterfaceName!);
                if (oldDescription == request.Description) throw Failure("CONFIG_CHECKPOINT_NO_CHANGE", "验证描述必须与当前描述不同。");
                baselineServices = await CriticalServices(session, execution, cancellationToken).ConfigureAwait(false);
                var systemNames = Normalize(original).Split('\n').Select(line => line.Trim())
                    .Where(line => line.StartsWith("sysname ", StringComparison.Ordinal)).Select(line => line[8..].Trim()).ToArray();
                if (systemNames.Length != 1 || systemNames[0].Length == 0)
                    throw Failure("CONFIG_CHECKPOINT_LIST_UNRECOGNIZED", "完整配置中的设备提示符身份不唯一。");
                var prior = ParseCheckpointIds(await session.ReadAsync(CheckpointListCommand, cancellationToken).ConfigureAwait(false), systemNames[0]);
                Require(execution, "CHECKPOINT_CREATE");
                operation = operation with { State = "CHECKPOINT_CREATE_REQUIRES_REVIEW" }; SaveCheckpoint(operation);
                _ = await session.CommitConfigurationCheckpointAsync(cancellationToken).ConfigureAwait(false);
                Require(execution, "CHECKPOINT_IDENTIFY");
                var after = ParseCheckpointIds(await session.ReadAsync(CheckpointListCommand, cancellationToken).ConfigureAwait(false), systemNames[0]);
                var created = after.Except(prior, StringComparer.Ordinal).ToArray();
                if (created.Length == 0)
                    throw Failure("NATIVE_CHECKPOINT_NOT_CREATED", "设备执行 commit 后未产生新检查点，原生恢复不可验证；未执行接口描述变更。");
                if (created.Length != 1 || prior.Except(after, StringComparer.Ordinal).Any())
                    throw Failure("CONFIG_CHECKPOINT_ID_AMBIGUOUS", "无法唯一识别新建检查点，或已有检查点被替换；禁止配置变更。");
                operation = operation with { CheckpointId = created[0], State = "CHECKPOINT_CREATED" }; SaveCheckpoint(operation);
                if (!session.SupportsNativeCheckpointRestore)
                    throw Failure("CONFIG_CHECKPOINT_REPLACE_INTERACTION_UNVERIFIED", "尚未验证设备原生 replace 交互，禁止执行接口描述变更。");
                // Confirm checkpoint creation itself did not modify the running configuration.
                var checkpointCurrent = await Capture(session, target, execution, cancellationToken).ConfigureAwait(false);
                if (checkpointCurrent.RawSha256 != before.RawSha256)
                    throw Failure("CONFIG_CHECKPOINT_CURRENT_CHANGED", "创建检查点期间运行配置已变化，禁止继续。");
                Require(execution, "CHECKPOINT_CONTROLLED_WRITE");
                operation = operation with { State = "CONFIG_WRITE_REQUIRES_REVIEW" }; SaveCheckpoint(operation);
                mutationAttempted = true;
                await session.SetDescriptionAsync(request.InterfaceName!, request.Description, cancellationToken).ConfigureAwait(false);
                operation = operation with { State = "CONFIG_WRITE_SUCCEEDED", ConfigWriteSucceeded = true }; SaveCheckpoint(operation);
                var changed = await Capture(session, target, execution, cancellationToken).ConfigureAwait(false);
                operation = operation with { ChangedBackupId = changed.Id }; SaveCheckpoint(operation);
                var changedText = Encoding.UTF8.GetString(VerifiedBytes(changed, false));
                if (Description(changedText, request.InterfaceName!) != request.Description ||
                    WithoutDescription(changedText, request.InterfaceName!) != WithoutDescription(original, request.InterfaceName!))
                    throw Failure("CONFIG_CHECKPOINT_CONTROLLED_READBACK_FAILED", "受控变更读取校验失败。");
                Require(execution, "CHECKPOINT_REPLACE");
                operation = operation with { State = "CHECKPOINT_RESTORE_REQUIRES_REVIEW" }; SaveCheckpoint(operation);
                _ = await session.RestoreConfigurationCheckpointAsync(operation.CheckpointId!, cancellationToken).ConfigureAwait(false);
            }
            // A fresh transport is mandatory after the native replacement boundary.
            Require(execution, "CHECKPOINT_RECONNECT");
            await using (var verify = await _sessions.OpenAsync(target, cancellationToken).ConfigureAwait(false))
            {
                var restored = await Capture(verify, target, execution, cancellationToken).ConfigureAwait(false);
                operation = operation with { RestoredBackupId = restored.Id };
                if (restored.RawSha256 != before.RawSha256 || restored.Model != before.Model || restored.SoftwareVersion != before.SoftwareVersion)
                    throw Failure("CONFIG_CHECKPOINT_RESTORE_READBACK_FAILED", "原生恢复后完整配置或设备版本不一致。");
                operation = operation with { ConfigReadbackVerified = true, State = "CONFIG_READBACK_VERIFIED" }; SaveCheckpoint(operation);
                var restoredServices = await CriticalServices(verify, execution, cancellationToken).ConfigureAwait(false);
                if (restoredServices != baselineServices) throw Failure("CONFIG_CHECKPOINT_SERVICE_CHECK_FAILED", "恢复后的接口链路、协议或 LLDP 关键探针与基线不一致。");
                return FinishCheckpoint(operation with { ServiceVerified = true, State = "SERVICE_VERIFIED" }, actor);
            }
        }
        catch (Exception error)
        {
            operation = operation with { Error = CheckpointError(error), State = mutationAttempted ? "ROLLBACK_REQUIRED" : "FAILED" };
            SaveCheckpoint(operation);
            if (mutationAttempted && before is not null && operation.CheckpointId is not null)
            {
                using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                try
                {
                    Require(execution, "CHECKPOINT_ROLLBACK");
                    await using (var restore = await _sessions.OpenAsync(target, recovery.Token).ConfigureAwait(false))
                        _ = await restore.RestoreConfigurationCheckpointAsync(operation.CheckpointId, recovery.Token).ConfigureAwait(false);
                    Require(execution, "CHECKPOINT_ROLLBACK_RECONNECT");
                    await using var verify = await _sessions.OpenAsync(target, recovery.Token).ConfigureAwait(false);
                    var rollback = await Capture(verify, target, execution, recovery.Token).ConfigureAwait(false);
                    operation = operation with { RollbackBackupId = rollback.Id };
                    var configMatches = rollback.RawSha256 == before.RawSha256 && rollback.Model == before.Model && rollback.SoftwareVersion == before.SoftwareVersion;
                    var serviceMatches = await CriticalServices(verify, execution, recovery.Token).ConfigureAwait(false) == baselineServices;
                    operation = operation with { RollbackVerified = configMatches && serviceMatches, State = configMatches && serviceMatches ? "ROLLBACK_VERIFIED" : "ROLLBACK_FAILED" };
                }
                catch (Exception rollbackError) { operation = operation with { State = "ROLLBACK_FAILED", Error = operation.Error + ";" + CheckpointError(rollbackError) }; }
            }
            return FinishCheckpoint(operation, actor);
        }
        finally { gate.Release(); }
    }

    internal static IReadOnlyList<string> ParseCheckpointIds(string output, string systemName)
    {
        if (output.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
            throw Failure("CONFIG_CHECKPOINT_LIST_UNRECOGNIZED", "检查点列表包含未知控制字符。");
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Split('\n').Select(line => line.Trim()).ToList();
        while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        var userPrompt = "<" + systemName + ">";
        var configPrompt = "[" + systemName + "]";
        bool IsPrompt(string line) => line == userPrompt || line == configPrompt;
        // Netmiko deliberately retains command echoes and prompts. Remove only one
        // boundary echo of this exact command and prompts matching the verified
        // current sysname; unexpected commands, names and interior content remain.
        if (lines.Count > 1 && IsPrompt(lines[0]) && lines[1] == CheckpointListCommand) lines.RemoveAt(0);
        if (lines.Count > 0 && (lines[0] == CheckpointListCommand || lines[0] == userPrompt + CheckpointListCommand || lines[0] == configPrompt + CheckpointListCommand))
            lines.RemoveAt(0);
        if (lines.Count > 0 && IsPrompt(lines[^1])) lines.RemoveAt(lines.Count - 1);
        if (lines.Count == 1 && Regex.IsMatch(lines[0], @"\ANo commit point exists\.?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return [];
        // Fail closed until a supported table layout is recognized. IDs remain strings
        // to preserve the exact device identifier and avoid integer truncation.
        if (lines.Count < 2 || !Regex.IsMatch(lines[0], @"\ACommit\s+ID\s+\S.*\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw Failure("CONFIG_CHECKPOINT_LIST_UNRECOGNIZED", "无法识别设备检查点列表。");
        var ids = new List<string>();
        foreach (var line in lines.Skip(1))
        {
            if (Regex.IsMatch(line, @"\A[- ]+\z", RegexOptions.CultureInvariant)) continue;
            var match = Regex.Match(line, @"\A(?<id>[0-9]{1,20})\s+\S.*\z", RegexOptions.CultureInvariant);
            if (!match.Success) throw Failure("CONFIG_CHECKPOINT_LIST_UNRECOGNIZED", "检查点列表包含未知内容。");
            ids.Add(match.Groups["id"].Value);
        }
        if (ids.Count == 0 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
            throw Failure("CONFIG_CHECKPOINT_LIST_UNRECOGNIZED", "检查点列表为空、重复或无法识别。");
        return ids;
    }

    private async Task<string> CriticalServices(ISimulatorConfigurationSession session, TaskExecutionScope execution, CancellationToken token)
    {
        Require(execution, "CHECKPOINT_SERVICE_PROBES");
        var interfaces = H3cComwareParser.ParseInterfaceBrief(await session.ReadAsync(H3cCommandPolicy.DisplayInterfaceBrief, token).ConfigureAwait(false));
        if (!interfaces.Recognized) throw Failure("CONFIG_CHECKPOINT_SERVICE_PROBE_UNAVAILABLE", "接口关键探针无法识别。");
        Require(execution, "CHECKPOINT_LLDP_PROBE");
        var lldpText = await session.ReadAsync(H3cCommandPolicy.DisplayLldpNeighborInformationList, token).ConfigureAwait(false);
        var transcript = CliTranscriptParser.Parse(lldpText, H3cCommandPolicy.DisplayLldpNeighborInformationList, completedExec: true);
        var table = H3cComwareParser.ParseLldpListTable(lldpText, completedExec: true);
        var explicitEmpty = (transcript.Status == CliTranscriptStatus.Complete ||
            transcript.Status == CliTranscriptStatus.Incomplete && transcript.Prompt.Length == 0) &&
            !CliTranscriptParser.HasCommandBoundaryMismatch(lldpText, H3cCommandPolicy.DisplayLldpNeighborInformationList) &&
            Regex.IsMatch(transcript.Output.Trim(), @"\A(?:Total\s*(?:number\s+of\s+neighbors)?\s*:\s*0|No\s+(?:LLDP\s+)?neighbors?)\.?\z",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (table.Status != "PARSED" && !explicitEmpty)
            throw Failure("CONFIG_CHECKPOINT_SERVICE_PROBE_UNAVAILABLE", "LLDP 关键探针无法识别，不能将空输出视为业务正常。");
        var lldp = table.Neighbors;
        return JsonSerializer.Serialize(new
        {
            interfaces = interfaces.Interfaces.OrderBy(item => item.InterfaceName, StringComparer.Ordinal).Select(item => new { item.InterfaceName, item.LinkStatus, item.ProtocolStatus, item.IpAddress, item.Speed, item.Duplex, item.PortMode, item.Vlan }),
            neighbors = lldp.OrderBy(item => item.LocalInterface, StringComparer.Ordinal).ThenBy(item => item.NeighborInterface, StringComparer.Ordinal).ThenBy(item => item.ChassisId, StringComparer.Ordinal)
        }, Json);
    }
    private void SaveCheckpoint(H3cCheckpointRestoreOperation operation) => Write(Path.Combine(DirectoryAt(operation.ProfileId, "checkpoints", true), operation.Id + ".json"), JsonSerializer.SerializeToUtf8Bytes(operation, Json));
    private static string CheckpointError(Exception error) => error is NotSupportedException
        ? error.Message == "CONFIG_CHECKPOINT_REPLACE_INTERACTION_UNVERIFIED" ? error.Message : "CONFIG_CHECKPOINT_SESSION_UNSUPPORTED"
        : Error(error);
    private H3cCheckpointRestoreOperation FinishCheckpoint(H3cCheckpointRestoreOperation operation, WebUserIdentity actor)
    {
        operation = operation with { CompletedMs = Now() }; SaveCheckpoint(operation);
        Audit(actor, "CONFIG_NATIVE_CHECKPOINT_RESTORE", operation.Id, operation.ProfileId, operation.State, operation.Error);
        return operation;
    }
}
