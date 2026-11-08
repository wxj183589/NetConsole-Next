using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NetConsole.Next.Service;

internal sealed record H3cLldpRelation(
    string ProfileId,
    string LocalDeviceId,
    string LocalInterface,
    string NeighborDevice,
    string? NeighborPort,
    string? NeighborManagementAddress,
    string? NeighborChassisId,
    string SiteId,
    string Source,
    long CapturedMs,
    string FactRevision,
    string RelationKey,
    string ChangeKind)
{
    public string SourceAuthority { get; init; } = "";
    public string CapturedScopeKey { get; init; } = "UNSCOPED";
}

internal sealed record H3cLldpClaimsView(
    string ProfileId,
    string ScopeState,
    bool IsGlobal,
    string? ClaimsRevision,
    IReadOnlyList<string> AllowedSiteReferences,
    int FactCount,
    int RelationCount,
    IReadOnlyList<H3cLldpRelation> Relations);

/// <summary>
/// Projects verified LLDP facts into a read-only relationship stream. The stream
/// is deliberately additive: an absent observation is never treated as a delete.
/// </summary>
internal static class H3cLldpTopologyProjection
{
    public static IReadOnlyList<H3cLldpRelation> Project(string profileId,
        IEnumerable<H3cFactSnapshot> facts)
    {
        var previous = new Dictionary<string, string>(StringComparer.Ordinal);
        var result = new List<H3cLldpRelation>();
        foreach (var fact in facts
                     .Where(item => string.Equals(item.ProfileId, profileId, StringComparison.Ordinal) &&
                                    string.Equals(item.Kind, "lldp.neighbor", StringComparison.Ordinal))
                     .OrderBy(item => item.CapturedMs)
                     .ThenBy(item => item.Id, StringComparer.Ordinal))
        {
            if (!TryRead(fact, out var observation)) continue;
            var fingerprint = Fingerprint(observation);
            var changeKind = previous.TryGetValue(observation.RelationKey, out var prior)
                ? string.Equals(prior, fingerprint, StringComparison.Ordinal) ? "REPEAT" : "CHANGED"
                : "FIRST_SEEN";
            previous[observation.RelationKey] = fingerprint;
            result.Add(new(profileId, observation.LocalDeviceId, observation.LocalInterface,
                observation.NeighborDevice, observation.NeighborPort, observation.NeighborManagementAddress,
                observation.NeighborChassisId, observation.SiteId, fact.Source, fact.CapturedMs, fact.Id,
                observation.RelationKey, changeKind)
                { SourceAuthority = fact.SourceAuthority, CapturedScopeKey = fact.CapturedScopeKey });
        }
        return result;
    }

    private static bool TryRead(H3cFactSnapshot fact, out Observation observation)
    {
        observation = default;
        try
        {
            using var document = JsonDocument.Parse(fact.PayloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("capturedScope", out var scope) ||
                scope.ValueKind != JsonValueKind.Object ||
                !ReadString(scope, "profileId", out var scopedProfile) ||
                !string.Equals(scopedProfile, fact.ProfileId, StringComparison.Ordinal) ||
                !ReadString(scope, "deviceId", out var localDeviceId) ||
                !ReadString(scope, "siteId", out var siteId) ||
                !ReadString(document.RootElement, "localInterface", out var localInterface) ||
                !Regex.IsMatch(localInterface, @"^[A-Za-z][A-Za-z0-9/._:-]{1,63}$", RegexOptions.CultureInvariant) ||
                !localInterface.Any(char.IsDigit))
                return false;

            var neighborPort = ReadOptional(document.RootElement, "neighborInterface");
            var neighborName = ReadOptional(document.RootElement, "neighborSysname");
            var chassis = ReadOptional(document.RootElement, "chassisId");
            var management = ReadOptional(document.RootElement, "managementAddress");
            // Older parsers persisted legend rows and sometimes placed a system
            // name in the management-address column. Keep the original fact/Raw,
            // but never publish those values as an observed link or IP address.
            if (!IPAddress.TryParse(management, out _)) management = null;
            var neighborIdentity = FirstNonEmpty(chassis, neighborName, management);
            if (neighborIdentity is null) return false;
            var neighborDevice = FirstNonEmpty(neighborName, chassis, management) ?? neighborIdentity;
            // Scope the relation to the observed local device and Site. Port
            // names are only unique within one device; omitting this context
            // would merge identical ports from separate devices into one
            // history stream.
            var relationKey = $"{fact.SourceAuthority}|{fact.CapturedScopeKey}|{siteId}|{NormalizeIdentity(localDeviceId)}|{Normalize(localInterface)}|{NormalizeIdentity(neighborIdentity)}";
            observation = new(localDeviceId, siteId, localInterface, neighborDevice, neighborPort,
                management, chassis, relationKey);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static string Fingerprint(Observation value) => string.Join('|',
        Normalize(value.LocalInterface), NormalizeIdentity(value.NeighborDevice),
        Normalize(value.NeighborPort), Normalize(value.NeighborManagementAddress),
        NormalizeIdentity(value.NeighborChassisId), Normalize(value.SiteId));

    private static bool ReadString(JsonElement parent, string name, out string value)
    {
        value = string.Empty;
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
            return false;
        value = element.GetString()?.Trim() ?? string.Empty;
        return value.Length > 0;
    }

    private static string? ReadOptional(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()?.Trim() is { Length: > 0 } value && value is not ("-" or "--") ? value : null
            : null;

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string Normalize(string? value) =>
        string.Join(' ', (value ?? string.Empty).Trim().Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private static string NormalizeIdentity(string? value)
    {
        var normalized = Normalize(value);
        var compact = normalized.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(":", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal);
        return compact.Length == 12 && compact.All(Uri.IsHexDigit) ? compact : normalized;
    }

    private readonly record struct Observation(string LocalDeviceId, string SiteId,
        string LocalInterface, string NeighborDevice, string? NeighborPort,
        string? NeighborManagementAddress, string? NeighborChassisId, string RelationKey);
}

internal sealed partial class Catalog
{
    public IReadOnlyList<H3cLldpRelation> QueryH3cLldpRelations(string profileId,
        bool history, int limit, P2B5SiteScopeDecision? scope)
    {
        var facts = QueryH3cFacts(profileId, "lldp.neighbor", history, Math.Clamp(limit, 1, 1000), scope,
            requireCapturedScope: true);
        return H3cLldpTopologyProjection.Project(profileId, facts);
    }
}
