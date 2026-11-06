using System.Text.Json;

namespace NetConsole.Next.Service;

internal static class WirelessIdentityLifecycleStates
{
    public const string PendingReview = "PENDING_REVIEW";
    public const string Accepted = "ACCEPTED";
    public const string Rejected = "REJECTED";
    public const string Conflict = "CONFLICT";
    public const string Superseded = "SUPERSEDED";

    public static bool IsKnown(string value) => value is PendingReview or Accepted or Rejected or Conflict or Superseded;
}

internal sealed record WirelessIdentityLifecycleRequest(
    string ResourceId,
    string ExpectedRevision,
    string Decision,
    string Reason,
    string? ReplacementResourceId = null,
    IReadOnlyList<string>? EvidenceRefs = null);

internal sealed record WirelessIdentityLifecycleEvent(
    string FromState,
    string ToState,
    string SourceRevision,
    string Revision,
    string? ReplacementResourceId,
    IReadOnlyList<string> EvidenceRefs,
    string Reason,
    string Actor,
    long DecidedMs);

internal sealed record WirelessIdentityLifecycleView(
    string ResourceId,
    string State,
    string Revision,
    string? ReplacementResourceId,
    IReadOnlyList<string> EvidenceRefs,
    string? Reason,
    string? Actor,
    long? DecidedMs,
    string? SourceRevision = null,
    IReadOnlyList<WirelessIdentityLifecycleEvent>? History = null);

internal sealed record WirelessIdentityLifecycleStore(
    Dictionary<string, WirelessIdentityLifecycleView> Current,
    Dictionary<string, List<WirelessIdentityLifecycleEvent>> History);

internal sealed class WirelessIdentityLifecycleException(string code, string detail, int statusCode = 409) : Exception(detail)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

internal sealed class WirelessIdentityLifecycleService(RuntimeSettings settings, Catalog catalog)
{
    private const int MaximumHistoryPerResource = 64;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private readonly object _gate = new();
    private readonly string _path = Path.Combine(settings.DataRoot, "wireless", "identity-lifecycle.json");

    public bool IsAuthorityAvailable() =>
        P2B4WriteFeatureGate.EnvironmentEnabled() &&
        IsTemporaryRoot(settings.DataRoot) &&
        !IsReservedRoot(settings.DataRoot) &&
        !IsReparse(settings.DataRoot);

    public WirelessIdentityLifecycleView Get(string resourceId, WirelessIdentityReviewView review,
        P2B5SiteScopeDecision scope)
    {
        RequireVisible(resourceId, review, scope);
        lock (_gate)
        {
            var store = ReadStore();
            if (store.Current.TryGetValue(resourceId, out var stored))
                return stored with { History = store.History.TryGetValue(resourceId, out var history) ? history : Array.Empty<WirelessIdentityLifecycleEvent>() };
            return new(resourceId, InitialState(review), review.IdentityRevision, null,
                review.EvidenceRefs, null, null, null, review.IdentityRevision, Array.Empty<WirelessIdentityLifecycleEvent>());
        }
    }

