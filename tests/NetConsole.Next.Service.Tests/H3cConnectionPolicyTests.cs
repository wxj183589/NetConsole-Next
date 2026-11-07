using System.Net;
using NetConsole.Next.Service;

namespace NetConsole.Next.Service.Tests;

internal static class H3cConnectionPolicyTests
{
    private const string Key = "SHA256:ERERERERERERERERERERERERERERERERERERERERERE";

    public static async Task RunAsync()
    {
        await AutoUsesOneAlternateBeforeCommands();
        await RefusesFallbackForAuthenticationAndIdentityFailures();
        await HonorsExplicitProtocolAndDisabledFallback();
        await StopsAfterTheAlternateFails();
        await CancellationPreventsAnotherAttempt();
        await ConnectionProbeReportsTheActualProtocol();
        await ConnectionProbeHonorsFrozenBoundary();
        await RevocationStopsAlternateAndReconnect();
        Console.WriteLine("H3C_CONNECTION_POLICY_TESTS=PASS (8 groups)");
    }

    private static H3cDeviceProfileSnapshot Profile() => new("a1111111111111111111111111111111", "192.0.2.77", 2323,
        "reader", "SYNTHETIC", Key, true, 0, 0, TerminalProtocol: "AUTO", AllowTelnetFallback: true);

    private static async Task AutoUsesOneAlternateBeforeCommands()
    {
        foreach (var error in new Exception[] { new H3cProtocolNegotiationException(), new H3cConnectionTimeoutException() })
        {
            var transports = new Factory((attempt, _) => attempt == 0 ? error : null);
            var credentials = new Credentials();
            await using (var session = new H3cSshDeviceSession(Profile(), credentials, transports))
            {
                await session.ConnectAsync(CancellationToken.None);
                Assert(session.Protocol == "TELNET", "AUTO must expose the effective Telnet protocol");
                Assert(transports.Protocols.SequenceEqual(new[] { "SSH", "TELNET" }), "AUTO must make exactly two ordered attempts");
                Assert(transports.Instances[0].DisposeCount == 1 && !transports.Instances[0].IsConnected,
                    "failed SSH must close before Telnet starts");
                Assert(transports.Instances.Sum(item => item.CommandCount) == 0, "retry must happen before business commands");
                await session.ExecuteAsync(H3cCommandPolicy.DisplayVersion, CancellationToken.None);
                Assert(transports.Instances[1].CommandCount == 1, "the command must run once on the selected transport");
                Assert(credentials.ReadCount == 1, "both attempts must use the same profile-bound credential read");
            }
            Assert(transports.Instances.All(item => item.DisposeCount == 1), "every attempt must be disposed exactly once");
        }
    }

    private static async Task RefusesFallbackForAuthenticationAndIdentityFailures()
    {
        foreach (var error in new Exception[] { new H3cAuthenticationFailedException(), new H3cHostKeyMismatchException(),
            new H3cHostKeyMissingException(), new H3cJumpHostKeyMismatchException(), new H3cJumpHostKeyMissingException(),
            new H3cJumpHostConnectionException("JUMP_HOST_UNREACHABLE", "synthetic"), new IOException("connection closed"),
            new ArgumentException("unsupported driver"), new OperationCanceledException() })
        {
            var factory = new Factory((_, _) => error);
            await using (var session = new H3cSshDeviceSession(Profile(), new Credentials(), factory))
                await ThrowsSame(error, () => session.ConnectAsync(CancellationToken.None));
            Assert(factory.Protocols.SequenceEqual(new[] { "SSH" }), "authentication, identity, Jump and cancellation failures must not fallback");
            Assert(factory.Instances[0].DisposeCount == 1, "failed transport must be released once");
        }
    }

