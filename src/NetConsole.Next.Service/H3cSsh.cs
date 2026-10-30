using System.Buffers;
using System.Diagnostics;
using System.Text;
using Microsoft.Data.Sqlite;
using Renci.SshNet;
using Renci.SshNet.Common;
using NetConsole.Next.Credentials;

namespace NetConsole.Next.Service;

internal interface IH3cCredentialProvider
{
    string? GetPassword(string reference);
}

internal sealed class H3cCredentialUnavailableException() : InvalidOperationException(
    "Configured H3C credential reference has no available secret.") { }

internal sealed class H3cHostKeyMismatchException() : InvalidOperationException(
    "SSH host key did not match the configured SHA256 pin.") { }

internal sealed class H3cHostKeyMissingException() : InvalidOperationException(
    "SSH connection completed without a verifiable host key.") { }

internal sealed class H3cAuthenticationFailedException() : InvalidOperationException(
    "SSH authentication failed for the configured H3C profile.") { }

internal sealed class H3cConnectionTimeoutException() : TimeoutException(
    "H3C SSH connection timed out.") { }

internal sealed class H3cUnsupportedAlgorithmException() : InvalidOperationException(
    "The SSH peer has no algorithm in the configured security allowlist.") { }

internal sealed class H3cProtocolNegotiationException() : InvalidOperationException(
    "The SSH peer could not negotiate a compatible protocol algorithm.") { }

internal sealed class H3cOutputLimitException() : Exception(
    "H3C command output exceeded the configured combined output/error limit.") { }

internal sealed class H3cInvalidEncodingException() : Exception(
    "H3C command output is not valid for the configured terminal encoding.") { }

internal sealed class H3cCommandRejectedException() : Exception(
    "H3C read-only command failed or returned an SSH error stream.") { }

internal sealed class H3cPaginationDetectedException() : Exception(
    "H3C response contains a paging prompt and is incomplete.") { }

internal sealed class H3cPartialOutputException(
    string output, string failureCode, bool isCancellation) : IOException(failureCode)
{
    public string Output { get; } = output;
    public string FailureCode { get; } = failureCode;
    public bool IsCancellation { get; } = isCancellation;
}

// The bootstrap provider is only selected for an explicit Development configuration.
internal sealed class EnvironmentH3cCredentialProvider : IH3cCredentialProvider
{
    private readonly bool _enabled;

    public EnvironmentH3cCredentialProvider(bool enabled) => _enabled = enabled;

    public string? GetPassword(string reference) =>
        _enabled && H3cCredentialReference.IsValid(reference)
            ? Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_CREDENTIAL_" + reference)
            : null;
}

internal sealed class WindowsDpapiH3cCredentialProvider(string dataRoot) : IH3cCredentialProvider
{
    public string? GetPassword(string reference) => OperatingSystem.IsWindows()
        ? WindowsDpapiCredentialVault.Get(dataRoot, reference)
        : null;
}

internal sealed class UnavailableH3cCredentialProvider : IH3cCredentialProvider
{
    public string? GetPassword(string reference) => null;
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

    private static readonly HashSet<string> LegacyKeyExchanges = new(StringComparer.Ordinal)
    {
        "diffie-hellman-group14-sha1", "diffie-hellman-group-exchange-sha1"
    };
    private static readonly HashSet<string> LegacyEncryptions = new(StringComparer.Ordinal)
    {
        "aes128-cbc", "aes192-cbc", "aes256-cbc"
    };
    private static readonly HashSet<string> LegacyMacAlgorithms = new(StringComparer.Ordinal)
    {
        "hmac-sha1", "hmac-sha1-96"
    };
    private static readonly HashSet<string> LegacyHostKeys = new(StringComparer.Ordinal)
    {
        "ssh-rsa"
    };

