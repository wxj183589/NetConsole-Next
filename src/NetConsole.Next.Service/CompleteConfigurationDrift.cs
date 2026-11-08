using System.Text;

namespace NetConsole.Next.Service;

internal sealed record CompleteConfigurationDrift(string ProfileId, string DeviceId, string SiteId,
    string State, string Reason, string? BeforeBackupId = null, string? AfterBackupId = null,
    int? BeforeVersion = null, int? AfterVersion = null, long? BeforeCapturedMs = null, long? AfterCapturedMs = null,
    bool? ConfigurationChanged = null, bool? SensitiveValuesChanged = null, bool? ModelOrSoftwareChanged = null,
    IReadOnlyList<string>? Categories = null);

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