    private static async Task HonorsExplicitProtocolAndDisabledFallback()
    {
        foreach (var profile in new[] { Profile() with { AllowTelnetFallback = false }, Profile() with { TerminalProtocol = "SSH" } })
        {
            var error = new H3cConnectionTimeoutException();
            var factory = new Factory((_, _) => error);
            await using var session = new H3cSshDeviceSession(profile, new Credentials(), factory);
            await ThrowsSame(error, () => session.ConnectAsync(CancellationToken.None));
            Assert(factory.Instances.Count == 1, "explicit SSH or disabled fallback must make one attempt");
        }
        var telnet = new Factory((_, _) => null);
        await using var direct = new H3cSshDeviceSession(Profile() with { TerminalProtocol = "TELNET" }, new Credentials(), telnet);
        await direct.ConnectAsync(CancellationToken.None);
        Assert(direct.Protocol == "TELNET" && telnet.Protocols.SequenceEqual(new[] { "TELNET" }),
            "explicit Telnet must connect directly without SSH or host-key validation");
    }

    private static async Task StopsAfterTheAlternateFails()
    {
        var alternateFailure = new H3cConnectionTimeoutException();
        var factory = new Factory((attempt, _) => attempt == 0 ? new H3cProtocolNegotiationException() : alternateFailure);
        await using (var session = new H3cSshDeviceSession(Profile(), new Credentials(), factory))
            await ThrowsSame(alternateFailure, () => session.ConnectAsync(CancellationToken.None));
        Assert(factory.Instances.Count == 2 && factory.Instances.All(item => item.DisposeCount == 1),
            "failed Telnet must terminate the bounded retry and release both transports");
    }