    public WirelessIdentityLifecycleView Decide(WebUserIdentity actor, WirelessIdentityLifecycleRequest request,
        WirelessIdentityReviewView review, IReadOnlyList<WirelessApResourceView> resources,
        P2B5SiteScopeDecision scope)
    {
        if (!IsAuthorityAvailable())
            throw new WirelessIdentityLifecycleException("WIRELESS_IDENTITY_WRITE_UNAVAILABLE",
                "Identity lifecycle writes require the isolated write gate and temporary data root.", 503);
        if (!string.Equals(actor.Role, "SUPERADMIN", StringComparison.OrdinalIgnoreCase))
            throw new WirelessIdentityLifecycleException("WIRELESS_IDENTITY_WRITE_DENIED",
                "Identity lifecycle writes require SUPERADMIN.", 403);
        RequireVisible(request.ResourceId, review, scope);
        var expected = Required(request.ExpectedRevision, "expectedRevision");
        var reason = Required(request.Reason, "reason");
        if (reason.Length > 512) throw new WirelessIdentityLifecycleException("IDENTITY_REASON_TOO_LONG", "Reason exceeds 512 characters.", 400);
        var decision = Required(request.Decision, "decision").ToUpperInvariant();
        if (decision is not ("ACCEPT" or "REJECT" or "CONFLICT" or "SUPERSEDE"))
            throw new WirelessIdentityLifecycleException("IDENTITY_DECISION_INVALID", "Decision must be ACCEPT, REJECT, CONFLICT or SUPERSEDE.", 400);

        var evidence = (request.EvidenceRefs ?? Array.Empty<string>()).Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (evidence.Length == 0 || evidence.Any(item => item.Length > 256))
            throw new WirelessIdentityLifecycleException("IDENTITY_EVIDENCE_REQUIRED", "At least one bounded evidence reference is required.", 400);
        if (evidence.Any(item => !review.EvidenceRefs.Contains(item, StringComparer.Ordinal)))
            throw new WirelessIdentityLifecycleException("IDENTITY_EVIDENCE_NOT_VISIBLE", "Evidence must belong to the current identity review.", 403);

        lock (_gate)
        {
            var store = ReadStore();
            var current = store.Current.TryGetValue(request.ResourceId, out var saved)
                ? saved
                : new(request.ResourceId, InitialState(review), review.IdentityRevision, null, review.EvidenceRefs, null, null, null,
                    review.IdentityRevision, Array.Empty<WirelessIdentityLifecycleEvent>());
            if (!string.Equals(expected, current.Revision, StringComparison.Ordinal))
                throw new WirelessIdentityLifecycleException("IDENTITY_STALE_REVISION", "Identity evidence changed after the review.");
            if (!string.Equals(expected, review.IdentityRevision, StringComparison.Ordinal))
                throw new WirelessIdentityLifecycleException("IDENTITY_STALE_REVIEW", "The review is stale and must be refreshed.");
            if (decision == "ACCEPT") EnsureUniqueIdentity(request.ResourceId, resources);
            if (decision == "SUPERSEDE") EnsureReplacementVisible(request.ResourceId, request.ReplacementResourceId, resources);
            var next = NextState(current.State, decision, review, request.ReplacementResourceId);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var nextRevision = WirelessProjectionEngine.Fingerprint(new[] {
                current.Revision, current.State, next.State, review.IdentityRevision,
                actor.UserId, now.ToString(System.Globalization.CultureInfo.InvariantCulture), reason,
                request.ReplacementResourceId ?? string.Empty, string.Join(',', evidence)
            });
            var result = next with { Revision = nextRevision, SourceRevision = review.IdentityRevision,
                ReplacementResourceId = request.ReplacementResourceId, EvidenceRefs = evidence, Reason = reason,
                Actor = actor.UserId, DecidedMs = now, History = Array.Empty<WirelessIdentityLifecycleEvent>() };
            var history = store.History.TryGetValue(request.ResourceId, out var events)
                ? events
                : new List<WirelessIdentityLifecycleEvent>();
            history.Add(new(current.State, result.State, review.IdentityRevision, nextRevision,
                request.ReplacementResourceId, evidence, reason, actor.UserId, now));
            if (history.Count > MaximumHistoryPerResource)
                history.RemoveRange(0, history.Count - MaximumHistoryPerResource);
            result = result with { History = history.ToArray() };
            store.Current[request.ResourceId] = result;
            store.History[request.ResourceId] = history;
            WriteStore(store);
            catalog.TryRecordDeviceAuditEvent(actor.UserId, "WIRELESS_IDENTITY_DECISION", result.State,
                request.ResourceId, result.Revision,
                new[] { "decision=" + decision, "evidenceCount=" + evidence.Length,
                    "replacement=" + (request.ReplacementResourceId ?? ""), "scope=" + review.ScopeStatus }, reason);
            return result;
        }
    }

    private static string InitialState(WirelessIdentityReviewView review) =>
        review.ConflictReason is not null ? WirelessIdentityLifecycleStates.Conflict : WirelessIdentityLifecycleStates.PendingReview;

    private static WirelessIdentityLifecycleView NextState(string current, string decision,
        WirelessIdentityReviewView review, string? replacement)
    {
        if (!WirelessIdentityLifecycleStates.IsKnown(current))
            throw new WirelessIdentityLifecycleException("IDENTITY_STATE_INVALID", "Stored identity state is invalid.", 500);
        if (decision == "SUPERSEDE")
        {
            if (string.IsNullOrWhiteSpace(replacement) || string.Equals(replacement, review.ResourceId, StringComparison.Ordinal))
                throw new WirelessIdentityLifecycleException("IDENTITY_REPLACEMENT_REQUIRED", "SUPERSEDE requires a different replacement resource.", 400);
            if (current != WirelessIdentityLifecycleStates.Accepted)
                throw new WirelessIdentityLifecycleException("IDENTITY_SUPERSEDE_REQUIRES_ACCEPTED", "Only an accepted identity can be superseded.");
            return new(review.ResourceId, WirelessIdentityLifecycleStates.Superseded, review.IdentityRevision, replacement, Array.Empty<string>(), null, null, null);
        }
        if (current is WirelessIdentityLifecycleStates.Accepted or WirelessIdentityLifecycleStates.Rejected or WirelessIdentityLifecycleStates.Superseded)
            throw new WirelessIdentityLifecycleException("IDENTITY_STATE_TERMINAL", "A terminal identity decision cannot be changed.");
        if (decision == "ACCEPT")
        {
            if (!review.CanConfirm) throw new WirelessIdentityLifecycleException("IDENTITY_CONFIRM_BLOCKED", review.DecisionBlockReason ?? "Identity evidence is not confirmable.");
            return new(review.ResourceId, WirelessIdentityLifecycleStates.Accepted, review.IdentityRevision, replacement, Array.Empty<string>(), null, null, null);
        }
        if (decision == "CONFLICT") return new(review.ResourceId, WirelessIdentityLifecycleStates.Conflict, review.IdentityRevision, null, Array.Empty<string>(), null, null, null);
        if (!review.CanReject) throw new WirelessIdentityLifecycleException("IDENTITY_REJECT_BLOCKED", "Identity evidence cannot be rejected from its current state.");
        return new(review.ResourceId, WirelessIdentityLifecycleStates.Rejected, review.IdentityRevision, null, Array.Empty<string>(), null, null, null);
    }

