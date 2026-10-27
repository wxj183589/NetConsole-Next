using System.Text;
using System.Text.Json;

namespace NetConsole.Next.Service.Tests;

internal static class H3cCapabilityTests
{
    public static void Run()
    {
        var service = new H3cCapabilityService();
        var empty = service.AnalyzeMockText(string.Empty, "fixture:h3c:empty", 1_800_000_000_010);
        Assert(empty.Status == "EMPTY" && empty.Capabilities.Count == 0,
            "empty text must preserve the EMPTY capability result");

        var v7 = service.AnalyzeMockText(ReadFixture("display_version.txt"), "fixture:h3c:v7", 1_800_000_000_000);
        Assert(v7.Status == "CAPABILITIES_RESOLVED" && v7.Recognized && v7.VersionFamily == "V7" &&
               v7.Version == "7.1.070" && v7.Release == "6607P20" && v7.Source == "MOCK" && v7.Confidence == "HIGH" &&
               v7.Capabilities.Contains("READONLY_INTERFACE_BRIEF"), "V7 capability fixture must resolve a bounded read-only contract");

        var v7Bytes = service.AnalyzeMockBytes(Encoding.UTF8.GetBytes(ReadFixture("display_version.txt")), "UTF-8",
            "fixture:h3c:v7", 1_800_000_000_000);
        Assert(v7.ToJson() == v7Bytes.ToJson(),
            "text and UTF-8 byte inputs must produce the same capability result");

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

        var chinese = "H3C Comware Software, Version 5.20, Release 2220P05\n线路描述：车站控制器\n";
        var chineseResult = service.AnalyzeMockText(chinese, "fixture:h3c:chinese", 1_800_000_000_020);
        Assert(chineseResult.Status == "CAPABILITIES_RESOLVED" && chineseResult.VersionFamily == "V5",
            "normal Chinese UTF-8 text must retain the V5 capability result");

        var unicodeBoundary = "H3C Comware Software, Version 7.1.070\n" + char.ConvertFromUtf32(0x10FFFF);
        var unicodeResult = service.AnalyzeMockText(unicodeBoundary, "fixture:h3c:unicode-boundary", 1_800_000_000_021);
        Assert(unicodeResult.Status == "CAPABILITIES_RESOLVED" && unicodeResult.VersionFamily == "V7",
            "valid Unicode boundary code points must remain accepted");

        var belowMaximum = BuildUtf8Text(H3cCapabilityService.MaximumInputBytes - 1, "中");
        Assert(Encoding.UTF8.GetByteCount(belowMaximum) == H3cCapabilityService.MaximumInputBytes - 1,
            "below-limit UTF-8 fixture must have the exact intended byte length");
        var belowResult = service.AnalyzeMockText(belowMaximum, "fixture:h3c:below-limit", 1_800_000_000_022);
        Assert(belowResult.Status == "CAPABILITIES_RESOLVED" && belowResult.VersionFamily == "V7",
            "text below the 1 MiB UTF-8 limit must be parsed normally");

        var exactlyMaximum = BuildUtf8Text(H3cCapabilityService.MaximumInputBytes, "中");
        Assert(Encoding.UTF8.GetByteCount(exactlyMaximum) == H3cCapabilityService.MaximumInputBytes,
            "exact-limit UTF-8 fixture must have exactly 1 MiB");
        var exactResult = service.AnalyzeMockText(exactlyMaximum, "fixture:h3c:exact-limit", 1_800_000_000_023);
        Assert(exactResult.Status == "CAPABILITIES_RESOLVED" && exactResult.VersionFamily == "V7",
            "text at exactly 1 MiB must be accepted");

        var overMaximum = BuildUtf8Text(H3cCapabilityService.MaximumInputBytes + 1, "中");
        Assert(Encoding.UTF8.GetByteCount(overMaximum) == H3cCapabilityService.MaximumInputBytes + 1,
            "over-limit UTF-8 fixture must have exactly one excess byte");
        var overResult = service.AnalyzeMockText(overMaximum, "fixture:h3c:over-limit", 1_800_000_000_024);
        Assert(overResult.Status == "OUTPUT_LIMIT" && overResult.FailureCode == "OUTPUT_LIMIT" && !overResult.Recognized,
            "text over the 1 MiB UTF-8 limit must fail closed with OUTPUT_LIMIT");

        var tooLarge = service.AnalyzeMockBytes(new byte[H3cCapabilityService.MaximumInputBytes + 1], "UTF-8", "fixture:h3c:too-large");
        Assert(tooLarge.Status == "OUTPUT_LIMIT" && tooLarge.Capabilities.Count == 0,
            "oversized mock output must be bounded before parsing");
        var extreme = service.AnalyzeMockText(new string('x', H3cCapabilityService.MaximumInputBytes * 4), "fixture:h3c:extreme", 1_800_000_000_025);
        Assert(extreme.Status == "OUTPUT_LIMIT" && extreme.FailureCode == "OUTPUT_LIMIT",
            "extremely oversized text must be rejected without entering the parser");
        var nullText = service.AnalyzeMockText(null!, "fixture:h3c:null", 1_800_000_000_026);
        Assert(nullText.Status == "EMPTY" && nullText.Capabilities.Count == 0,
            "null text must preserve the existing null-as-empty behavior");
        AssertThrows<ArgumentException>(() => service.AnalyzeMockText("x", "bad\nsource"));
        Assert(v7.ToJson().Contains("versionFamily", StringComparison.Ordinal), "capability result must have a stable JSON contract");
        Console.WriteLine("PASS: H3C V5/V7 mock capability service, UNRECOGNIZED fallback, source/confidence, pagination, encoding and redaction");
    }

    private static string BuildUtf8Text(int targetBytes, string filler)
    {
        const string prefix = "H3C Comware Software, Version 7.1.070\n";
        var prefixBytes = Encoding.UTF8.GetByteCount(prefix);
        var fillerBytes = Encoding.UTF8.GetByteCount(filler);
        var remaining = targetBytes - prefixBytes;
        if (remaining < 0 || fillerBytes == 0) throw new InvalidOperationException("Invalid UTF-8 test fixture size.");

        var builder = new StringBuilder(prefix, targetBytes);
        while (remaining >= fillerBytes)
        {
            builder.Append(filler);
            remaining -= fillerBytes;
        }
        while (remaining > 0)
        {
            builder.Append('A');
            remaining--;
        }
        return builder.ToString();
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
