using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;

namespace NetConsole.Next.Service;

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum P2BSourceKind { PLANNED, OBSERVED, IMPORTED, MANUAL }

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum P2BAddressRole { MANAGEMENT, SERVICE, OUT_OF_BAND, LOOPBACK, OTHER }

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum P2BLifecycleState { ACTIVE, ARCHIVED }

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum P2BAddressFamily { IPV4, IPV6 }

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum P2BNetworkDomainKind { MANAGEMENT, BUSINESS, OUT_OF_BAND, TRAIN_GROUND, SHARED_SERVICE, OTHER }

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum P2BScopeResolution { RESOLVED, UNRESOLVED, CONFLICT }

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum P2BReviewDecision { ACCEPT_PLANNED, ACCEPT_OBSERVED, REJECT, DEFER }

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum P2BConflictKind
{
    NONE,
    SOURCE_FACT_DUPLICATE,
    SOURCE_FACT_COEXISTENCE,
    SOURCE_CONFLICT,
    ADDRESS_ALREADY_CLAIMED,
    NETWORK_DOMAIN_NOT_RESOLVED,
    CROSS_DOMAIN_REUSE_ALLOWED,
    HISTORICAL_REUSE_ALLOWED,
    VLAN_VALIDITY_OVERLAP,
    VLAN_PLANNED_OBSERVED_COEXISTENCE,
    VLAN_DOMAIN_MISMATCH,
    NETWORK_SCOPE_CONFLICT,
    REVIEW_REQUIRED
}

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum P2BEffectiveState { EMPTY, CONSISTENT, CONFLICT, MANUALLY_SELECTED }

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum P2BVlanTaggingMode { ACCESS, TRUNK, HYBRID, QINQ }

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum P2BLegacyAddressState
{
    LEGACY_ADDRESS_PENDING_DOMAIN,
    LEGACY_ADDRESS_RESOLVED,
    LEGACY_ADDRESS_CONFLICT
}

internal sealed record P2BSourceProvenance(
    P2BSourceKind SourceKind,
    string SourceReference,
    long Revision,
    long? ObservedAtMs = null)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SourceReference) || SourceReference.Length > 256 || SourceReference.Any(char.IsControl))
            throw new ArgumentException("SourceReference must be a bounded non-control identifier.", nameof(SourceReference));
        if (Revision <= 0) throw new ArgumentOutOfRangeException(nameof(Revision));
        if (ObservedAtMs is < 0) throw new ArgumentOutOfRangeException(nameof(ObservedAtMs));
    }
}

internal sealed record P2BValidityInterval(long? ValidFromMs, long? ValidToMs)
{
    public void Validate()
    {
        if (ValidFromMs is < 0) throw new ArgumentOutOfRangeException(nameof(ValidFromMs));
        if (ValidToMs is < 0) throw new ArgumentOutOfRangeException(nameof(ValidToMs));
        if (ValidFromMs is not null && ValidToMs is not null && ValidFromMs >= ValidToMs)
            throw new ArgumentException("Validity uses a half-open interval [from,to) and requires from < to.");
    }

    public bool Overlaps(P2BValidityInterval other)
    {
        Validate();
        other.Validate();
        var startsBeforeEnd = ValidToMs is null || other.ValidFromMs is null || other.ValidFromMs < ValidToMs;
        var otherStartsBeforeEnd = other.ValidToMs is null || ValidFromMs is null || ValidFromMs < other.ValidToMs;
        return startsBeforeEnd && otherStartsBeforeEnd;
    }
}

internal sealed record P2BNormalizedAddress(
    P2BAddressFamily AddressFamily,
    string NormalizedAddress,
    int PrefixLength)
{
    public string ClaimKey => $"{AddressFamily}:{NormalizedAddress}";
}

