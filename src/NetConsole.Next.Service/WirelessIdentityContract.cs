namespace NetConsole.Next.Service;

internal sealed record WirelessIdentityReviewView(
    string ResourceId,
    string IdentityStatus,
    string IdentityRevision,
    string ScopeStatus,
    bool CanConfirm,
    bool CanReject,
    string? DecisionBlockReason,
    string? ConflictReason,
    IReadOnlyList<string> EvidenceRefs);

internal static class WirelessIdentityContract
{
    public static WirelessIdentityReviewView Describe(WirelessApResourceView resource)
    {
        var completeEvidence = resource.SerialEvidence is { Length: > 0 } &&
            resource.NormalizedMacSet.Count > 0 &&
            resource.Observations.Count > 0 &&
            resource.Observations.All(item => item.SerialEvidence is { Length: > 0 } && item.NormalizedMacSet.Count > 0);
        var candidate = string.Equals(resource.IdentityStatus, "CANDIDATE", StringComparison.Ordinal);
        var canConfirm = candidate && completeEvidence && resource.ConflictReason is null;
        var canReject = candidate || string.Equals(resource.IdentityStatus, "CONFLICT", StringComparison.Ordinal) ||
            string.Equals(resource.IdentityStatus, "UNKNOWN", StringComparison.Ordinal);
        var blockReason = canConfirm ? null : resource.IdentityStatus switch
        {
            "CONFLICT" => resource.ConflictReason ?? "IDENTITY_CONFLICT",
            "CANDIDATE" when !completeEvidence => "INCOMPLETE_EXACT_EVIDENCE",
            "CONFIRMED" => "IDENTITY_ALREADY_CONFIRMED",
            "REPLACED" => "IDENTITY_ALREADY_REPLACED",
            _ => "IDENTITY_REVIEW_NOT_AVAILABLE"
        };
        return new(resource.Id, resource.IdentityStatus, ComputeRevision(resource), resource.ScopeStatus,
            canConfirm, canReject, blockReason, resource.ConflictReason,
            resource.Observations.Select(item => item.RawManifestRef)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    // This revision deliberately excludes runtime state and display text. A
    // state refresh must not invalidate an identity review, while a new source
    // observation or changed exact serial/MAC evidence must.
    public static string ComputeRevision(WirelessApResourceView resource) =>
        WirelessProjectionEngine.Fingerprint(new[] {
            resource.Id,
            resource.IdentityStatus,
            resource.ConflictReason ?? string.Empty,
            resource.SerialEvidence ?? string.Empty,
            string.Join(',', resource.NormalizedMacSet.Order(StringComparer.Ordinal)),
            string.Join(',', resource.SourceObservationIds.Order(StringComparer.Ordinal))
        }.Concat(resource.Observations
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => string.Join('|', item.Id, item.SerialEvidence ?? string.Empty,
                string.Join(',', item.NormalizedMacSet.Order(StringComparer.Ordinal)), item.SourceRevision))));
}
