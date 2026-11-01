using NetConsole.Next.Credentials;

namespace NetConsole.Next.Service;

/// <summary>
/// Keeps the user-facing device workflow independent from the internal DPAPI
/// credential reference.  The service never writes a clear-text secret to the
/// catalog; the only durable secret operation is the DPAPI vault write.
/// </summary>
internal static class H3cDeviceCredentialService
{
    public static string CreateReference()
    {
        // The reference is an internal join key, not a user-facing identifier.
        return "H3C_" + Guid.NewGuid().ToString("N").ToUpperInvariant();
    }

    public static void ValidatePassword(string? password, string parameterName = "password")
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("A device login password is required.", parameterName);
        if (password.Length > 512 || password.Any(char.IsControl))
            throw new ArgumentException("The device login password is outside the supported format.", parameterName);
    }

    public static void Store(string dataRoot, string reference, string password)
    {
        ValidatePassword(password);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Device credential storage requires Windows DPAPI.");
        WindowsDpapiCredentialVault.Set(dataRoot, reference, password);
    }

    public static bool Revoke(string dataRoot, string reference)
    {
        if (!OperatingSystem.IsWindows()) return false;
        return WindowsDpapiCredentialVault.Revoke(dataRoot, reference);
    }

    public static string? ReadForProbe(string dataRoot, string reference) => OperatingSystem.IsWindows()
        ? WindowsDpapiCredentialVault.Get(dataRoot, reference)
        : null;

}
