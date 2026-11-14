using NetConsole.Next.Credentials;
using System.Diagnostics.CodeAnalysis;

namespace NetConsole.Next.Service;

internal interface IH3cCredentialAuthority
{
    string? GetPassword(H3cDeviceProfileSnapshot profile);
    string? GetPassword(string profileId);
    bool HasPassword(H3cDeviceProfileSnapshot profile);
    bool HasPassword(string profileId);
    void SetPassword(H3cDeviceProfileSnapshot profile, string password);
    void SetPassword(string profileId, string password);
    bool RevokePassword(H3cDeviceProfileSnapshot profile);
    bool RevokePassword(string profileId);
}

[SuppressMessage("Interoperability", "CA1416", Justification = "Every public operation checks Windows before calling the DPAPI vault.")]
internal sealed class WindowsDpapiH3cCredentialAuthority(string dataRoot) : IH3cCredentialAuthority
{
    public string? GetPassword(H3cDeviceProfileSnapshot profile) =>
        ReadPassword(KeyFor(profile));

    public string? GetPassword(string profileId) =>
        ReadPassword(KeyFor(profileId));

    public bool HasPassword(H3cDeviceProfileSnapshot profile) =>
        ContainsPassword(KeyFor(profile));

    public bool HasPassword(string profileId) =>
        ContainsPassword(KeyFor(profileId));

    public void SetPassword(H3cDeviceProfileSnapshot profile, string password) =>
        WritePassword(KeyFor(profile), password);

    public void SetPassword(string profileId, string password) =>
        WritePassword(KeyFor(profileId), password);

    public bool RevokePassword(H3cDeviceProfileSnapshot profile) =>
        RemovePassword(KeyFor(profile));

    public bool RevokePassword(string profileId) =>
        RemovePassword(KeyFor(profileId));

    private string? ReadPassword(string key)
    {
        EnsureWindows();
        return WindowsDpapiCredentialVault.Get(dataRoot, key);
    }

    private bool ContainsPassword(string key)
    {
        EnsureWindows();
        return WindowsDpapiCredentialVault.Contains(dataRoot, key);
    }

    private void WritePassword(string key, string password)
    {
        EnsureWindows();
        WindowsDpapiCredentialVault.Set(dataRoot, key, password);
    }

    private bool RemovePassword(string key)
    {
        EnsureWindows();
        return WindowsDpapiCredentialVault.Revoke(dataRoot, key);
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Device credential storage requires Windows DPAPI.");
    }

    internal static string KeyFor(H3cDeviceProfileSnapshot profile) => KeyFor(profile.Id);

    internal static string KeyFor(string profileId)
    {
        if (!Guid.TryParseExact(profileId, "N", out var parsed))
            throw new ArgumentException("H3C profile id must be a 32-character GUID.", nameof(profileId));
        return "DEVICE_" + parsed.ToString("N").ToUpperInvariant();
    }
}

// The Catalog adapter exists for isolated unit fixtures only. Production DI
// binds WindowsDpapiH3cCredentialAuthority and never uses this adapter.
internal sealed class CatalogH3cCredentialAuthority(Catalog catalog) : IH3cCredentialAuthority
{
    public string? GetPassword(H3cDeviceProfileSnapshot profile) => catalog.GetH3cCredentialPassword(profile.Id);

    public string? GetPassword(string profileId) => catalog.GetH3cCredentialPassword(profileId);

    public bool HasPassword(H3cDeviceProfileSnapshot profile) =>
        !string.IsNullOrEmpty(catalog.GetH3cCredentialPassword(profile.Id));

    public bool HasPassword(string profileId) =>
        !string.IsNullOrEmpty(catalog.GetH3cCredentialPassword(profileId));

    public void SetPassword(H3cDeviceProfileSnapshot profile, string password) =>
        catalog.UpdateH3cCredential(profile.Id, password, clearPassword: false);

    public void SetPassword(string profileId, string password) =>
        catalog.UpdateH3cCredential(profileId, password, clearPassword: false);

    public bool RevokePassword(H3cDeviceProfileSnapshot profile)
    {
        catalog.UpdateH3cCredential(profile.Id, null, clearPassword: true);
        return true;
    }

    public bool RevokePassword(string profileId)
    {
        catalog.UpdateH3cCredential(profileId, null, clearPassword: true);
        return true;
    }
}

internal sealed class H3cCredentialMigrationRequiredException() : InvalidOperationException(
    "The H3C profile still has a legacy Catalog credential and must be migrated explicitly.");

internal sealed class H3cCredentialCompensationException(Exception inner) : IOException(
    "H3C credential storage compensation failed; the profile write was not confirmed.", inner);

internal sealed class H3cProfileCredentialService(Catalog catalog, IH3cCredentialAuthority authority)
{
    public void CreateOrUpdate(H3cDeviceProfileSnapshot profile,
        DeviceInventoryWriteRequest? onboardInventory = null, string? actor = null)
    {
        var existing = catalog.GetH3cDeviceProfile(profile.Id);
        var previous = existing is null ? null : authority.GetPassword(existing);
        var legacy = existing is null ? null : catalog.GetH3cCredentialPassword(profile.Id);
        var requested = profile.ClearPassword ? null : profile.Password;
        var desired = string.IsNullOrEmpty(requested) ? previous : requested;
        if (existing is not null && string.IsNullOrEmpty(requested) && !profile.ClearPassword &&
            previous is null && !string.IsNullOrEmpty(legacy))
            throw new H3cCredentialMigrationRequiredException();

        var authorityProfile = profile with { Password = null, ClearPassword = false };
        var changed = !string.Equals(previous, desired, StringComparison.Ordinal);
        if (changed)
        {
            if (desired is null) authority.RevokePassword(authorityProfile);
            else authority.SetPassword(authorityProfile, desired);
        }

        try
        {
            // The Catalog password column is deliberately cleared on the
            // production path. The DPAPI vault is the only runtime source.
            catalog.CreateH3cDeviceProfile(authorityProfile with { ClearPassword = true }, onboardInventory, actor);
        }
        catch
        {
            if (changed)
            {
                try
                {
                    if (previous is null) authority.RevokePassword(authorityProfile);
                    else authority.SetPassword(authorityProfile, previous);
                }
                catch (Exception compensation) { throw new H3cCredentialCompensationException(compensation); }
            }
            throw;
        }
    }

    public void UpdateCredential(string id, string? password, bool clearPassword)
    {
        var existing = catalog.GetH3cDeviceProfile(id) ?? throw new KeyNotFoundException("H3C device profile was not found.");
        var previous = authority.GetPassword(existing);
        var legacy = catalog.GetH3cCredentialPassword(id);
        if (!clearPassword && string.IsNullOrEmpty(password) && previous is null && !string.IsNullOrEmpty(legacy))
            throw new H3cCredentialMigrationRequiredException();

        var desired = clearPassword ? null : string.IsNullOrEmpty(password) ? previous : password;
        var changed = !string.Equals(previous, desired, StringComparison.Ordinal);
        if (changed)
        {
            if (desired is null) authority.RevokePassword(existing);
            else authority.SetPassword(existing, desired);
        }

        try
        {
            catalog.UpdateH3cCredential(id, null, clearPassword: true);
        }
        catch
        {
            if (changed)
            {
                try
                {
                    if (previous is null) authority.RevokePassword(existing);
                    else authority.SetPassword(existing, previous);
                }
                catch (Exception compensation) { throw new H3cCredentialCompensationException(compensation); }
            }
            throw;
        }
    }
}
