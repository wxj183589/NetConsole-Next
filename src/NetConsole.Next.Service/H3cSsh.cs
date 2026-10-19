using System.Text;
using Microsoft.Data.Sqlite;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace NetConsole.Next.Service;

internal interface IH3cCredentialProvider
{
    string? GetPassword(string reference);
}

internal sealed class H3cCredentialUnavailableException() : InvalidOperationException(
    "Configured H3C credential reference has no available secret.") { }

internal sealed class H3cHostKeyMismatchException() : InvalidOperationException(
    "SSH host key did not match the configured SHA256 pin.") { }

internal sealed class H3cAuthenticationFailedException() : InvalidOperationException(
    "SSH authentication failed for the configured H3C profile.") { }

// Bootstrap provider only: values are read at use time and never persisted, returned, or logged.
internal sealed class EnvironmentH3cCredentialProvider : IH3cCredentialProvider
{
    public string? GetPassword(string reference) =>
        H3cCredentialReference.IsValid(reference)
            ? Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_CREDENTIAL_" + reference)
            : null;
}

internal static class H3cSshAlgorithmPolicy
{
    private static readonly HashSet<string> KeyExchanges = new(StringComparer.Ordinal)
    {
        "mlkem768x25519-sha256", "sntrup761x25519-sha512", "sntrup761x25519-sha512@openssh.com",
        "curve25519-sha256", "curve25519-sha256@libssh.org", "ecdh-sha2-nistp256", "ecdh-sha2-nistp384",
        "ecdh-sha2-nistp521", "diffie-hellman-group-exchange-sha256", "diffie-hellman-group14-sha256",
        "diffie-hellman-group16-sha512", "diffie-hellman-group18-sha512"
    };

    private static readonly HashSet<string> Encryptions = new(StringComparer.Ordinal)
    {
        "aes128-ctr", "aes192-ctr", "aes256-ctr", "aes128-gcm@openssh.com", "aes256-gcm@openssh.com",
        "chacha20-poly1305@openssh.com"
    };

    private static readonly HashSet<string> MacAlgorithms = new(StringComparer.Ordinal)
    {
        "hmac-sha2-256", "hmac-sha2-512", "hmac-sha2-256-etm@openssh.com", "hmac-sha2-512-etm@openssh.com"
    };

    private static readonly HashSet<string> HostKeys = new(StringComparer.Ordinal)
    {
        "ssh-ed25519", "ecdsa-sha2-nistp256", "ecdsa-sha2-nistp384", "ecdsa-sha2-nistp521",
        "rsa-sha2-256", "rsa-sha2-512"
    };

    public static void Apply(Renci.SshNet.ConnectionInfo info)
    {
        KeepOnly(info.KeyExchangeAlgorithms.Keys, name => info.KeyExchangeAlgorithms.Remove(name), KeyExchanges);
        KeepOnly(info.Encryptions.Keys, name => info.Encryptions.Remove(name), Encryptions);
        KeepOnly(info.HmacAlgorithms.Keys, name => info.HmacAlgorithms.Remove(name), MacAlgorithms);
        KeepOnly(info.HostKeyAlgorithms.Keys, name => info.HostKeyAlgorithms.Remove(name), HostKeys);
        if (info.KeyExchangeAlgorithms.Count == 0 || info.Encryptions.Count == 0 ||
            info.HmacAlgorithms.Count == 0 || info.HostKeyAlgorithms.Count == 0)
            throw new NotSupportedException("SSH.NET did not expose any algorithm from the configured H3C allowlist.");
    }

    private static void KeepOnly(IEnumerable<string> names, Action<string> remove, HashSet<string> allowed)
    {
        foreach (var name in names.Where(name => !allowed.Contains(name)).ToArray()) remove(name);
    }
}

internal sealed class H3cSshDeviceSessionFactory(Catalog catalog, IH3cCredentialProvider credentials) : IDeviceSessionFactory
{
    public IDeviceSession Create(string target)
    {
        var profile = catalog.GetH3cDeviceProfile(target)
            ?? throw new KeyNotFoundException("H3C device profile was not found.");
        if (!profile.Enabled) throw new InvalidOperationException("H3C device profile is disabled.");
        return new H3cSshDeviceSession(profile, credentials);
    }
}

internal sealed class H3cSshDeviceSession(H3cDeviceProfileSnapshot profile, IH3cCredentialProvider credentials) : IDeviceSession
{
    private SshClient? _client;
    private bool _hostKeyAccepted;
    private bool _hostKeySeen;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var password = credentials.GetPassword(profile.CredentialReference);
        if (string.IsNullOrEmpty(password))
            throw new H3cCredentialUnavailableException();