    private static async Task CancellationPreventsAnotherAttempt()
    {
        using var cancellation = new CancellationTokenSource();
        var failure = new H3cConnectionTimeoutException();
        var factory = new Factory((_, _) => { cancellation.Cancel(); return failure; });
        await using var session = new H3cSshDeviceSession(Profile(), new Credentials(), factory);
        var cancelled = false;
        try { await session.ConnectAsync(cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Assert(cancelled && factory.Instances.Count == 1, "a cancelled caller must never start Telnet");
    }

    private static async Task ConnectionProbeReportsTheActualProtocol()
    {
        await WithCatalog(async catalog =>
        {
            var factory = new Factory((attempt, _) => attempt == 0 ? new H3cConnectionTimeoutException() : null);
            var service = new H3cConnectionTestService(catalog, new H3cSshDeviceSessionFactory(catalog, new Credentials(), factory));
            var result = await service.TestAsync(Profile().Id, CancellationToken.None);
            Assert(result is { Protocol: "TELNET", Status: "DEVICE_INFO_UNRECOGNIZED" },
                "connection test must report successful AUTO fallback as Telnet");
            Assert(factory.Instances.Count == 2 && factory.Instances.All(item => item.CommandCount == 0 && item.DisposeCount == 1),
                "Telnet probe must share collection retry and disposal without a second login or device-info command");

            var failed = new Factory((attempt, _) => attempt == 0 ? new H3cProtocolNegotiationException() : new H3cConnectionTimeoutException());
            result = await new H3cConnectionTestService(catalog, new H3cSshDeviceSessionFactory(catalog, new Credentials(), failed))
                .TestAsync(Profile().Id, CancellationToken.None);
            Assert(result is { Protocol: "TELNET", Status: "CONNECT_TIMEOUT" } && failed.Instances.Count == 2,
                "connection test must retain the final protocol and must not repeat a failed fallback");
            Assert(catalog.SshHostKeyHistory(Profile().Id).Count == 0, "failed SSH and successful Telnet must not commit an SSH fingerprint");
        });
    }

    private static async Task ThrowsSame(Exception expected, Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception actual) when (ReferenceEquals(expected, actual)) { return; }
        throw new InvalidOperationException("The original attempt failure must be preserved.");
    }

    private static async Task ConnectionProbeHonorsFrozenBoundary()
    {
        await WithCatalog(async catalog =>
        {
            var revoked = false;
            var rejection = new TaskScopeRevokedException("SYNTHETIC_SCOPE_REVOKED");
            void Boundary() { if (revoked) throw rejection; }
            var created = 0;
            var session = new ProbeSession();
            var factory = new SessionFactory(() => { created++; return session; });
            var service = new H3cConnectionTestService(catalog, factory);
            revoked = true;
            await ThrowsSame(rejection, () => service.TestAsync(Profile().Id, CancellationToken.None, Boundary));
            Assert(created == 0, "revoked recovery must not create a session");

            revoked = false;
            factory = new SessionFactory(() => { revoked = true; return session; });
            await ThrowsSame(rejection, () => new H3cConnectionTestService(catalog, factory)
                .TestAsync(Profile().Id, CancellationToken.None, Boundary));
            Assert(session.ConnectCount == 0 && session.Disposed, "a target rebound in the factory must be disposed without connecting");

            foreach (var phase in new[] { "CONNECT", "COMMAND", "COMMAND_FAILURE", "COMMAND_TIMEOUT" })
            {
                revoked = false;
                session = new ProbeSession
                {
                    Connected = () => { if (phase == "CONNECT") revoked = true; },
                    Executing = () =>
                    {
                        revoked = true;
                        if (phase == "COMMAND_FAILURE") throw new IOException("synthetic");
                        if (phase == "COMMAND_TIMEOUT") throw new OperationCanceledException();
                    }
                };
                await ThrowsSame(rejection, () => new H3cConnectionTestService(catalog, new SessionFactory(() => session))
                    .TestAsync(Profile().Id, CancellationToken.None, Boundary));
                Assert(session.Disposed && session.CommandCount == (phase == "CONNECT" ? 0 : 1),
                    "revocation must stop commands after connect and prevent stale or degraded success results");
            }
        });
    }

    private static async Task RevocationStopsAlternateAndReconnect()
    {
        await WithCatalog(async catalog =>
        {
            foreach (var phase in new[] { "ALTERNATE", "CONNECT_RETRY", "COMMAND_RETRY" })
            {
                var revoked = false;
                var rejection = new TaskScopeRevokedException("SYNTHETIC_SCOPE_REVOKED");
                void Boundary() { if (revoked) throw rejection; }
                var physicalCreates = 0;
                var physical = new ProbeTransport(
                    () =>
                    {
                        if (phase == "COMMAND_RETRY") return;
                        revoked = true;
                        if (phase == "ALTERNATE") throw new H3cProtocolNegotiationException();
                        throw new H3cConnectionLostException();
                    },
                    () => { revoked = true; throw new H3cConnectionLostException(); });
                IH3cSshTransport CreatePhysical() { physicalCreates++; return physical; }
                var transports = new DelegateTransportFactory(() => phase == "ALTERNATE" ? CreatePhysical() :
                    new NetmikoRetryTransport(CreatePhysical, H3cCommandPolicy.IsAllowed, (_, _) => Task.CompletedTask));
                var service = new H3cConnectionTestService(catalog,
                    new H3cSshDeviceSessionFactory(catalog, new Credentials(), transports));
                await ThrowsSame(rejection, () => service.TestAsync(Profile().Id, CancellationToken.None, Boundary));
                Assert(physicalCreates == 1 && transports.CreateCount == 1 && physical.Disposed,
                    "revocation must prevent alternate/reconnect transport creation and release the original transport");
                Assert(physical.CommandCount == (phase == "COMMAND_RETRY" ? 1 : 0),
                    "a lost device-info channel must not replay after authorization revocation");
            }
        });
    }

    private static async Task WithCatalog(Func<Catalog, Task> test)
    {
        var workspace = Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_WORKSPACE_ROOT") ??
            (OperatingSystem.IsWindows() ? @"D:\study\NetConsole-Next-Workspace" : Path.Combine(Path.GetTempPath(), "netconsole-next-workspace"));
        var root = Path.Combine(workspace, "test-data", "connection-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var catalog = new Catalog(new RuntimeSettings { DataRoot = root, WebPort = 18771, SyslogPort = 0,
                SyslogBind = IPAddress.Loopback, SyslogReceiveBufferBytes = 65536 });
            catalog.Initialize();
            catalog.CreateH3cDeviceProfile(Profile());
            await test(catalog);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private sealed class SessionFactory(Func<IDeviceSession> create) : IDeviceSessionFactory
    {
        public IDeviceSession Create(string target) => create();
    }

    private sealed class ProbeSession : IDeviceSession
    {
        public Action? Connected { get; init; }
        public Action? Executing { get; init; }
        public int ConnectCount { get; private set; }
        public int CommandCount { get; private set; }
        public bool Disposed { get; private set; }
        public Task ConnectAsync(CancellationToken cancellationToken) { ConnectCount++; Connected?.Invoke(); return Task.CompletedTask; }
        public Task<string> ExecuteAsync(string command, CancellationToken cancellationToken)
        {
            CommandCount++;
            Executing?.Invoke();
            return Task.FromResult("H3C Comware Software, Version 7.1.070, Release 1234\n");
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class DelegateTransportFactory(Func<IH3cSshTransport> create) : IH3cSshTransportFactory
    {
        public int CreateCount { get; private set; }
        public IH3cSshTransport Create(H3cDeviceProfileSnapshot profile, string password) { CreateCount++; return create(); }
    }

    private sealed class ProbeTransport(Action connecting, Action executing) : IH3cSshTransport
    {
        public bool IsConnected { get; private set; }
        public bool Disposed { get; private set; }
        public int CommandCount { get; private set; }
        public Task ConnectAsync(Func<string?, string?, bool> verifyHostKey, CancellationToken cancellationToken)
        {
            connecting();
            Assert(verifyHostKey("ssh-ed25519", Key), "synthetic pinned key must be accepted");
            IsConnected = true;
            return Task.CompletedTask;
        }
        public Task<string> ExecuteAsync(string command, CancellationToken cancellationToken)
        {
            CommandCount++;
            executing();
            return Task.FromResult(string.Empty);
        }
        public void Abort() => IsConnected = false;
        public void Dispose() { IsConnected = false; Disposed = true; }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Credentials : IH3cCredentialProvider
    {
        public int ReadCount { get; private set; }
        public string? GetPassword(string reference) => throw new InvalidOperationException("Profile binding is required.");
        public string? GetPassword(H3cDeviceProfileSnapshot profile) { ReadCount++; return "synthetic"; }
    }

    private sealed class Factory(Func<int, H3cDeviceProfileSnapshot, Exception?> failure) : IH3cSshTransportFactory
    {
        public List<string> Protocols { get; } = [];
        public List<Transport> Instances { get; } = [];
        public IH3cSshTransport Create(H3cDeviceProfileSnapshot profile, string password)
        {
            Assert(password == "synthetic", "transport must receive the stored synthetic credential");
            var error = failure(Instances.Count, profile);
            Protocols.Add(profile.TerminalProtocol);
            var transport = new Transport(profile.TerminalProtocol, error);
            Instances.Add(transport);
            return transport;
        }
    }

    private sealed class Transport(string protocol, Exception? failure) : IH3cSshTransport
    {
        public bool IsConnected { get; private set; }
        public int DisposeCount { get; private set; }
        public int CommandCount { get; private set; }
        public Task ConnectAsync(Func<string?, string?, bool> verifyHostKey, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (failure is not null) return Task.FromException(failure);
            if (protocol == "SSH" && !verifyHostKey("ssh-ed25519", Key)) throw new H3cHostKeyMismatchException();
            IsConnected = true;
            return Task.CompletedTask;
        }
        public Task<string> ExecuteAsync(string command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CommandCount++;
            return Task.FromResult("H3C Comware Software, Version 7.1.070, Release 1234\n");
        }
        public void Abort() => IsConnected = false;
        public void Dispose() { DisposeCount++; IsConnected = false; }
    }
}
