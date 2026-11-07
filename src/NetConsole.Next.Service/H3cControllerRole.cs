using System.Text.Json;
using System.Text.RegularExpressions;

namespace NetConsole.Next.Service;

internal sealed record H3cControllerRoleFact(string Command, string Status, string? RawRole,
    string NormalizedRole, string? Notice);

/// <summary>Controller-reported role, independent of AP roles and actual failover verification.</summary>
internal static partial class H3cControllerRoleParser
{
    [GeneratedRegex(@"(?im)^\s*Current AC role:\s*([A-Za-z][A-Za-z -]{0,31})\.?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex RolePattern();

    public static H3cControllerRoleFact Parse(string output, bool completedExec = false)
    {
        const string command = H3cAcResources.DisplayWlanAcRole;
        var transcript = H3cAcResources.ParseTranscript(command, output, completedExec);
        var failure = transcript.Status switch
        {
            CliTranscriptStatus.Unsupported or CliTranscriptStatus.PermissionDenied => "UNSUPPORTED",
            CliTranscriptStatus.Paged or CliTranscriptStatus.Incomplete => "PARTIAL",
            CliTranscriptStatus.Empty => "EMPTY",
            _ => null
        };
        if (failure is not null) return new(command, failure, null, "UNKNOWN", transcript.Notice);
        var roles = RolePattern().Matches(transcript.Output).Select(m => m.Groups[1].Value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (roles.Length != 1)
            return new(command, roles.Length > 1 ? "PARTIAL" : "UNRECOGNIZED", null, "UNKNOWN",
                roles.Length > 1 ? "CONFLICTING_CONTROLLER_ROLES" : "CONTROLLER_ROLE_NOT_RECOGNIZED");
        var normalized = roles[0].ToUpperInvariant() switch
        {
            "COMMON" => "COMMON",
            "MASTER" => "MASTER",
            "BACKUP" => "BACKUP",
            _ => "UNKNOWN"
        };
        return new(command, normalized == "UNKNOWN" ? "UNRECOGNIZED" : "PARSED", roles[0], normalized,
            normalized == "COMMON" ? "COMMON_ROLE_DOES_NOT_VERIFY_HA" : "DEVICE_REPORTED_ROLE_ONLY");
    }

    public static H3cParseResult ParseJson(string output, bool completedExec = false)
    {
        var result = Parse(output, completedExec);
        return new(result.Status, JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}
