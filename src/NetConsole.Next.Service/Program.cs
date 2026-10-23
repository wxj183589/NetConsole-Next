using Microsoft.Extensions.Hosting;
using NetConsole.Next.Service;
using NetConsole.Next.Credentials;
using System.Security.Cryptography;
using System.Text;
using System.Net.WebSockets;
using System.Text.Json;
using System.IO.Ports;

using var singleInstance = SingleInstanceGuard.Acquire();
var settings = RuntimeSettings.FromEnvironment();
var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(options => options.ServiceName = "NetConsoleNext");
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(45));
builder.WebHost.UseUrls($"http://127.0.0.1:{settings.WebPort}");
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<Catalog>();
builder.Services.AddSingleton<IRawSegmentWriterFactory, RawSegmentWriterFactory>();
IH3cCredentialProvider credentialProvider = builder.Environment.IsDevelopment() && settings.H3cDevelopmentEnvironmentCredentialsEnabled
    ? new EnvironmentH3cCredentialProvider(enabled: true)
    : OperatingSystem.IsWindows()
        ? new WindowsDpapiH3cCredentialProvider(settings.DataRoot)
        : new UnavailableH3cCredentialProvider();
builder.Services.AddSingleton<IH3cCredentialProvider>(credentialProvider);
builder.Services.AddSingleton<TerminalSessionManager>();
builder.Services.AddSingleton<TerminalTicketStore>();
builder.Services.AddSingleton<TerminalRecordingService>();
builder.Services.AddSingleton<TerminalExportService>();
builder.Services.AddSingleton<IDeviceSessionFactory, H3cSshDeviceSessionFactory>();
builder.Services.AddSingleton<ITaskResultSink>(sp => new H3cTaskRawResultSink(
    sp.GetRequiredService<Catalog>(), ex =>
    {
        sp.GetRequiredService<Catalog>().MarkUnavailable(ex);
        sp.GetRequiredService<RuntimeStatus>().SetFaulted($"H3C_RAW_PERSISTENCE_FAILED: {ex.GetType().Name}");
    }));
builder.Services.AddSingleton(sp => new BoundedCollectionScheduler(
    sp.GetRequiredService<IDeviceSessionFactory>(), maxConcurrency: 2, queueCapacity: 128,
    sp.GetRequiredService<Catalog>(), ex =>
    {
        sp.GetRequiredService<Catalog>().MarkUnavailable(ex);
        sp.GetRequiredService<RuntimeStatus>().SetFaulted($"TASK_STATE_PERSISTENCE_FAILED: {ex.GetType().Name}");
    }, sp.GetRequiredService<ITaskResultSink>()));