internal static class P2BAddressNormalization
{
    public static P2BNormalizedAddress Normalize(string address, int prefixLength)
    {
        if (string.IsNullOrWhiteSpace(address)) throw new ArgumentException("Address is required.", nameof(address));
        if (!IPAddress.TryParse(address.Trim(), out var parsed))
            throw new FormatException("Address must be a valid IPv4 or IPv6 address.");

        // IPv4-mapped IPv6 is normalized to the IPv4 claim namespace so a textual
        // representation cannot bypass the same-address conflict rule.
        if (parsed.AddressFamily == AddressFamily.InterNetworkV6 && parsed.IsIPv4MappedToIPv6)
            parsed = parsed.MapToIPv4();

        var family = parsed.AddressFamily == AddressFamily.InterNetwork ? P2BAddressFamily.IPV4 :
            parsed.AddressFamily == AddressFamily.InterNetworkV6 ? P2BAddressFamily.IPV6 :
            throw new FormatException("Only IPv4 and IPv6 addresses are supported.");
        var maximumPrefix = family == P2BAddressFamily.IPV4 ? 32 : 128;
        if (prefixLength < 0 || prefixLength > maximumPrefix)
            throw new ArgumentOutOfRangeException(nameof(prefixLength), $"Prefix length must be 0..{maximumPrefix} for {family}.");
        return new P2BNormalizedAddress(family, parsed.ToString().ToLowerInvariant(), prefixLength);
    }

    public static bool TryNormalize(string address, int prefixLength, out P2BNormalizedAddress? result)
    {
        try { result = Normalize(address, prefixLength); return true; }
        catch (ArgumentException) { result = null; return false; }
    }
}

internal sealed record P2BDeviceAddressFact(
    string DeviceId,
    string? NetworkDomainId,
    P2BNormalizedAddress Address,
    P2BAddressRole AddressRole,
    P2BSourceProvenance Source,
    P2BValidityInterval Validity,
    P2BLifecycleState Lifecycle = P2BLifecycleState.ACTIVE,
    string? ImportFingerprint = null)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DeviceId)) throw new ArgumentException("DeviceId is required.", nameof(DeviceId));
        if (NetworkDomainId is not null && string.IsNullOrWhiteSpace(NetworkDomainId))
            throw new ArgumentException("NetworkDomainId cannot be blank.", nameof(NetworkDomainId));
        Source.Validate();
        Validity.Validate();
        if (ImportFingerprint is { Length: > 256 }) throw new ArgumentException("Import fingerprint is too long.", nameof(ImportFingerprint));
    }

    public P2BAddressFactIdentity Identity => new(DeviceId, NetworkDomainId, Address, AddressRole, Source);
}

internal sealed record P2BAddressFactIdentity(
    string DeviceId,
    string? NetworkDomainId,
    P2BNormalizedAddress Address,
    P2BAddressRole AddressRole,
    P2BSourceProvenance Source);

internal sealed record P2BAddressClaim(
    string DeviceId,
    string? NetworkDomainId,
    P2BNormalizedAddress Address,
    P2BValidityInterval Validity,
    P2BLifecycleState Lifecycle = P2BLifecycleState.ACTIVE);

internal sealed record P2BAddressComparison(P2BConflictKind Kind, string Detail)
{
    public bool IsConflict => Kind is P2BConflictKind.ADDRESS_ALREADY_CLAIMED or P2BConflictKind.SOURCE_CONFLICT or
        P2BConflictKind.NETWORK_DOMAIN_NOT_RESOLVED;
}