    public static void Apply(Renci.SshNet.ConnectionInfo info, bool allowLegacyAlgorithms = false)
    {
        KeepOnly(info.KeyExchangeAlgorithms.Keys, name => info.KeyExchangeAlgorithms.Remove(name),
            allowLegacyAlgorithms ? KeyExchanges.Concat(LegacyKeyExchanges).ToHashSet(StringComparer.Ordinal) : KeyExchanges);
        KeepOnly(info.Encryptions.Keys, name => info.Encryptions.Remove(name),
            allowLegacyAlgorithms ? Encryptions.Concat(LegacyEncryptions).ToHashSet(StringComparer.Ordinal) : Encryptions);
        KeepOnly(info.HmacAlgorithms.Keys, name => info.HmacAlgorithms.Remove(name),
            allowLegacyAlgorithms ? MacAlgorithms.Concat(LegacyMacAlgorithms).ToHashSet(StringComparer.Ordinal) : MacAlgorithms);
        KeepOnly(info.HostKeyAlgorithms.Keys, name => info.HostKeyAlgorithms.Remove(name),
            allowLegacyAlgorithms ? HostKeys.Concat(LegacyHostKeys).ToHashSet(StringComparer.Ordinal) : HostKeys);
        if (info.KeyExchangeAlgorithms.Count == 0 || info.Encryptions.Count == 0 ||
            info.HmacAlgorithms.Count == 0 || info.HostKeyAlgorithms.Count == 0)
            throw new H3cUnsupportedAlgorithmException();
    }

    private static void KeepOnly(IEnumerable<string> names, Action<string> remove, HashSet<string> allowed)
    {
        foreach (var name in names.Where(name => !allowed.Contains(name)).ToArray()) remove(name);
    }
}

internal interface IH3cSshTransportFactory
{
    IH3cSshTransport Create(H3cDeviceProfileSnapshot profile, string password);
}

internal interface IH3cSshTransport : IDisposable
{
    bool IsConnected { get; }
    Task ConnectAsync(Func<string?, string?, bool> verifyHostKey, CancellationToken cancellationToken);
    Task<string> ExecuteAsync(string command, CancellationToken cancellationToken);
    void Abort();
}

internal sealed record H3cCapturedOutput(string Text, long StderrByteCount);

internal sealed class SshNetH3cSshTransportFactory : IH3cSshTransportFactory
{
    public IH3cSshTransport Create(H3cDeviceProfileSnapshot profile, string password) =>
        new SshNetH3cSshTransport(profile, password);
}

internal sealed class SshNetH3cSshTransport : IH3cSshTransport
{
    private readonly SshClient _client;
    private readonly string _terminalEncoding;
    private bool _hostKeySeen;
    private bool _hostKeyAccepted;
    private bool _disposed;
    private long _hostKeyAcceptedTimestamp;

    public SshNetH3cSshTransport(H3cDeviceProfileSnapshot profile, string password)
    {
        var info = new Renci.SshNet.ConnectionInfo(profile.Host, profile.Port, profile.Username,
            new PasswordAuthenticationMethod(profile.Username, password))
        {
            Timeout = TimeSpan.FromSeconds(10),
            Encoding = TerminalTextEncoding.Resolve(profile.TerminalEncoding)
        };
        _terminalEncoding = profile.TerminalEncoding;
        H3cSshAlgorithmPolicy.Apply(info, profile.AllowLegacyAlgorithms);
        _client = new SshClient(info);
    }

    public bool IsConnected => !_disposed && _client.IsConnected;

