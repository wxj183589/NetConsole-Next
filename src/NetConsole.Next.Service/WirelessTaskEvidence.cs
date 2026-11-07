namespace NetConsole.Next.Service;

internal sealed partial class Catalog
{
    internal (WirelessProjectionSnapshot Projection, WirelessTaskExportEvidence Evidence) ReadWirelessTaskEvidence(
        WirelessTaskReport report, P2B5SiteScopeDecision scope)
    {
        var acs = new List<WirelessAcDeviceSeed>();
        var observations = new List<WirelessObservationSeed>();
        var rawFiles = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var rawRuns = new List<object>();
        foreach (var item in report.Items)
        {
            if (item.RunId is not { } runId)
            {
                rawRuns.Add(new { item.DeviceId, runId = (string?)null, status = "NOT_PUBLISHED", reason = "RUN_NOT_CREATED" });
                continue;
            }
            var evidence = ReadTaskDiagnosticEvidence(runId, scope, CancellationToken.None);
            if (evidence is null)
            {
                rawRuns.Add(new { item.DeviceId, runId, status = "NOT_PUBLISHED", reason = "DIAGNOSTIC_EVIDENCE_UNAVAILABLE" });
                continue;
            }
            var detail = GetDeviceInventoryDetail(item.DeviceId, scope);
            if (detail is null || detail.TerminalProfileId != evidence.Access.Scope.ProfileId ||
                detail.Item.Id != evidence.Access.Scope.DeviceId)
                throw new TaskScopeRevokedException("WIRELESS_TASK_EVIDENCE_SCOPE_REVOKED");
            rawRuns.Add(new { item.DeviceId, runId, evidence.Manifest.Status, evidence.Manifest.Commands });
            foreach (var output in evidence.Outputs)
            {
                var name = $"raw-results/{runId}/command-{output.Key + 1:000}.txt";
                rawFiles.Add(name, output.Value);
            }
            if (rawFiles.Values.Sum(bytes => (long)bytes.Length) > 32L * 1024 * 1024)
                throw new TaskDiagnosticException("DIAGNOSTIC_SIZE_LIMIT", "无线任务证据超过大小上限。", 413);

            var device = detail.Item;
            acs.Add(new(device.Id, device.Name, device.SiteId, device.ManagementIp, device.Vendor, device.Model,
                device.ConnectionStatus, detail.TerminalProfileId, null, device.Enabled));
            // Only project facts when every sealed output passed the same verification used by diagnostics.
            if (evidence.Manifest.Status != "COMPLETE" || evidence.Manifest.Commands.Any(command => !command.CanDownload)) continue;
            var facts = ReadWirelessRunFacts(detail.TerminalProfileId!, runId, scope);
            var ap = facts.FirstOrDefault(f => f.Kind == "command.display.wlan.ap.all");
            var address = facts.FirstOrDefault(f => f.Kind == "command.display.wlan.ap.all.address");
            if (ap is null || address is null || ap.CapturedMs != address.CapturedMs) continue;
            var version = ReadVersion(facts.FirstOrDefault(f => f.Kind == "device.identity")?.PayloadJson);
            var addresses = ParseAddressFacts(address.PayloadJson);
            foreach (var row in H3cComwareParser.ParseWlanApAll(ReadOutput(ap.PayloadJson)))
            {
                addresses.TryGetValue(row.Name, out var macs);
                var state = (row.State ?? "").Trim().ToUpperInvariant();
                var normalized = state switch { "R/M" or "RM" => "RUNNING_MASTER", "R/B" or "RB" => "RUNNING_BACKUP", "I" when version is not null => "OFFLINE", _ => "UNKNOWN" };
                observations.Add(new(device.Id, device.Name, device.SiteId, device.Model, version, row.Name,
                    row.Serial, macs ?? Array.Empty<string>(), row.Model, row.Group, state, normalized,
                    normalized == "UNKNOWN" ? "LOW" : "HIGH", ap.CapturedMs, "fact:" + ap.Id, "h3c.ap.v1", ap.Id));
            }
        }
        foreach (var retry in report.Retries ?? Array.Empty<WirelessTaskRetryResult>())
        {
            var evidence = ReadTaskDiagnosticEvidence(retry.RetryRunId, scope, CancellationToken.None);
            if (evidence is null)
            {
                rawRuns.Add(new { retry.DeviceId, runId = retry.RetryRunId, status = "NOT_PUBLISHED", reason = "DIAGNOSTIC_EVIDENCE_UNAVAILABLE", retry = true });
                continue;
            }
            var detail = GetDeviceInventoryDetail(retry.DeviceId, scope);
            if (detail is null || detail.TerminalProfileId != evidence.Access.Scope.ProfileId ||
                detail.Item.Id != evidence.Access.Scope.DeviceId)
                throw new TaskScopeRevokedException("WIRELESS_TASK_EVIDENCE_SCOPE_REVOKED");
            rawRuns.Add(new { retry.DeviceId, runId = retry.RetryRunId, evidence.Manifest.Status, evidence.Manifest.Commands, retry = true });
            acs.Add(new(detail.Item.Id, detail.Item.Name, detail.Item.SiteId, detail.Item.ManagementIp,
                detail.Item.Vendor, detail.Item.Model, detail.Item.ConnectionStatus, detail.TerminalProfileId, null, detail.Item.Enabled));
            foreach (var output in evidence.Outputs)
                rawFiles.TryAdd($"raw-results/{retry.RetryRunId}/command-{output.Key + 1:000}.txt", output.Value);
            if (rawFiles.Values.Sum(bytes => (long)bytes.Length) > 32L * 1024 * 1024)
                throw new TaskDiagnosticException("DIAGNOSTIC_SIZE_LIMIT", "无线任务证据超过大小上限。", 413);
            if (evidence.Manifest.Status != "COMPLETE" || evidence.Manifest.Commands.Any(command => !command.CanDownload)) continue;
            var retryFacts = ReadWirelessRunFacts(detail.TerminalProfileId!, retry.RetryRunId, scope);
            var retryAp = retryFacts.FirstOrDefault(f => f.Kind == "command.display.wlan.ap.all");
            var retryAddress = retryFacts.FirstOrDefault(f => f.Kind == "command.display.wlan.ap.all.address");
            if (retryAp is null || retryAddress is null || retryAp.CapturedMs != retryAddress.CapturedMs) continue;
            var retryVersion = ReadVersion(retryFacts.FirstOrDefault(f => f.Kind == "device.identity")?.PayloadJson);
            var retryAddresses = ParseAddressFacts(retryAddress.PayloadJson);
            foreach (var row in H3cComwareParser.ParseWlanApAll(ReadOutput(retryAp.PayloadJson)))
            {
                retryAddresses.TryGetValue(row.Name, out var macs);
                var state = (row.State ?? "").Trim().ToUpperInvariant();
                var normalized = state switch { "R/M" or "RM" => "RUNNING_MASTER", "R/B" or "RB" => "RUNNING_BACKUP", "I" when retryVersion is not null => "OFFLINE", _ => "UNKNOWN" };
                observations.Add(new(detail.Item.Id, detail.Item.Name, detail.Item.SiteId, detail.Item.Model, retryVersion, row.Name,
                    row.Serial, macs ?? Array.Empty<string>(), row.Model, row.Group, state, normalized,
                    normalized == "UNKNOWN" ? "LOW" : "HIGH", retryAp.CapturedMs, "fact:" + retryAp.Id, "h3c.ap.v1", retryAp.Id));
            }
        }
        var projection = new WirelessProjectionEngine().Project(acs, observations, Array.Empty<WirelessTopologyEvidence>(),
            new WirelessScopeView(scope.State, scope.ClaimsRevision ?? ""));
        return (projection, new(rawFiles, rawRuns));
    }