internal static class P2BAddressConflictRules
{
    public static P2BAddressComparison Compare(P2BDeviceAddressFact left, P2BDeviceAddressFact right)
    {
        left.Validate();
        right.Validate();
        if (left.Identity == right.Identity && left.Validity == right.Validity &&
            string.Equals(left.ImportFingerprint, right.ImportFingerprint, StringComparison.Ordinal))
            return new(P2BConflictKind.SOURCE_FACT_DUPLICATE, "The same source fact/import revision is idempotently repeated.");

        var sameSourceRevision = left.DeviceId == right.DeviceId &&
            left.Source.SourceKind == right.Source.SourceKind &&
            string.Equals(left.Source.SourceReference, right.Source.SourceReference, StringComparison.Ordinal) &&
            left.Source.Revision == right.Source.Revision;
        if (sameSourceRevision)
            return new(P2BConflictKind.SOURCE_CONFLICT, "One source revision supplied different address facts.");

        var sameAddress = left.Address.AddressFamily == right.Address.AddressFamily &&
            string.Equals(left.Address.NormalizedAddress, right.Address.NormalizedAddress, StringComparison.Ordinal);
        if (!sameAddress) return new(P2BConflictKind.NONE, "Different normalized addresses do not conflict.");

        if (left.DeviceId == right.DeviceId)
        {
            return new(P2BConflictKind.SOURCE_FACT_COEXISTENCE, "Multiple source facts for one device are retained.");
        }

        if (left.NetworkDomainId is null || right.NetworkDomainId is null)
            return new(P2BConflictKind.NETWORK_DOMAIN_NOT_RESOLVED, "An address claim cannot be compared without an explicit Network Domain.");
        if (!string.Equals(left.NetworkDomainId, right.NetworkDomainId, StringComparison.Ordinal))
            return new(P2BConflictKind.CROSS_DOMAIN_REUSE_ALLOWED, "Different explicit Network Domains are separate address claim scopes.");
        if (!left.Validity.Overlaps(right.Validity) || left.Lifecycle == P2BLifecycleState.ARCHIVED || right.Lifecycle == P2BLifecycleState.ARCHIVED)
            return new(P2BConflictKind.HISTORICAL_REUSE_ALLOWED, "Address reuse is allowed when active validity intervals do not overlap.");
        return new(P2BConflictKind.ADDRESS_ALREADY_CLAIMED, "Different devices claim the same address in one active Network Domain.");
    }
}

internal sealed record P2BNetworkDomainScope(
    IReadOnlyList<string> SiteReferences,
    IReadOnlyList<string> StationReferences,
    IReadOnlyList<string> LineReferences,
    IReadOnlyList<string> SectionReferences,
    P2BScopeResolution Resolution)
{
    public bool IsCrossSite => SiteReferences.Distinct(StringComparer.Ordinal).Count() > 1;

    public void Validate()
    {
        ValidateReferences(SiteReferences, nameof(SiteReferences));
        ValidateReferences(StationReferences, nameof(StationReferences));
        ValidateReferences(LineReferences, nameof(LineReferences));
        ValidateReferences(SectionReferences, nameof(SectionReferences));
        if (Resolution == P2BScopeResolution.RESOLVED && SiteReferences.Count == 0)
            throw new ArgumentException("A resolved Network Domain needs explicit Site references.");
    }

    public bool IsVisibleTo(IReadOnlySet<string> allowedSiteReferences, bool allowUnresolved)
    {
        Validate();
        if (Resolution != P2BScopeResolution.RESOLVED) return allowUnresolved;
        return SiteReferences.All(allowedSiteReferences.Contains);
    }

    private static void ValidateReferences(IReadOnlyList<string> references, string parameterName)
    {
        if (references.Any(reference => string.IsNullOrWhiteSpace(reference) || reference.Any(char.IsControl)))
            throw new ArgumentException("Scope references must be non-empty non-control identifiers.", parameterName);
        if (references.Count != references.Distinct(StringComparer.Ordinal).Count())
            throw new ArgumentException("Scope references must not contain duplicates.", parameterName);
    }
}

internal sealed record P2BNetworkDomainContract(
    string DomainId,
    string DomainName,
    P2BNetworkDomainKind DomainKind,
    P2BNetworkDomainScope Scope,
    string? ParentDomainReference,
    string? VrfName,
    P2BLifecycleState Lifecycle,
    long Revision,
    P2BSourceProvenance Source)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DomainId) || string.IsNullOrWhiteSpace(DomainName))
            throw new ArgumentException("Network Domain identity is required.");
        if (ParentDomainReference == DomainId) throw new ArgumentException("A Network Domain cannot parent itself.");
        Scope.Validate();
        Source.Validate();
        if (Revision <= 0) throw new ArgumentOutOfRangeException(nameof(Revision));
    }
}

