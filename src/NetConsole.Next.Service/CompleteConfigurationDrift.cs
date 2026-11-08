using System.Text;

namespace NetConsole.Next.Service;

internal sealed record CompleteConfigurationDrift(string ProfileId, string DeviceId, string SiteId,
    string State, string Reason, string? BeforeBackupId = null, string? AfterBackupId = null,
    int? BeforeVersion = null, int? AfterVersion = null, long? BeforeCapturedMs = null, long? AfterCapturedMs = null,
    bool? ConfigurationChanged = null, bool? SensitiveValuesChanged = null, bool? ModelOrSoftwareChanged = null,
    IReadOnlyList<string>? Categories = null);

internal static class CompleteConfigurationFaultProjection
{
    internal const string Authority = "CONFIG_COMPLETE_BACKUP";
    internal static bool IsFreshPair(CompleteConfigurationDrift? drift, string deviceId, string profileId,
        string siteId, long now) => drift is not null && drift.DeviceId == deviceId && drift.ProfileId == profileId &&
        drift.SiteId == siteId && !string.IsNullOrWhiteSpace(drift.BeforeBackupId) &&
        !string.IsNullOrWhiteSpace(drift.AfterBackupId) && drift.BeforeBackupId != drift.AfterBackupId &&
        drift.BeforeVersion is > 0 && drift.AfterVersion == (long)drift.BeforeVersion + 1 &&
        drift.BeforeCapturedMs is > 0 && drift.AfterCapturedMs is { } at && at >= drift.BeforeCapturedMs &&
        at <= now && now - at <= OperationalHealthProjection.FreshnessWindowMs;

    internal static OperationalFaultEvent? Changed(CompleteConfigurationDrift? drift, DeviceInventoryItem device, long now)
    {
        if (device.TerminalProfileId is not { } profile || !device.Enabled ||
            !IsFreshPair(drift, device.Id, profile, device.SiteId, now) || drift!.State != "CHANGED" ||
            !(drift.ConfigurationChanged == true || drift.ModelOrSoftwareChanged == true)) return null;
        return new("configuration:" + drift.AfterBackupId, device.Id, device.SiteId, "CONFIGURATION_CHANGED",
            "WARNING", "OBSERVED", drift.AfterCapturedMs!.Value, Authority, drift.AfterBackupId!,
            $"连续完整备份 v{drift.BeforeVersion} → v{drift.AfterVersion} 检出配置或版本变化；敏感值不在故障摘要中展示。",
            profile, Authority, device.SiteId);
    }

    // This confirms stability, not rollback to the configuration before the alert.
    internal static string? StableEvidence(FaultIncident item, CompleteConfigurationDrift? drift, long now) =>
        item.Kind == "CONFIGURATION_CHANGED" && item.ResourceKey == item.ProfileId &&
        item.SourceAuthority == Authority && item.CapturedScopeKey == item.SiteId &&
        IsFreshPair(drift, item.DeviceId, item.ProfileId, item.SiteId, now) &&
        drift!.State == "UNCHANGED" && drift.ConfigurationChanged == false && drift.SensitiveValuesChanged == false &&
        drift.ModelOrSoftwareChanged == false && drift.AfterCapturedMs > item.LastSeenMs &&
        (drift.BeforeCapturedMs > item.LastSeenMs ||
            drift.BeforeCapturedMs == item.LastSeenMs && drift.BeforeBackupId == item.EvidenceId)
            ? drift.AfterBackupId : null;
}

internal sealed partial class SimulatorConfigurationService
{
    // Read-only detection also covers scheduled/bulk captures. Never skip a damaged
    // latest snapshot to manufacture an apparently unchanged current configuration.
    public CompleteConfigurationDrift DetectDrift(string profileId, WebUserIdentity actor, P2B5SiteScopeDecision scope)
    {
        var target = Target(profileId, actor, scope);
        var device = _catalog.GetTaskScopeTarget(profileId, scope)!;
        CompleteConfigurationDrift Result(string state, string reason) => new(profileId, device.Id, device.SiteId, state, reason);
        lock (_storageGate)
        {
            var directory = DirectoryFor(profileId, false);
            if (!Directory.Exists(directory)) return Result("NO_BASELINE", "NO_COMPLETE_BACKUPS");
            var manifests = Directory.EnumerateFiles(directory, "*.json")
                .Select(path => ReadManifest(Path.GetFileNameWithoutExtension(path), profileId)).ToArray();
            if (manifests.Any(item => item is null)) return Result("UNKNOWN", "MANIFEST_UNAVAILABLE");
            var ordered = manifests.Cast<CompleteConfigurationBackup>().OrderByDescending(item => item.Version)
                .ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
            if (ordered.Length == 0) return Result("NO_BASELINE", "NO_COMPLETE_BACKUPS");
            if (ordered.Select(item => item.Version).Distinct().Count() != ordered.Length)
                return Result("UNKNOWN", "VERSION_AMBIGUOUS");
            var after = ordered[0];
            var before = ordered.ElementAtOrDefault(1);
            if (!SameIdentity(after, target, device) || before is not null && !SameIdentity(before, target, device))
                return Result("UNKNOWN", "DEVICE_IDENTITY_CHANGED");
            if (Read(after.Id, profileId) is null || before is not null && Read(before.Id, profileId) is null)
                return Result("UNKNOWN", "BACKUP_INTEGRITY_FAILED");
            if (after.CapturedMs <= 0 || before is not null && (before.CapturedMs <= 0 || after.CapturedMs < before.CapturedMs))
                return Result("UNKNOWN", "CAPTURE_TIME_INVALID");
            if (before is null)
                return Result("BASELINE_ONLY", "SECOND_CAPTURE_REQUIRED") with
                { AfterBackupId = after.Id, AfterVersion = after.Version, AfterCapturedMs = after.CapturedMs };
            if (after.Version != before.Version + 1) return Result("UNKNOWN", "VERSION_GAP");
            try
            {
                var left = Encoding.UTF8.GetString(VerifiedBytes(before, false));
                var right = Encoding.UTF8.GetString(VerifiedBytes(after, false));
                var changed = Normalize(left) != Normalize(right);
                var metadataChanged = before.Model != after.Model || before.SoftwareVersion != after.SoftwareVersion;
                var sensitiveChanged = CompleteConfigurationDiffer.SecretsChanged(left, right);
                var categories = changed ? CompleteConfigurationDiffer.Compare(left, right)
                    .SelectMany(item => item.Categories).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() : [];
                return new(profileId, device.Id, device.SiteId, changed || metadataChanged ? "CHANGED" : "UNCHANGED",
                    "LATEST_TWO_VERIFIED_CAPTURES", before.Id, after.Id, before.Version, after.Version,
                    before.CapturedMs, after.CapturedMs, changed, sensitiveChanged, metadataChanged, categories);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ConfigRestoreException)
            { return Result("UNKNOWN", "BACKUP_INTEGRITY_FAILED"); }
        }
    }
}