        var info = new Renci.SshNet.ConnectionInfo(profile.Host, profile.Port, profile.Username,
            new PasswordAuthenticationMethod(profile.Username, password))
        {
            Timeout = TimeSpan.FromSeconds(10),
            Encoding = new UTF8Encoding(false, true)
        };
        H3cSshAlgorithmPolicy.Apply(info);
        _client = new SshClient(info);
        _client.HostKeyReceived += (_, args) =>
        {
            _hostKeySeen = true;
            _hostKeyAccepted = H3cHostKeyFingerprint.Matches(profile.HostKeySha256, args.FingerPrintSHA256);
            args.CanTrust = _hostKeyAccepted;
        };
        try
        {
            await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            if (!_hostKeyAccepted) throw new H3cHostKeyMismatchException();
        }
        catch (SshAuthenticationException)
        {
            _client.Dispose();
            _client = null;
            throw new H3cAuthenticationFailedException();
        }
        catch (Exception) when (_hostKeySeen && !_hostKeyAccepted)
        {
            _client.Dispose();
            _client = null;
            throw new H3cHostKeyMismatchException();
        }
        catch
        {
            _client.Dispose();
            _client = null;
            throw;
        }
    }

    public async Task<string> ExecuteAsync(string command, CancellationToken cancellationToken)
    {
        if (!H3cCommandPolicy.IsAllowed(command))
            throw new ArgumentException("Only the H3C read-only version and interface-brief commands are allowed.", nameof(command));
        var client = _client;
        if (client is null || !client.IsConnected) throw new InvalidOperationException("H3C SSH session is not connected.");
        cancellationToken.ThrowIfCancellationRequested();
        using var sshCommand = client.CreateCommand(command);
        // The persisted scheduler imposes the stricter per-run deadline (maximum ten minutes).
        sshCommand.CommandTimeout = TimeSpan.FromMinutes(11);
        using var commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var execution = sshCommand.ExecuteAsync(commandCancellation.Token);
        var outputLimitExceeded = false;
        while (!execution.IsCompleted)
        {
            if (StreamLengthExceedsLimit(sshCommand.OutputStream) ||
                StreamLengthExceedsLimit(sshCommand.ExtendedOutputStream))
            {
                outputLimitExceeded = true;
                commandCancellation.Cancel();
                try { client.Disconnect(); }
                catch (Exception) { }
                break;
            }
            await Task.WhenAny(execution, Task.Delay(10, cancellationToken)).ConfigureAwait(false);
        }
        outputLimitExceeded |= StreamLengthExceedsLimit(sshCommand.OutputStream) ||
                               StreamLengthExceedsLimit(sshCommand.ExtendedOutputStream);
        try { await execution.ConfigureAwait(false); }
        catch when (outputLimitExceeded) { }
        if (outputLimitExceeded)
            throw new InvalidDataException($"H3C response exceeded the {H3cRawOutput.MaximumBytes}-byte output or error stream limit.");
        if (sshCommand.ExitStatus is not null and not 0)
            throw new InvalidDataException($"H3C read-only command returned exit status {sshCommand.ExitStatus}.");
        var output = sshCommand.Result;
        if (output.Contains("---- More ----", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Press any key", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("H3C response was paginated; no partial result was accepted.");
        if (Encoding.UTF8.GetByteCount(output) > H3cRawOutput.MaximumBytes)
            throw new InvalidDataException($"H3C response exceeds the {H3cRawOutput.MaximumBytes}-byte result limit.");
        return output;
    }

    private static bool StreamLengthExceedsLimit(Stream stream)
    {
        try { return stream.CanSeek && stream.Length > H3cRawOutput.MaximumBytes; }
        catch (NotSupportedException) { return false; }
    }

    public ValueTask DisposeAsync()
    {
        _client?.Dispose();
        _client = null;
        return ValueTask.CompletedTask;
    }
}

internal sealed class H3cTaskRawResultSink(Catalog catalog, Action<Exception>? storageFailure = null) : ITaskResultSink
{
    public async Task PersistAsync(string runId, TaskDefinitionSnapshot definition, string output)
    {
        if (definition.TaskType != "H3C_SSH_READONLY") return;
        var profile = catalog.GetH3cDeviceProfile(definition.Target)
            ?? throw new InvalidOperationException("H3C result references a missing device profile.");
        var capturedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var outputByteCount = Encoding.UTF8.GetByteCount(output);
        if (outputByteCount > H3cRawOutput.MaximumBytes)
            throw new InvalidDataException("H3C response exceeds the Raw output byte limit.");

        var writer = new RawSegmentWriterFactory().CreateH3c(catalog, profile.Id);
        try
        {
            var outputSha = writer.AppendH3cCommand(profile.Id, runId, profile.Host,
                definition.Command, output, capturedMs);
            await writer.FlushAsync().ConfigureAwait(false);
            await writer.SealAsync().ConfigureAwait(false);
            var parsed = H3cComwareParser.ToParsedJson(definition.Command, output);
            catalog.RegisterTaskRunRawOutput(runId, writer.RelativePath, definition.Command, outputByteCount,
                Convert.ToHexString(outputSha).ToLowerInvariant(), writer.Sha256!, capturedMs, parsed);
        }
        catch (Exception ex)
        {
            try { await writer.PreserveOpenAsync().ConfigureAwait(false); }
            catch (Exception preserveError) { storageFailure?.Invoke(preserveError); }
            if (ex is not InvalidDataException && (ex is IOException or UnauthorizedAccessException or SqliteException))
            {
                try { storageFailure?.Invoke(ex); }
                catch { }
            }
            throw;
        }
    }
}
