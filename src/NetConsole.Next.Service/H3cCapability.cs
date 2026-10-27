using System.Text;
using System.Text.Json;

namespace NetConsole.Next.Service;

internal sealed record H3cCapabilityReading(
    string Status,
    string Vendor,
    string Version,
    string? Release,
    string? VersionFamily,
    bool Recognized,
    string Source,
    string SourceReference,
    string Confidence,
    long ObservedAtMs,
    IReadOnlyList<string> Capabilities,
    string? FailureCode)
{
    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions(JsonSerializerDefaults.Web));
}

/// <summary>
/// Pure capability analysis for synthetic H3C output. It deliberately has no
/// transport, credential, catalog or authorization side effects.
/// </summary>
internal sealed class H3cCapabilityService
{
    public const string MockSource = "MOCK";
    public const int MaximumInputBytes = 1024 * 1024;

    public H3cCapabilityReading AnalyzeMockText(string output, string sourceReference, long? observedAtMs = null)
    {
        ValidateSourceReference(sourceReference);
        var observed = observedAtMs ?? UtcNow();
        var text = output ?? string.Empty;
        if (Encoding.UTF8.GetByteCount(text) > MaximumInputBytes)
            return Failure("OUTPUT_LIMIT", sourceReference, observed);
        return Analyze(text, sourceReference, observed);
    }

    public H3cCapabilityReading AnalyzeMockBytes(ReadOnlySpan<byte> output, string? terminalEncoding,
        string sourceReference, long? observedAtMs = null)
    {
        ValidateSourceReference(sourceReference);
        var observed = observedAtMs ?? UtcNow();
        if (output.Length > MaximumInputBytes)
            return Failure("OUTPUT_LIMIT", sourceReference, observed);
        try
        {
            var decoded = TerminalTextEncoding.Decode(output, terminalEncoding);
            return Analyze(decoded, sourceReference, observed);
        }
        catch (DecoderFallbackException)
        {
            return Failure("INVALID_ENCODING", sourceReference, observed);
        }
    }

    private static H3cCapabilityReading Analyze(string output, string sourceReference, long observedAtMs)
    {
        if (string.IsNullOrWhiteSpace(output))
            return new("EMPTY", "unknown", string.Empty, null, null, false, MockSource, sourceReference,
                "NONE", observedAtMs, Array.Empty<string>(), null);
        if (H3cComwareParser.ContainsPaginationPrompt(output))
            return Failure("PAGING_DETECTED", sourceReference, observedAtMs);

        var version = H3cComwareParser.ParseVersion(output);
        var family = ResolveFamily(version.Version);
        if (family is null)
            return new("UNRECOGNIZED", version.Vendor, version.Version, version.Release, null, false,
                MockSource, sourceReference, "LOW", observedAtMs, Array.Empty<string>(), "UNRECOGNIZED_VERSION");

        var capabilities = new[]
        {
            "READONLY_DISPLAY_VERSION",
            "READONLY_INTERFACE_BRIEF",
            "PAGINATION_GUARD"
        };
        return new("CAPABILITIES_RESOLVED", version.Vendor, version.Version, version.Release, family, true,
            MockSource, sourceReference, "HIGH", observedAtMs, capabilities, null);
    }

    private static H3cCapabilityReading Failure(string code, string sourceReference, long observedAtMs) =>
        new(code, "unknown", string.Empty, null, null, false, MockSource, sourceReference, "NONE", observedAtMs,
            Array.Empty<string>(), code);

    private static string? ResolveFamily(string version)
    {
        var major = version.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return major switch
        {
            "5" => "V5",
            "7" => "V7",
            _ => null
        };
    }

    private static void ValidateSourceReference(string sourceReference)
    {
        if (string.IsNullOrWhiteSpace(sourceReference) || sourceReference.Length > 256 || sourceReference.Any(char.IsControl))
            throw new ArgumentException("Mock capability source reference must be a bounded non-control identifier.", nameof(sourceReference));
    }

    private static long UtcNow() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
