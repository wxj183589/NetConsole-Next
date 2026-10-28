using NetConsole.Next.Service;

namespace NetConsole.Next.Service.Tests;

internal static class RuntimeSettingsTests
{
    public static void Run()
    {
        Assert(RuntimeSettings.ValidateWebBindAddress(null) == "127.0.0.1", "missing Web bind must default to IPv4 loopback");
        Assert(RuntimeSettings.ValidateWebBindAddress("") == "127.0.0.1", "empty Web bind must default to IPv4 loopback");
        Assert(RuntimeSettings.ValidateWebBindAddress("localhost") == "127.0.0.1", "localhost must remain a valid local alias");
        Assert(RuntimeSettings.ValidateWebBindAddress("LOCALHOST") == "127.0.0.1", "localhost matching must be case-insensitive");
        Assert(RuntimeSettings.ValidateWebBindAddress("127.0.0.1") == "127.0.0.1", "IPv4 loopback must remain valid");
        Assert(RuntimeSettings.ValidateWebBindAddress("::1") == "::1", "IPv6 loopback must remain valid");

        foreach (var value in new[] { "0.0.0.0", "::", "192.168.1.25", "10.0.0.1", "2001:db8::1", "not-an-address" })
            ExpectInvalid(value);

        RuntimeSettings.RejectRemoteWebOptIn(null);
        RuntimeSettings.RejectRemoteWebOptIn("0");
        try
        {
            RuntimeSettings.RejectRemoteWebOptIn("1");
            throw new InvalidOperationException("NETCONSOLE_NEXT_WEB_LAN=1 must be rejected");
        }
        catch (InvalidOperationException ex)
        {
            Assert(ex.Message.Contains("loopback-only", StringComparison.OrdinalIgnoreCase),
                "remote Web opt-in rejection must explain the loopback-only security boundary");
        }

        Console.WriteLine("PASS: Web binding accepts loopback only and rejects wildcard, LAN and remote opt-in values");
    }

    private static void ExpectInvalid(string value)
    {
        try
        {
            RuntimeSettings.ValidateWebBindAddress(value);
            throw new InvalidOperationException($"non-loopback Web bind was accepted: {value}");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("loopback address", StringComparison.OrdinalIgnoreCase)) { }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
