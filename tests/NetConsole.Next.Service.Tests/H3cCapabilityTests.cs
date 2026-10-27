using System.Text;
using System.Text.Json;

namespace NetConsole.Next.Service.Tests;

internal static class H3cCapabilityTests
{
    public static void Run()
    {
        var service = new H3cCapabilityService();
        var v7 = service.AnalyzeMockText(ReadFixture("display_version.txt"), "fixture:h3c:v7", 1_800_000_000_000);
        Assert(v7.Status == "CAPABILITIES_RESOLVED" && v7.Recognized && v7.VersionFamily == "V7" &&
               v7.Version == "7.1.070" && v7.Release == "6607P20" && v7.Source == "MOCK" && v7.Confidence == "HIGH" &&
               v7.Capabilities.Contains("READONLY_INTERFACE_BRIEF"), "V7 capability fixture must resolve a bounded read-only contract");

        var v5 = service.AnalyzeMockText(ReadFixture("display_version_v5.txt"), "fixture:h3c:v5", 1_800_000_000_001);
        Assert(v5.Status == "CAPABILITIES_RESOLVED" && v5.Recognized && v5.VersionFamily == "V5" &&
               v5.Version == "5.20" && v5.Release == "2220P05", "V5 capability fixture must preserve version and release provenance");

        var unknown = service.AnalyzeMockText("Vendor software, Version 9.9\npassword=synthetic-secret", "fixture:h3c:unknown", 1_800_000_000_002);
        Assert(unknown.Status == "UNRECOGNIZED" && !unknown.Recognized && unknown.Capabilities.Count == 0 &&
               unknown.FailureCode == "UNRECOGNIZED_VERSION", "unknown output must fail closed without capability claims");
        Assert(!unknown.ToJson().Contains("synthetic-secret", StringComparison.Ordinal),
            "capability result must not echo raw output or secrets");

        var paged = service.AnalyzeMockText("H3C Comware Software, Version 7.1.070\n---- More ----", "fixture:h3c:paged");
        Assert(paged.Status == "PAGING_DETECTED" && paged.Capabilities.Count == 0,
            "paging prompts must prevent a partial capability decision");

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var gb18030 = Encoding.GetEncoding(54936).GetBytes("H3C Comware Software, Version 5.20, Release 2220P05\n设备描述\n");
        var decoded = service.AnalyzeMockBytes(gb18030, "GB18030", "fixture:h3c:gb18030");
        Assert(decoded.Status == "CAPABILITIES_RESOLVED" && decoded.VersionFamily == "V5",
            "explicit GB18030 mock output must resolve without changing the contract");

        var invalidEncoding = service.AnalyzeMockBytes(new byte[] { 0xff, 0xfe, 0xfd }, "UTF-8", "fixture:h3c:invalid-encoding");
        Assert(invalidEncoding.Status == "INVALID_ENCODING" && invalidEncoding.Capabilities.Count == 0,
            "invalid terminal encoding must return a redacted failure category");
        var tooLarge = service.AnalyzeMockBytes(new byte[H3cCapabilityService.MaximumInputBytes + 1], "UTF-8", "fixture:h3c:too-large");
        Assert(tooLarge.Status == "OUTPUT_LIMIT" && tooLarge.Capabilities.Count == 0,
            "oversized mock output must be bounded before parsing");
        AssertThrows<ArgumentException>(() => service.AnalyzeMockText("x", "bad\nsource"));
        Assert(v7.ToJson().Contains("versionFamily", StringComparison.Ordinal), "capability result must have a stable JSON contract");
        Console.WriteLine("PASS: H3C V5/V7 mock capability service, UNRECOGNIZED fallback, source/confidence, pagination, encoding and redaction");
    }

    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "h3c", name));

    private static void Assert(bool condition, string detail)
    {
        if (!condition) throw new InvalidOperationException(detail);
    }

    private static void AssertThrows<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