internal static class P2BNetworkDomainRules
{
    public static void ValidateNoParentCycle(IReadOnlyDictionary<string, string?> parents)
    {
        foreach (var domain in parents.Keys)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var current = domain;
            while (parents.TryGetValue(current, out var parent) && parent is not null)
            {
                if (!seen.Add(current) || !parents.ContainsKey(parent))
                    throw new ArgumentException("Network Domain parent reference is cyclic or unresolved.");
                current = parent;
            }
        }
    }

    public static bool CanAssociateSite(P2BNetworkDomainContract domain, string siteId) =>
        domain.Scope.Resolution == P2BScopeResolution.RESOLVED && domain.Scope.SiteReferences.Contains(siteId, StringComparer.Ordinal);
}

internal sealed record P2BVlanPlanContract(
    string PlanId,
    string NetworkDomainId,
    int VlanId,
    P2BVlanTaggingMode TaggingMode,
    int? NativeVlanId,
    int? Pvid,
    int? QinQOuterId,
    int? QinQInnerId,
    P2BValidityInterval Validity,
    P2BLifecycleState Lifecycle,
    P2BSourceProvenance Source)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(PlanId) || string.IsNullOrWhiteSpace(NetworkDomainId))
            throw new ArgumentException("VLAN plan and Network Domain identities are required.");
        ValidateVlanId(VlanId, nameof(VlanId));
        ValidateOptional(NativeVlanId, nameof(NativeVlanId));
        ValidateOptional(Pvid, nameof(Pvid));
        ValidateOptional(QinQOuterId, nameof(QinQOuterId));
        ValidateOptional(QinQInnerId, nameof(QinQInnerId));
        Validity.Validate();
        Source.Validate();

        switch (TaggingMode)
        {
            case P2BVlanTaggingMode.ACCESS:
                if (Pvid != VlanId || (NativeVlanId is not null && NativeVlanId != VlanId) || QinQOuterId is not null || QinQInnerId is not null)
                    throw new ArgumentException("ACCESS requires PVID=VLAN ID, optional matching native VLAN, and no QinQ fields.");
                break;
            case P2BVlanTaggingMode.TRUNK or P2BVlanTaggingMode.HYBRID:
                if (NativeVlanId is not null && Pvid is not null && NativeVlanId != Pvid)
                    throw new ArgumentException("Native VLAN and PVID must agree when both are specified.");
                if (QinQOuterId is not null || QinQInnerId is not null)
                    throw new ArgumentException("QinQ fields require QINQ tagging mode.");
                break;
            case P2BVlanTaggingMode.QINQ:
                if (QinQOuterId != VlanId || QinQInnerId is null || NativeVlanId is not null || Pvid is not null)
                    throw new ArgumentException("QINQ requires outer VLAN equal to VLAN ID, an inner VLAN, and no native/PVID fields.");
                break;
        }
    }

    private static void ValidateOptional(int? vlanId, string parameterName)
    {
        if (vlanId is not null) ValidateVlanId(vlanId.Value, parameterName);
    }

    private static void ValidateVlanId(int vlanId, string parameterName)
    {
        if (vlanId is < 1 or > 4094) throw new ArgumentOutOfRangeException(parameterName, "VLAN IDs 0 and 4095 are reserved.");
    }
}

internal sealed record P2BVlanClaim(
    string NetworkDomainId,
    int VlanId,
    P2BValidityInterval Validity,
    P2BLifecycleState Lifecycle,
    P2BSourceKind SourceKind,
    string SourceReference,
    string ValueFingerprint);