    private IReadOnlyList<H3cFactSnapshot> ReadWirelessRunFacts(string profileId, string runId, P2B5SiteScopeDecision scope)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        var parameters = new List<(string Name, object? Value)>();
        var predicate = H3cFactScopePredicate(scope, parameters, "h3c_facts", true);
        command.CommandText = $@"SELECT id,profile_id,kind,identity_key,status,source,captured_ms,is_current,payload_json
            FROM h3c_facts WHERE profile_id=$profile AND source=$source AND {predicate}
            AND status NOT IN ('PARTIAL','FAILED','UNSUPPORTED','EMPTY') AND {H3cFactRawVerificationPredicate("h3c_facts")}
            AND kind IN ('device.identity','command.display.wlan.ap.all','command.display.wlan.ap.all.address')
            ORDER BY captured_ms DESC,id DESC";
        command.Parameters.AddWithValue("$profile", profileId);
        command.Parameters.AddWithValue("$source", "H3C_TASK_RAW:" + runId);
        DeviceSiteScope.AddParameters(command, parameters);
        using var reader = command.ExecuteReader();
        var facts = new List<H3cFactSnapshot>();
        while (reader.Read()) facts.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.GetInt64(6), reader.GetInt32(7) != 0, reader.GetString(8)));
        return facts;
    }
}