    public async Task ConnectAsync(Func<string?, string?, bool> verifyHostKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verifyHostKey);
        var started = Stopwatch.GetTimestamp();
        _client.HostKeyReceived += (_, args) =>
        {
            _hostKeySeen = true;
            _hostKeyAccepted = verifyHostKey(args.HostKeyName, args.FingerPrintSHA256);
            args.CanTrust = _hostKeyAccepted;
            if (_hostKeyAccepted) Interlocked.Exchange(ref _hostKeyAcceptedTimestamp, Stopwatch.GetTimestamp());
        };
        try
        {
            await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            if (!_hostKeySeen) throw new H3cHostKeyMissingException();
            if (!_hostKeyAccepted) throw new H3cHostKeyMismatchException();
        }
        catch (SshAuthenticationException)
        {
            Abort();
            throw new H3cAuthenticationFailedException();
        }
        catch (SshOperationTimeoutException)
        {
            Abort();
            throw new H3cConnectionTimeoutException();
        }
        catch (SshConnectionException ex) when (IsAlgorithmNegotiationFailure(ex.Message))
        {
            Abort();
            throw new H3cProtocolNegotiationException();
        }
        catch (Exception) when (_hostKeySeen && !_hostKeyAccepted)
        {
            Abort();
            throw new H3cHostKeyMismatchException();
        }
        catch
        {
            Abort();
            throw;
        }
        finally
        {
            var ended = Stopwatch.GetTimestamp();
            var authenticationStarted = Interlocked.Read(ref _hostKeyAcceptedTimestamp);
            var connectionEnded = authenticationStarted > 0 ? authenticationStarted : ended;
            H3cCollectionMetrics.RecordConnectAuthentication(Stopwatch.GetElapsedTime(started, ended));
            H3cCollectionMetrics.RecordConnectionSetup(Stopwatch.GetElapsedTime(started, connectionEnded));
            if (authenticationStarted > 0)
                H3cCollectionMetrics.RecordAuthentication(Stopwatch.GetElapsedTime(authenticationStarted, ended));
        }
    }

    public async Task<string> ExecuteAsync(string command, CancellationToken cancellationToken)
    {
        if (!H3cCommandPolicy.IsAllowed(command) && !ZteCommandPolicy.IsAllowed(command))
            throw new ArgumentException("Only allowlisted vendor read-only commands are allowed.", nameof(command));
        if (!IsConnected) throw new InvalidOperationException("H3C SSH session is not connected.");

        using var sshCommand = _client.CreateCommand(command);
        // The persisted scheduler imposes the stricter per-run deadline (maximum ten minutes).
        sshCommand.CommandTimeout = TimeSpan.FromMinutes(11);
        var execution = sshCommand.ExecuteAsync(cancellationToken);
        H3cCapturedOutput captured;
        try
        {
            captured = await H3cCommandOutputCollector.CollectAsync(
                sshCommand.OutputStream, sshCommand.ExtendedOutputStream, execution,
                H3cRawOutput.MaximumBytes, Abort, cancellationToken, _terminalEncoding).ConfigureAwait(false);
        }
        catch (H3cOutputLimitException)
        {
            throw;
        }
        catch (H3cPartialOutputException)
        {
            throw;
        }

        var output = captured.Text;
        if (captured.StderrByteCount > 0)
        {
            if (output.Length > 0) throw new H3cPartialOutputException(output, "H3C_STDERR_PARTIAL", false);
            throw new H3cCommandRejectedException();
        }
        if (sshCommand.ExitStatus is not null and not 0 || sshCommand.ExitSignal is not null)
        {
            if (output.Length > 0) throw new H3cPartialOutputException(output, "H3C_COMMAND_EXIT_FAILED_PARTIAL", false);
            throw new H3cCommandRejectedException();
        }
        if (H3cComwareParser.ContainsPaginationPrompt(output))
            throw new H3cPartialOutputException(output, "H3C_PAGINATION_PARTIAL", false);
        return output;
    }

    public void Abort()
    {
        if (_disposed) return;
        _disposed = true;
        _client.Dispose();
    }

    public void Dispose() => Abort();

    private static bool IsAlgorithmNegotiationFailure(string? message)
    {
        var value = message ?? string.Empty;
        return value.Contains("algorithm", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("key exchange", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("cipher", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("mac", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("no matching", StringComparison.OrdinalIgnoreCase);
    }
}

internal static class H3cCommandOutputCollector
{
    public static async Task<H3cCapturedOutput> CollectAsync(Stream stdout, Stream stderr, Task execution,
        int maximumCombinedBytes, Action abortTransport, CancellationToken cancellationToken = default,
        string terminalEncoding = "AUTO")
    {
        if (maximumCombinedBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumCombinedBytes));
        cancellationToken.ThrowIfCancellationRequested();
        long totalBytes = 0;
        long stderrBytes = 0;
        using var captured = new MemoryStream(Math.Min(maximumCombinedBytes, 4096));
        var exceeded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drainFailed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var abortRequested = 0;
        void AbortOnce()
        {
            if (Interlocked.Exchange(ref abortRequested, 1) != 0) return;
            try { abortTransport(); }
            catch (Exception) { }
        }
        _ = exceeded.Task.ContinueWith(_ => AbortOnce(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            cancelled.TrySetResult();
            AbortOnce();
        });

        async Task DrainAsync(Stream source, bool capture)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(8192);
            try
            {
                while (true)
                {
                    var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false);
                    if (read == 0) return;
                    if (!capture) Interlocked.Add(ref stderrBytes, read);
                    var before = Interlocked.Add(ref totalBytes, read) - read;
                    var remaining = Math.Max(0, maximumCombinedBytes - before);
                    var accepted = (int)Math.Min(read, remaining);
                    if (capture && accepted > 0) captured.Write(buffer, 0, accepted);
                    if (before + read > maximumCombinedBytes) exceeded.TrySetResult();
                }
            }
            catch (Exception ex)
            {
                drainFailed.TrySetResult(ex);
                throw;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
        }

        var stdoutDrain = DrainAsync(stdout, capture: true);
        var stderrDrain = DrainAsync(stderr, capture: false);
        var drains = Task.WhenAll(stdoutDrain, stderrDrain);
        Exception? executionError = null;
        var winner = await Task.WhenAny(execution, exceeded.Task, drainFailed.Task, cancelled.Task).ConfigureAwait(false);
        var wasOverLimit = winner == exceeded.Task;
        var wasCancelled = winner == cancelled.Task;
        if (wasOverLimit || winner == drainFailed.Task || wasCancelled)
            AbortOnce();

        try { await execution.ConfigureAwait(false); }
        catch (Exception ex) { executionError = ex; }
        try { await drains.ConfigureAwait(false); }
        catch (Exception ex) { executionError ??= ex; }
        wasOverLimit |= exceeded.Task.IsCompleted;
        wasCancelled |= cancelled.Task.IsCompleted || cancellationToken.IsCancellationRequested;
        if (wasOverLimit) AbortOnce();

        string output;
        var allowIncompleteTail = wasOverLimit || wasCancelled || winner == drainFailed.Task || executionError is not null;
        try
        {
            output = TerminalTextEncoding.DecodePrefix(captured.GetBuffer(), checked((int)captured.Length),
                terminalEncoding, allowIncompleteTail);
        }
        catch (DecoderFallbackException) { throw new H3cInvalidEncodingException(); }

        if (wasOverLimit)
        {
            if (output.Length > 0) throw new H3cPartialOutputException(output, "H3C_OUTPUT_LIMIT_PARTIAL", false);
            throw new H3cOutputLimitException();
        }
        if (wasCancelled)
        {
            if (output.Length > 0) throw new H3cPartialOutputException(output, "H3C_CANCELLED_PARTIAL", true);
            throw new OperationCanceledException(cancellationToken);
        }
        if (winner == drainFailed.Task)
        {
            if (output.Length > 0) throw new H3cPartialOutputException(output, "H3C_STREAM_READ_FAILED_PARTIAL", false);
            throw new IOException("H3C SSH output stream could not be read.");
        }
        if (executionError is not null)
        {
            if (executionError is OperationCanceledException)
            {
                if (output.Length > 0) throw new H3cPartialOutputException(output, "H3C_CANCELLED_PARTIAL", true);
                throw executionError;
            }
            if (output.Length > 0) throw new H3cPartialOutputException(output, "H3C_DISCONNECTED_PARTIAL", false);
            throw executionError;
        }
        return new(output, Interlocked.Read(ref stderrBytes));
    }

}

internal sealed class H3cSshDeviceSessionFactory(Catalog catalog, IH3cCredentialProvider credentials,
    IH3cSshTransportFactory? transportFactory = null) : IDeviceSessionFactory
{
    private readonly IH3cSshTransportFactory _transportFactory = transportFactory ?? new SshNetH3cSshTransportFactory();

    public IDeviceSession Create(string target)
    {
        var profile = catalog.GetH3cDeviceProfile(target)
            ?? throw new KeyNotFoundException("H3C device profile was not found.");
        if (!profile.Enabled) throw new InvalidOperationException("H3C device profile is disabled.");
        return new H3cSshDeviceSession(profile, credentials, _transportFactory, catalog);
    }
}

internal sealed class H3cSshDeviceSession(H3cDeviceProfileSnapshot profile, IH3cCredentialProvider credentials,
    IH3cSshTransportFactory transportFactory, Catalog? catalog = null) : IDeviceSession
{
    private IH3cSshTransport? _transport;
    private bool _sessionCounted;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var password = credentials.GetPassword(profile.CredentialReference);
        if (string.IsNullOrEmpty(password)) throw new H3cCredentialUnavailableException();
        _transport = transportFactory.Create(profile, password);
        string? hostKeyCorrelation = null;
        try
        {
            await _transport.ConnectAsync((keyType, fingerprint) =>
            {
                if (catalog is null) return H3cHostKeyFingerprint.Matches(profile.HostKeySha256, fingerprint);
                var decision = catalog.CheckSshHostKey(profile, keyType, fingerprint);
                hostKeyCorrelation = decision.CorrelationId;
                return decision.Accepted;
            }, cancellationToken).ConfigureAwait(false);
            if (catalog is not null && hostKeyCorrelation is not null &&
                !catalog.CommitSshHostKey(hostKeyCorrelation))
                throw new H3cHostKeyMismatchException();
        }
        catch
        {
            if (catalog is not null && hostKeyCorrelation is not null)
            {
                try { catalog.FailSshHostKey(hostKeyCorrelation, "SSH_CONNECTION_FAILED_BEFORE_EFFECTIVE"); } catch { }
            }
            throw;
        }
        _sessionCounted = true;
        H3cCollectionMetrics.SessionOpened();
    }

    public async Task<string> ExecuteAsync(string command, CancellationToken cancellationToken)
    {
        if (!H3cCommandPolicy.IsAllowed(command))
            throw new ArgumentException("Only allowlisted H3C read-only commands are allowed.", nameof(command));
        var transport = _transport;
        if (transport is null || !transport.IsConnected)
            throw new InvalidOperationException("H3C SSH session is not connected.");
        cancellationToken.ThrowIfCancellationRequested();
        var started = Stopwatch.GetTimestamp();
        try { return await transport.ExecuteAsync(command, cancellationToken).ConfigureAwait(false); }
        finally { H3cCollectionMetrics.RecordCommand(Stopwatch.GetElapsedTime(started)); }
    }

    public ValueTask DisposeAsync()
    {
        try { _transport?.Dispose(); }
        finally
        {
            _transport = null;
            if (_sessionCounted)
            {
                _sessionCounted = false;
                H3cCollectionMetrics.SessionClosed();
            }
        }
        return ValueTask.CompletedTask;
    }
}