    private static void RequireVisible(string resourceId, WirelessIdentityReviewView review, P2B5SiteScopeDecision scope)
    {
        if (!scope.IsAuthorized || !string.Equals(resourceId, review.ResourceId, StringComparison.Ordinal) ||
            !string.Equals(review.ScopeStatus, "FULL", StringComparison.OrdinalIgnoreCase))
            throw new WirelessIdentityLifecycleException("WIRELESS_IDENTITY_SCOPE_DENIED", "Identity is outside the current Site scope.", 403);
    }

    private static void EnsureUniqueIdentity(string resourceId, IReadOnlyList<WirelessApResourceView> resources)
    {
        var resource = resources.FirstOrDefault(item => string.Equals(item.Id, resourceId, StringComparison.Ordinal));
        if (resource is null) throw new WirelessIdentityLifecycleException("WIRELESS_IDENTITY_SCOPE_DENIED", "Identity is outside the current Site scope.", 403);
        var duplicateSerial = !string.IsNullOrWhiteSpace(resource.SerialEvidence) && resources.Any(item =>
            !string.Equals(item.Id, resourceId, StringComparison.Ordinal) &&
            string.Equals(item.SerialEvidence, resource.SerialEvidence, StringComparison.Ordinal));
        var duplicateMac = resource.NormalizedMacSet.Any(mac => resources.Any(item =>
            !string.Equals(item.Id, resourceId, StringComparison.Ordinal) && item.NormalizedMacSet.Contains(mac, StringComparer.Ordinal)));
        if (duplicateSerial || duplicateMac)
            throw new WirelessIdentityLifecycleException("IDENTITY_SERIAL_MAC_CONFLICT", "Serial and MAC evidence must be unique before acceptance.");
    }

    private static void EnsureReplacementVisible(string resourceId, string? replacementResourceId,
        IReadOnlyList<WirelessApResourceView> resources)
    {
        if (string.IsNullOrWhiteSpace(replacementResourceId) ||
            string.Equals(resourceId, replacementResourceId, StringComparison.Ordinal) ||
            !resources.Any(item => string.Equals(item.Id, replacementResourceId, StringComparison.Ordinal)))
            throw new WirelessIdentityLifecycleException("IDENTITY_REPLACEMENT_NOT_VISIBLE",
                "SUPERSEDE requires a different replacement identity visible in the current Site scope.", 403);
    }

    private WirelessIdentityLifecycleStore ReadStore()
    {
        if (!File.Exists(_path)) return new(new(StringComparer.Ordinal), new(StringComparer.Ordinal));
        try
        {
            using var stream = File.OpenRead(_path);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.TryGetProperty("current", out _))
                return JsonSerializer.Deserialize<WirelessIdentityLifecycleStore>(document.RootElement.GetRawText(), JsonOptions)
                    ?? new(new(StringComparer.Ordinal), new(StringComparer.Ordinal));
            // Accept the short-lived pre-history format so a local candidate
            // can be upgraded without silently dropping an existing decision.
            var current = JsonSerializer.Deserialize<Dictionary<string, WirelessIdentityLifecycleView>>(document.RootElement.GetRawText(), JsonOptions)
                ?? new(StringComparer.Ordinal);
            return new(current, new(StringComparer.Ordinal));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        { throw new WirelessIdentityLifecycleException("WIRELESS_IDENTITY_STORE_UNAVAILABLE", "Identity lifecycle store is unavailable.", 503); }
    }

    private void WriteStore(WirelessIdentityLifecycleStore store)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32 * 1024, FileOptions.WriteThrough))
                JsonSerializer.Serialize(stream, store, JsonOptions);
            if (File.Exists(_path)) File.Replace(temp, _path, null, true);
            else File.Move(temp, _path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw new WirelessIdentityLifecycleException("WIRELESS_IDENTITY_STORE_UNAVAILABLE", "Identity lifecycle store cannot be written.", 503);
        }
    }

    private static string Required(string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new WirelessIdentityLifecycleException("IDENTITY_FIELD_REQUIRED", name + " is required.", 400) : value.Trim();

    private static bool IsReservedRoot(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(full, Path.GetFullPath(@"D:\NetConsoleData").TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
               string.Equals(full, Path.GetFullPath(@"D:\NetConsoleData-dev").TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTemporaryRoot(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(full, temp, StringComparison.OrdinalIgnoreCase) || full.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReparse(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}