internal static class P2BVlanConflictRules
{
    public static P2BConflictKind Compare(P2BVlanClaim left, P2BVlanClaim right)
    {
        if (!string.Equals(left.NetworkDomainId, right.NetworkDomainId, StringComparison.Ordinal) || left.VlanId != right.VlanId)
            return P2BConflictKind.CROSS_DOMAIN_REUSE_ALLOWED;
        if (left.Lifecycle == P2BLifecycleState.ARCHIVED || right.Lifecycle == P2BLifecycleState.ARCHIVED || !left.Validity.Overlaps(right.Validity))
            return P2BConflictKind.HISTORICAL_REUSE_ALLOWED;
        if (left.SourceKind == P2BSourceKind.OBSERVED || right.SourceKind == P2BSourceKind.OBSERVED)
            return P2BConflictKind.VLAN_PLANNED_OBSERVED_COEXISTENCE;
        return P2BConflictKind.VLAN_VALIDITY_OVERLAP;
    }
}

internal sealed record P2BSourceValueFact<T>(T Value, P2BSourceProvenance Source, string ValueFingerprint);

internal sealed record P2BReviewDecisionContract(
    string TargetResource,
    string TargetId,
    P2BReviewDecision Decision,
    IReadOnlyList<P2BSourceRevisionReference> ComparedSources,
    string? SelectedSourceReference,
    long? SelectedRevision,
    string Actor,
    string Reason)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(TargetResource) || string.IsNullOrWhiteSpace(TargetId) || string.IsNullOrWhiteSpace(Actor))
            throw new ArgumentException("Review target and actor are required.");
        if (ComparedSources.Count == 0) throw new ArgumentException("A review must cite compared source revisions.");
        if (string.IsNullOrWhiteSpace(Reason) || Reason.Length > 2_000 || Reason.Any(char.IsControl))
            throw new ArgumentException("Review reason is required and bounded.");
        if (Decision is P2BReviewDecision.ACCEPT_PLANNED or P2BReviewDecision.ACCEPT_OBSERVED &&
            (string.IsNullOrWhiteSpace(SelectedSourceReference) || SelectedRevision is null or <= 0))
            throw new ArgumentException("An accepting review must select an exact source revision.");
    }
}

internal sealed record P2BSourceRevisionReference(string SourceReference, long Revision)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SourceReference) || Revision <= 0) throw new ArgumentException("Source revision reference is invalid.");
    }
}

internal sealed record P2BEffectiveProjection<T>(
    T? Value,
    P2BSourceKind? EffectiveSourceKind,
    string? EffectiveSourceReference,
    P2BEffectiveState State,
    IReadOnlyList<P2BSourceRevisionReference> SupportingSources) where T : class;

internal static class P2BEffectiveProjectionRules
{
    public static P2BEffectiveProjection<T> Evaluate<T>(IReadOnlyList<P2BSourceValueFact<T>> facts, P2BReviewDecisionContract? review = null)
        where T : class
    {
        if (facts.Count == 0) return new(default, null, null, P2BEffectiveState.EMPTY, Array.Empty<P2BSourceRevisionReference>());
        foreach (var fact in facts) fact.Source.Validate();
        var supporting = facts.Select(f => new P2BSourceRevisionReference(f.Source.SourceReference, f.Source.Revision)).ToArray();
        if (review is not null)
        {
            review.Validate();
            var selected = facts.SingleOrDefault(f => string.Equals(f.Source.SourceReference, review.SelectedSourceReference, StringComparison.Ordinal) && f.Source.Revision == review.SelectedRevision);
            var selectedIsCited = selected is not null && review.ComparedSources.Any(source =>
                string.Equals(source.SourceReference, selected.Source.SourceReference, StringComparison.Ordinal) && source.Revision == selected.Source.Revision);
            var selectedKindMatches = selected is not null &&
                ((review.Decision == P2BReviewDecision.ACCEPT_PLANNED && selected.Source.SourceKind == P2BSourceKind.PLANNED) ||
                 (review.Decision == P2BReviewDecision.ACCEPT_OBSERVED && selected.Source.SourceKind == P2BSourceKind.OBSERVED));
            if (selectedIsCited && selectedKindMatches)
                return new(selected!.Value, selected.Source.SourceKind, selected.Source.SourceReference, P2BEffectiveState.MANUALLY_SELECTED, supporting);
            if (review.Decision is P2BReviewDecision.ACCEPT_PLANNED or P2BReviewDecision.ACCEPT_OBSERVED)
                return new(default, null, null, P2BEffectiveState.CONFLICT, supporting);
            if (review.Decision is P2BReviewDecision.REJECT or P2BReviewDecision.DEFER)
                return new(default, null, null, P2BEffectiveState.CONFLICT, supporting);
        }
        if (facts.Select(f => f.ValueFingerprint).Distinct(StringComparer.Ordinal).Count() == 1)
        {
            var selected = facts.Count == 1 ? facts[0] : null;
            return new(selected?.Value, selected?.Source.SourceKind, selected?.Source.SourceReference, P2BEffectiveState.CONSISTENT, supporting);
        }
        return new(default, null, null, P2BEffectiveState.CONFLICT, supporting);
    }
}

