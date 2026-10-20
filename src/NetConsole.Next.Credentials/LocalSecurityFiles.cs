using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace NetConsole.Next.Credentials;

public static class LocalApiTokenStore
{
    public const string FileName = "local-api-token";

    public static string GetOrCreate(string dataRoot)
    {
        var path = TokenPath(dataRoot);
        Directory.CreateDirectory(dataRoot);
        if (File.Exists(path)) return Read(path);
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        try { WriteAtomic(path, token + "\n"); }
        catch (IOException) when (File.Exists(path)) { return Read(path); }
        return token;
    }

    public static string ReadExisting(string dataRoot) => Read(TokenPath(dataRoot));

    public static string Rotate(string dataRoot)
    {
        var path = TokenPath(dataRoot);
        Directory.CreateDirectory(dataRoot);
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        WriteAtomic(path, token + "\n", overwrite: true);
        return token;
    }

    private static string TokenPath(string root) => Path.Combine(Path.GetFullPath(root), FileName);

    private static string Read(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Local API token file cannot be a symbolic link or reparse point.");
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(path);
            if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                         UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        var token = File.ReadAllText(path, Encoding.ASCII).Trim();
        if (token.Length != 43 || token.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new InvalidDataException("Local API token file has an invalid format.");
        return token;
    }

    private static void WriteAtomic(string path, string content, bool overwrite = false)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       256, FileOptions.WriteThrough))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                var bytes = Encoding.ASCII.GetBytes(content);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (!overwrite)
            {
                try { File.Move(temp, path); }
                catch (IOException) when (File.Exists(path)) { }
            }
            else if (File.Exists(path)) File.Move(temp, path, overwrite: true);
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

[SupportedOSPlatform("windows")]
public static class WindowsDpapiCredentialVault
{
    private const string CredentialDirectory = "credentials";
    private const string CredentialFileName = "h3c-credentials.v1.json";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NetConsoleNext.H3C.Credential.v1");
    private static readonly object Gate = new();

    public static string? Get(string dataRoot, string reference)
    {
        EnsurePlatform();
        ValidateReference(reference);
        var entries = ReadEntries(dataRoot);
        if (!entries.TryGetValue(reference, out var encoded)) return null;
        byte[]? plaintext = null;
        try
        {
            plaintext = ProtectedData.Unprotect(Convert.FromBase64String(encoded), Entropy, DataProtectionScope.LocalMachine);
            return new UTF8Encoding(false, true).GetString(plaintext);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or DecoderFallbackException)
        {
            throw new InvalidDataException("Configured H3C credential cannot be decrypted on this Windows host.");
        }
        finally { if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext); }
    }

    public static void Set(string dataRoot, string reference, string secret)
    {
        EnsurePlatform();
        ValidateReference(reference);
        ArgumentException.ThrowIfNullOrEmpty(secret);
        lock (Gate)
        {
            var entries = ReadEntries(dataRoot);
            var bytes = Encoding.UTF8.GetBytes(secret);
            try { entries[reference] = Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy, DataProtectionScope.LocalMachine)); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            WriteEntries(dataRoot, entries);
        }
    }

    public static bool Revoke(string dataRoot, string reference)
    {
        EnsurePlatform();
        ValidateReference(reference);
        lock (Gate)
        {
            var entries = ReadEntries(dataRoot);
            if (!entries.Remove(reference)) return false;
            WriteEntries(dataRoot, entries);
            return true;
        }
    }

    private static Dictionary<string, string> ReadEntries(string dataRoot)
    {
        var path = CredentialPath(dataRoot);
        var root = Path.GetFullPath(dataRoot);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Credential vault data root cannot be a symbolic link or reparse point.");
        var directory = Path.GetDirectoryName(path)!;
        if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Credential vault directory cannot be a symbolic link or reparse point.");
        if (!File.Exists(path)) return new(StringComparer.Ordinal);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Credential vault file cannot be a symbolic link or reparse point.");
        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(path))
            ?? throw new InvalidDataException("Credential vault file is malformed.");
        foreach (var reference in parsed.Keys) ValidateReference(reference);
        return new(parsed, StringComparer.Ordinal);
    }

    private static void WriteEntries(string dataRoot, Dictionary<string, string> entries)
    {
        var directory = Path.Combine(Path.GetFullPath(dataRoot), CredentialDirectory);
        Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Credential vault directory cannot be a symbolic link or reparse point.");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(directory, CredentialFileName);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                JsonSerializer.Serialize(stream, entries);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Move(temp, path, overwrite: true);
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static string CredentialPath(string root) => Path.Combine(Path.GetFullPath(root), CredentialDirectory, CredentialFileName);
    private static void ValidateReference(string reference)
    {
        if (reference.Length is < 1 or > 64 || !char.IsAsciiLetterUpper(reference[0]) ||
            reference.Any(c => !(char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_')))
            throw new ArgumentException("Credential reference must match [A-Z][A-Z0-9_]{0,63}.", nameof(reference));
    }

    private static void EnsurePlatform()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows DPAPI credential storage is only available on Windows.");
    }
}