internal sealed record H3cRawOutputManifest(string RelativePath, string Command, long ByteCount,
    string OutputSha256, string SegmentSha256, string CaptureStatus, string ParseStatus,
    string ParsedJson, long CapturedMs);

internal interface ITaskResultSink
{
    Task<H3cRawOutputManifest> PersistAsync(string runId, TaskDefinitionSnapshot definition,
        string output, string captureStatus);
}

internal sealed class H3cTaskRawResultSink(Catalog catalog, Action<Exception>? storageFailure = null) : ITaskResultSink
{
    public async Task<H3cRawOutputManifest> PersistAsync(string runId, TaskDefinitionSnapshot definition,
        string output, string captureStatus)
    {
        if (definition.TaskType != "H3C_SSH_READONLY")
            throw new InvalidOperationException("Only H3C output can be written through the H3C Raw sink.");
        if (captureStatus is not ("COMPLETE" or "PARTIAL")) throw new ArgumentException("Capture status is invalid.", nameof(captureStatus));
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
                definition.Command, output, capturedMs, captureStatus);
            await writer.FlushAsync().ConfigureAwait(false);
            await writer.SealAsync().ConfigureAwait(false);
            H3cCollectionMetrics.RecordRawCommit(writer.LastSyncDurationMs, writer.LastSealDurationMs);
            var parse = captureStatus == "PARTIAL"
                ? new H3cParseResult("PARTIAL", "{}")
                : H3cComwareParser.Parse(definition.Command, output, completedExec: true);
            return new H3cRawOutputManifest(writer.RelativePath, definition.Command, outputByteCount,
                Convert.ToHexString(outputSha).ToLowerInvariant(), writer.Sha256!, captureStatus,
                parse.Status, parse.Json, capturedMs);
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