internal static class P2BErrorCodes
{
    public const string AddressAlreadyClaimed = "ADDRESS_ALREADY_CLAIMED";
    public const string NetworkDomainNotResolved = "NETWORK_DOMAIN_NOT_RESOLVED";
    public const string NetworkScopeConflict = "NETWORK_SCOPE_CONFLICT";
    public const string VlanValidityOverlap = "VLAN_VALIDITY_OVERLAP";
    public const string VlanDomainMismatch = "VLAN_DOMAIN_MISMATCH";
    public const string StaleRevision = "STALE_REVISION";
    public const string SourceConflict = "SOURCE_CONFLICT";
    public const string ReviewRequired = "REVIEW_REQUIRED";
    public const string InvalidArgument = "INVALID_ARGUMENT";
    public const string AuthenticationRequired = "AUTHENTICATION_REQUIRED";
    public const string Forbidden = "FORBIDDEN";
    public const string NotFound = "NOT_FOUND";
    public const string ResourceLimit = "RESOURCE_LIMIT";

    public static int StatusCode(string code) => code switch
    {
        AuthenticationRequired => 401,
        Forbidden => 403,
        NotFound => 404,
        ResourceLimit => 429,
        AddressAlreadyClaimed or NetworkDomainNotResolved or NetworkScopeConflict or VlanValidityOverlap or
            VlanDomainMismatch or StaleRevision or SourceConflict or ReviewRequired => 409,
        _ => 400
    };

    public static bool IsKnown(string code) => StatusCode(code) != 400 || code == InvalidArgument;
}

internal sealed record P2BConflictDetail(
    string Field,
    string ReasonCode,
    long? CurrentRevision,
    long? SubmittedRevision)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Field) || string.IsNullOrWhiteSpace(ReasonCode) || !P2BErrorCodes.IsKnown(ReasonCode))
            throw new ArgumentException("Conflict detail must use a known redacted reason code.");
        if (CurrentRevision is < 0 || SubmittedRevision is < 0) throw new ArgumentOutOfRangeException(nameof(CurrentRevision));
    }
}

internal sealed record P2BErrorResponse(
    string Code,
    string Resource,
    long? CurrentRevision,
    long? SubmittedRevision,
    IReadOnlyList<P2BConflictDetail> Conflicts,
    bool Retryable)
{
    public int HttpStatus => P2BErrorCodes.StatusCode(Code);

    public void Validate()
    {
        if (!P2BErrorCodes.IsKnown(Code) || string.IsNullOrWhiteSpace(Resource)) throw new ArgumentException("Unknown or missing P2B error code/resource.");
        if (CurrentRevision is < 0 || SubmittedRevision is < 0) throw new ArgumentOutOfRangeException(nameof(CurrentRevision));
        foreach (var conflict in Conflicts) conflict.Validate();
    }
}