builder.Services.AddSingleton<IngressStats>();
builder.Services.AddSingleton<RuntimeStatus>();
builder.Services.AddSingleton<RuntimeResources>();
builder.Services.AddSingleton<FileLogDiagnostics>();
builder.Services.AddHostedService<ServiceRunLifecycle>();
builder.Services.AddHostedService<ProcessResourceMonitor>();
builder.Services.AddHostedService<UdpSyslogWorker>();
builder.Services.AddHostedService<RawIntegrityAuditService>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
app.Use(async (context, next) =>
{
    if (!(context.Request.Path.Value?.StartsWith("/api/system", StringComparison.OrdinalIgnoreCase) ?? false))
    {
        await next();
        return;
    }

    var remote = context.Connection.RemoteIpAddress;
    var requestHost = context.Request.Host;
    var validHost = (string.Equals(requestHost.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(requestHost.Host, "localhost", StringComparison.OrdinalIgnoreCase)) &&
                    requestHost.Port == settings.WebPort;
    if (remote is null || !System.Net.IPAddress.IsLoopback(remote) || !validHost)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    if (string.Equals(context.Request.Path.Value, "/api/system/health", StringComparison.OrdinalIgnoreCase))
    {
        await next();
        return;
    }

    var websocketStream = context.WebSockets.IsWebSocketRequest &&
        context.Request.Path.Value?.EndsWith("/stream", StringComparison.OrdinalIgnoreCase) == true;
    if (websocketStream)
    {
        await next();
        return;
    }

    string expectedToken;
    try { expectedToken = LocalApiTokenStore.ReadExisting(settings.DataRoot); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        return;
    }
    var values = context.Request.Headers["Authorization"];
    if (values.Count != 1 || !values[0]!.StartsWith("Bearer ", StringComparison.Ordinal))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers["WWW-Authenticate"] = "Bearer";
        return;
    }
    var presented = values[0]![7..];
    var expectedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(expectedToken));
    var presentedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
    if (!CryptographicOperations.FixedTimeEquals(expectedDigest, presentedDigest))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers["WWW-Authenticate"] = "Bearer";
        return;
    }
    await next();
});
var catalog = app.Services.GetRequiredService<Catalog>();
var runtimeStatus = app.Services.GetRequiredService<RuntimeStatus>();
app.Lifetime.ApplicationStopping.Register(() =>
{
    try { app.Services.GetRequiredService<TerminalSessionManager>().DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    catch { }
});
RollingFileLoggerProvider? fileLoggerProvider = null;
try
{
    catalog.Initialize();
    catalog.BeginRawRecovery();
    try { catalog.ProbeRawWritable(); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        runtimeStatus.SetFaulted("RAW_WRITE_UNAVAILABLE: new Raw data cannot be safely persisted.");
        app.Logger.LogCritical(ex, "Raw data directory is not writable; collection will not start.");
    }

    fileLoggerProvider = new RollingFileLoggerProvider(settings.DataRoot,
        app.Services.GetRequiredService<FileLogDiagnostics>());
    app.Services.GetRequiredService<ILoggerFactory>().AddProvider(fileLoggerProvider);
    app.Lifetime.ApplicationStopped.Register(fileLoggerProvider.Dispose);

    var recovery = catalog.RecoverRawSegments();
    runtimeStatus.AssessRecovery(recovery, catalog.GetUnresolvedIntegrityIssueCount());
    runtimeStatus.SetInterruptedTaskRuns(catalog.InterruptedTaskRuns);
    if (!recovery.Issues.Any(issue => issue.Critical)) catalog.CompleteRawRecovery();
    if (recovery.FailedFiles > 0)
        app.Logger.LogWarning("Raw recovery left {FailedFiles} file(s) untouched; inspect recovery.issues and raw_integrity_audits.", recovery.FailedFiles);
    app.Logger.LogInformation("Raw recovery scanned {ScannedFiles} file(s), skipped {TrustedFiles} trusted file(s), validated {BytesRead} bytes and hashed {BytesHashed} bytes, recovered {OpenFiles} open file(s), repaired {RepairedFiles} tail(s), found {MissingFiles} missing file(s), in {ElapsedMs} ms.",
        recovery.ScannedFiles, recovery.TrustedFilesSkipped, recovery.BytesRead, recovery.BytesHashed,
        recovery.RecoveredOpenFiles, recovery.RepairedTailFiles, recovery.MissingFiles, recovery.ElapsedMs);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
{
    catalog.MarkUnavailable(ex);
    runtimeStatus.SetFaulted($"STORAGE_STARTUP_FAILED: {ex.GetType().Name}");
    app.Logger.LogCritical(ex, "Isolated storage could not be initialized or reconciled; collection is disabled.");
}

var processStartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
app.MapGet("/api/system/health", (RuntimeSettings runtime, IngressStats ingress, Catalog rawCatalog,
    RuntimeStatus status, RuntimeResources resources, FileLogDiagnostics logDiagnostics) =>
{
    var resource = resources.Snapshot;
    CatalogHealthSnapshot? storage = null;
    if (rawCatalog.Available)
    {
        try { storage = rawCatalog.GetHealthSnapshot(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            status.SetFaulted($"SQLITE_UNAVAILABLE: {ex.GetType().Name}");
        }
    }
    var healthState = logDiagnostics.WriteFailures > 0 && status.HealthState == "HEALTHY" ? "DEGRADED" : status.HealthState;
    return Results.Ok(new
    {
        status = status.ServiceState,
        serviceState = status.ServiceState,
        healthState,
        healthError = status.HealthError ?? rawCatalog.StorageFailure,
        processId = Environment.ProcessId,
        startedAtMs = processStartedAt,
        syslogEnabled = runtime.SyslogPort > 0,
        h3cCollectionEnabled = runtime.H3cEnabled,
        collectionState = status.CollectionState,
        collectionError = status.CollectionError,
        h3cMetrics = H3cCollectionMetrics.Snapshot(),
        udpReceived = ingress.Received,
        udpAccepted = ingress.Accepted,
        udpDropped = ingress.Dropped,
        udpQueueDepth = ingress.QueueDepth,
        udpQueueHighWatermark = ingress.QueueHighWatermark,
        udpQueueCapacity = UdpSyslogWorker.QueueCapacity,
        udpQueueBytes = ingress.QueueBytes,
        udpQueueBytesHighWatermark = ingress.QueueBytesHighWatermark,
        udpQueueByteCapacity = UdpSyslogWorker.QueueByteCapacity,
        udpSocketReceiveBufferRequestedBytes = ingress.RequestedSocketReceiveBufferBytes,
        udpSocketReceiveBufferEffectiveBytes = ingress.EffectiveSocketReceiveBufferBytes,
        rawFlushedRecords = ingress.Flushed,
        rawWriteLatency = ingress.RawFlushLatency,
        rawFileSyncLatency = ingress.RawFlushLatency,
        rawSegmentSealLatency = ingress.SegmentSealLatency,
        sqliteSegmentRegistrationLatency = ingress.CatalogWriteLatency,
        lastReceivedMs = ingress.LastReceivedMs == 0 ? (long?)null : ingress.LastReceivedMs,
        lastPersistedMs = ingress.LastPersistedMs == 0 ? (long?)null : ingress.LastPersistedMs,
        openSegmentsOnDisk = rawCatalog.OpenSegmentCount(),
        rawSegments = storage?.RawSegments,
        sqliteIntegrityAtStartup = rawCatalog.IntegrityStatus,
        rawWriteAvailable = rawCatalog.RawWriteAvailable,
        logWriteFailures = logDiagnostics.WriteFailures,
        recovery = rawCatalog.RecoverySummary,
        interruptedTaskRunsAtStartup = rawCatalog.InterruptedTaskRuns,
        integrityAuditCount = storage?.IntegrityAuditCount,
        unresolvedIntegrityIssues = storage?.UnresolvedIntegrityIssues,
        recentIntegrityAudits = storage?.RecentIntegrityAudits,
        resources = resource
    });
});
app.MapGet("/api/system/tasks", (Catalog rawCatalog) => Results.Ok(rawCatalog.RecentTaskRuns()));
if (settings.TerminalEnabled)
{
    app.MapGet("/api/system/terminal/devices", (Catalog rawCatalog, TerminalSessionManager manager) =>
    {
        var sessions = manager.Recent();
        var devices = rawCatalog.RecentH3cDeviceProfiles(100).Select(profile =>
        {
            var latest = sessions.FirstOrDefault(session => session.TargetId == profile.Id);
            return new TerminalDeviceView(profile.Id, profile.Host, profile.Port, profile.Username, profile.Enabled,
                new[] { "AUTO", "SSH", "TELNET" }, profile.HostKeyUpdatePolicy, profile.AllowLegacyAlgorithms,
                profile.TerminalEncoding, profile.TerminalProtocol, profile.AllowTelnetFallback,
                latest?.State, latest?.StartedMs, latest?.DisconnectReason);
        });
        return Results.Ok(devices);
    });
    app.MapGet("/api/system/terminal/serial-ports", (RuntimeSettings runtime) =>
    {
        var ports = SerialPort.GetPortNames().AsEnumerable();
        if (runtime.TerminalDevelopmentSimulationEnabled) ports = ports.Append("SIMULATED-COM1");
        return Results.Ok(ports.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
    });
    app.MapGet("/api/system/terminal/sessions", (HttpRequest request, Catalog rawCatalog) =>
        Results.Ok(rawCatalog.QueryTerminalSessions(TerminalSessionQuery.FromQuery(request.Query))));
    app.MapGet("/api/system/terminal/sessions/{sessionId}", (string sessionId, Catalog rawCatalog) =>
    {
        var session = rawCatalog.GetTerminalSession(sessionId);
        return session is null ? Results.NotFound() : Results.Ok(session);
    });
    app.MapGet("/api/system/terminal/sessions/{sessionId}/events", (string sessionId, Catalog rawCatalog, int? limit) =>
        Results.Ok(rawCatalog.TerminalEvents(sessionId, Math.Clamp(limit ?? 500, 1, 5000))));
    app.MapGet("/api/system/terminal/sessions/{sessionId}/recording/segments", (string sessionId, Catalog rawCatalog) =>
        Results.Ok(rawCatalog.TerminalRecordingSegments(sessionId)));
    app.MapGet("/api/system/terminal/sessions/{sessionId}/recording/integrity", (string sessionId,
        TerminalRecordingService recordings) =>
    {
        try { return Results.Ok(recordings.Verify(sessionId)); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (InvalidDataException ex) { return Results.Conflict(new { error = ex.Message }); }
    });
    app.MapPost("/api/system/terminal/sessions/{sessionId}/recording/verify", (string sessionId,
        TerminalRecordingService recordings) =>
    {
        try { return Results.Ok(recordings.Verify(sessionId)); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (InvalidDataException ex) { return Results.Conflict(new { error = ex.Message }); }
    });
    app.MapGet("/api/system/terminal/sessions/{sessionId}/exports", (string sessionId, Catalog rawCatalog) =>
        Results.Ok(rawCatalog.TerminalExports(sessionId)));
    app.MapGet("/api/system/terminal/sessions/{sessionId}/export", async (HttpContext context, string sessionId,
        string? format, Catalog rawCatalog, TerminalRecordingService recordings, TerminalExportService exports) =>
    {
        var session = rawCatalog.GetTerminalSession(sessionId);
        if (session is null) return Results.NotFound();
        try
        {
            var integrity = recordings.Verify(sessionId);
            await exports.WriteAsync(context.Response, session, format ?? "TXT", integrity, context.RequestAborted);
            return Results.Empty;
        }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (InvalidDataException ex) { return Results.Conflict(new { error = ex.Message }); }
    });
    app.MapGet("/api/system/terminal/sessions/{sessionId}/recording/replay", (string sessionId,
        long? offset, int? limit, long? fromMs, long? toMs, TerminalRecordingService recordings) =>
    {
        try
        {
            var safeOffset = Math.Max(0, offset ?? 0);
            var safeLimit = Math.Clamp(limit ?? 500, 1, 2000);
            var page = recordings.ReadReplay(sessionId, safeOffset, safeLimit, fromMs, toMs);
            return Results.Ok(new
            {
                page.SessionId, page.Offset, page.Limit, page.HasMore,
                entries = page.Entries.Select(entry => new
                {
                    entry.Position, entry.SegmentNumber, entry.AtMs, entry.Direction,
                    dataB64 = Convert.ToBase64String(entry.Data)
                })
            });
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (InvalidDataException ex) { return Results.Conflict(new { error = ex.Message }); }
        catch (FileNotFoundException) { return Results.NotFound(); }
    });
    app.MapPost("/api/system/terminal/sessions", (TerminalSessionRequest request, TerminalSessionManager manager) =>
    {
        try
        {
            var sessionId = manager.Create(request);
            return Results.Created($"/api/system/terminal/sessions/{sessionId}",
                new { sessionId, state = "CONNECTING", mode = "RESTRICTED_READONLY" });
        }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        catch (IOException ex) { return Results.Conflict(new { error = ex.Message }); }
    });
    app.MapPost("/api/system/terminal/sessions/{sessionId}/ticket", (string sessionId,
        TerminalSessionManager manager, TerminalTicketStore tickets) =>
    {
        if (manager.Get(sessionId) is null) return Results.NotFound();
        return Results.Ok(tickets.Issue(sessionId));
    });
    app.MapPost("/api/system/terminal/sessions/{sessionId}/close", async (string sessionId,
        TerminalSessionManager manager) =>
    {
        if (!await manager.CloseAsync(sessionId)) return Results.NotFound();
        return Results.Ok(new { sessionId, state = "CLOSED" });
    });
    app.MapGet("/api/system/terminal/sessions/{sessionId}/stream", async (HttpContext context,
        string sessionId, TerminalSessionManager manager, TerminalTicketStore tickets) =>
    {
        var expectedOrigins = new[] { $"http://127.0.0.1:{settings.WebPort}", $"http://localhost:{settings.WebPort}" };
        var origin = context.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && !expectedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        var ticket = context.Request.Query["ticket"].ToString();
        if (string.IsNullOrWhiteSpace(ticket) || !tickets.TryConsume(ticket, sessionId))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        var session = manager.Get(sessionId);
        if (session is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var send = SendTerminalOutputAsync(socket, session, cancellation.Token);
        var receive = ReceiveTerminalInputAsync(socket, session, cancellation.Token);
        await Task.WhenAny(send, receive).ConfigureAwait(false);
        cancellation.Cancel();
        try { await Task.WhenAll(send, receive).ConfigureAwait(false); } catch { }
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None); } catch { }
        }
    });
}
if (settings.H3cEnabled)
{
    app.MapGet("/api/system/h3c/profiles", (Catalog rawCatalog) => Results.Ok(rawCatalog.RecentH3cDeviceProfiles()));
    app.MapPost("/api/system/h3c/profiles", (H3cProfileRequest request, Catalog rawCatalog) =>
    {
        try
        {
            rawCatalog.CreateH3cDeviceProfile(new(request.Id, request.Host, request.Port, request.Username,
                request.CredentialReference, request.HostKeySha256, request.Enabled, 0, 0,
                request.HostKeyUpdatePolicy, request.AllowLegacyAlgorithms, request.TerminalEncoding,
                request.TerminalProtocol, request.AllowTelnetFallback));
            return Results.Created($"/api/system/h3c/profiles/{request.Id}", rawCatalog.GetH3cDeviceProfile(request.Id));
        }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            return Results.Conflict(new { error = $"Profile could not be saved ({ex.GetType().Name})." });
        }
    });
    app.MapPut("/api/system/h3c/profiles/{id}/compatibility", (string id, H3cCompatibilityRequest request,
        Catalog rawCatalog) =>
    {
        try
        {
            rawCatalog.SetH3cCompatibility(id, request.HostKeyUpdatePolicy, request.AllowLegacyAlgorithms,
                request.TerminalEncoding, request.TerminalProtocol, request.AllowTelnetFallback);
            return Results.Ok(rawCatalog.GetH3cDeviceProfile(id));
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    });
    app.MapGet("/api/system/h3c/profiles/{id}/host-key-history", (string id, Catalog rawCatalog, int? limit) =>
        Results.Ok(rawCatalog.SshHostKeyHistory(id, Math.Clamp(limit ?? 100, 1, 500))));
    app.MapPost("/api/system/h3c/task-definitions", (H3cTaskDefinitionRequest request, Catalog rawCatalog) =>
    {
        if (request.TimeoutMs is < 100 or > 600000)
            return Results.BadRequest(new { error = "timeoutMs must be between 100 and 600000." });
        try
        {
            rawCatalog.CreateTaskDefinition(request.Id, "H3C_SSH_READONLY", request.ProfileId,
                request.Command, TimeSpan.FromMilliseconds(request.TimeoutMs), request.Enabled);
            return Results.Created($"/api/system/tasks/definitions/{request.Id}",
                rawCatalog.GetTaskDefinition(request.Id));
        }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            return Results.Conflict(new { error = $"Task definition could not be saved ({ex.GetType().Name})." });
        }
    });
    app.MapPost("/api/system/tasks/{definitionId}/runs", (string definitionId, Catalog rawCatalog,
        RuntimeStatus status, BoundedCollectionScheduler scheduler) =>
    {
        if (!rawCatalog.Available || !rawCatalog.RawWriteAvailable || !status.CollectionAllowed)
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        try
        {
            if (rawCatalog.GetTaskDefinition(definitionId)?.TaskType != "H3C_SSH_READONLY")
                return Results.NotFound();
            if (!scheduler.TryScheduleDefinition(definitionId, out var runId))
                return Results.Problem("The bounded collection queue is full or shutting down.", statusCode: StatusCodes.Status429TooManyRequests);
            return Results.Accepted("/api/system/tasks", new { runId });
        }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    });
}
app.MapPost("/api/system/raw-integrity-audits/{auditId}/quarantine",
    (string auditId, RawIssueQuarantineRequest request, Catalog rawCatalog, RuntimeStatus status) =>
    {
        try
        {
            rawCatalog.QuarantineIntegrityAudit(auditId, "local-api-token", request.Detail);
            var unresolved = rawCatalog.GetUnresolvedIntegrityIssueCount();
            status.ReassessIntegrityIssues(unresolved);
            return Results.Ok(new { auditId, disposition = "QUARANTINED", unresolvedIntegrityIssues = unresolved,
                note = "Catalog quarantine is recorded; Raw and tail evidence remain on disk." });
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    });

app.Run();

static async Task SendTerminalOutputAsync(WebSocket socket, TerminalSessionRuntime session, CancellationToken cancellationToken)
{
    var status = JsonSerializer.SerializeToUtf8Bytes(new { type = "status", session = session.Snapshot });
    await socket.SendAsync(status, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    await foreach (var output in session.Output.ReadAllAsync(cancellationToken))
        await socket.SendAsync(output, WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
}

static async Task ReceiveTerminalInputAsync(WebSocket socket, TerminalSessionRuntime session, CancellationToken cancellationToken)
{
    var buffer = new byte[64 * 1024];
    while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
    {
        var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (result.MessageType == WebSocketMessageType.Close) return;
        if (result.MessageType != WebSocketMessageType.Text || !result.EndOfMessage || result.Count > buffer.Length)
        {
            await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "structured text messages only", cancellationToken).ConfigureAwait(false);
            return;
        }
        try
        {
            using var document = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
            var root = document.RootElement;
            var type = root.GetProperty("type").GetString();
            switch (type)
            {
                case "input":
                    await session.SendCommandAsync(root.GetProperty("text").GetString() ?? string.Empty, cancellationToken).ConfigureAwait(false);
                    break;
                case "control":
                    await session.SendControlAsync(root.GetProperty("signal").GetString() ?? string.Empty, cancellationToken).ConfigureAwait(false);
                    break;
                case "resize":
                    await session.ResizeAsync(root.GetProperty("columns").GetInt32(), root.GetProperty("rows").GetInt32(), cancellationToken).ConfigureAwait(false);
                    break;
                case "ping":
                    break;
                default:
                    throw new InvalidOperationException("Unsupported terminal message type.");
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            try
            {
                var error = JsonSerializer.SerializeToUtf8Bytes(new { type = "error", error = ex.Message });
                await socket.SendAsync(error, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            }
            catch { return; }
        }
    }
}

internal sealed record RawIssueQuarantineRequest(string Detail);
internal sealed record H3cProfileRequest(string Id, string Host, int Port, string Username,
    string CredentialReference, string HostKeySha256, bool Enabled = true,
    string HostKeyUpdatePolicy = "STRICT", bool AllowLegacyAlgorithms = false, string TerminalEncoding = "AUTO",
    string TerminalProtocol = "AUTO", bool AllowTelnetFallback = false);
internal sealed record H3cCompatibilityRequest(string HostKeyUpdatePolicy = "STRICT", bool AllowLegacyAlgorithms = false,
    string TerminalEncoding = "AUTO", string TerminalProtocol = "AUTO", bool AllowTelnetFallback = false);
internal sealed record H3cTaskDefinitionRequest(string Id, string ProfileId, string Command,
    long TimeoutMs = 30000, bool Enabled = true);
internal sealed record TerminalDeviceView(string Id, string Host, int Port, string Username, bool Enabled,
    IReadOnlyList<string> SupportedProtocols, string HostKeyUpdatePolicy, bool AllowLegacyAlgorithms,
    string TerminalEncoding, string TerminalProtocol, bool AllowTelnetFallback,
    string? LastSessionState, long? LastSessionStartedMs, string? LastDisconnectReason);