internal sealed record P2BDeviceAddressDto(
    string DeviceId,
    string? NetworkDomainId,
    P2BAddressFamily AddressFamily,
    string NormalizedAddress,
    int PrefixLength,
    P2BAddressRole AddressRole,
    P2BSourceKind SourceKind,
    string SourceReference,
    long Revision,
    long? ObservedAtMs,
    P2BValidityInterval Validity,
    P2BLifecycleState Lifecycle,
    P2BConflictKind ConflictState);

internal sealed record P2BFeatureState(bool Enabled, bool AllowsSchemaMutation, bool AllowsApiExposure)
{
    public static P2BFeatureState Disabled => new(false, false, false);
}

internal static class P2BFeatureGate
{
    public const string EnvironmentVariableName = "NETCONSOLE_NEXT_P2B_ENABLED";

    public static P2BFeatureState FromEnvironment(string? value = null)
    {
        var raw = value ?? Environment.GetEnvironmentVariable(EnvironmentVariableName);
        return string.Equals(raw, "1", StringComparison.Ordinal) ? new(true, false, false) : P2BFeatureState.Disabled;
    }
}

internal sealed record P2BLegacyDeviceRecord(
    string DeviceId,
    string ManagementIp,
    int PrefixLength,
    string SiteId,
    string? TerminalProfileId,
    long UpdatedMs);

internal sealed record P2BLegacyAddressProjection(
    string DeviceId,
    string ManagementIp,
    string SiteId,
    string? TerminalProfileId,
    P2BLegacyAddressState State,
    string? NetworkDomainId,
    P2BNormalizedAddress? NormalizedAddress,
    string SourceReference);

internal sealed record P2BLegacyMigrationPlan(IReadOnlyList<P2BLegacyAddressProjection> Records, string MigrationKey)
{
    public bool IsIdempotentWith(P2BLegacyMigrationPlan other) => string.Equals(MigrationKey, other.MigrationKey, StringComparison.Ordinal) &&
        Records.SequenceEqual(other.Records);
}

internal static class P2BLegacyManagementIpRules
{
    public static P2BLegacyMigrationPlan Plan(IReadOnlyList<P2BLegacyDeviceRecord> records)
    {
        var normalized = records.Select(record =>
        {
            var address = P2BAddressNormalization.Normalize(record.ManagementIp, record.PrefixLength);
            return (record, address);
        }).ToArray();
        var duplicateClaims = normalized.GroupBy(item => item.address.ClaimKey, StringComparer.Ordinal)
            .Where(group => group.Select(item => item.record.DeviceId).Distinct(StringComparer.Ordinal).Count() > 1)
            .SelectMany(group => group.Select(item => item.record.DeviceId))
            .ToHashSet(StringComparer.Ordinal);
        var projections = normalized.Select(item => new P2BLegacyAddressProjection(
            item.record.DeviceId,
            item.record.ManagementIp,
            item.record.SiteId,
            item.record.TerminalProfileId,
            duplicateClaims.Contains(item.record.DeviceId) ? P2BLegacyAddressState.LEGACY_ADDRESS_CONFLICT : P2BLegacyAddressState.LEGACY_ADDRESS_PENDING_DOMAIN,
            null,
            item.address,
            SourceReference(item.record.DeviceId))).ToArray();
        var migrationKey = string.Join("|", projections.OrderBy(item => item.DeviceId, StringComparer.Ordinal).Select(item =>
            $"{item.DeviceId}:{item.SourceReference}:{item.NormalizedAddress?.ClaimKey}"));
        return new P2BLegacyMigrationPlan(projections, migrationKey);
    }

    public static P2BLegacyAddressProjection ResolveDomain(P2BLegacyAddressProjection projection, string networkDomainId)
    {
        if (string.IsNullOrWhiteSpace(networkDomainId)) throw new ArgumentException("Network Domain is required.", nameof(networkDomainId));
        return projection with { NetworkDomainId = networkDomainId, State = P2BLegacyAddressState.LEGACY_ADDRESS_RESOLVED };
    }

    public static string SourceReference(string deviceId) => $"legacy-management-ip:{deviceId}";
}
