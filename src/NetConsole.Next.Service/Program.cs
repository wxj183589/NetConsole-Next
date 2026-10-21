using Microsoft.Extensions.Hosting;
using NetConsole.Next.Service;
using NetConsole.Next.Credentials;
using System.Security.Cryptography;
using System.Text;

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
app.MapGet("/", () => Results.Text("NetConsole Next Core service"));
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
if (settings.H3cEnabled)
{
    app.MapGet("/api/system/h3c/profiles", (Catalog rawCatalog) => Results.Ok(rawCatalog.RecentH3cDeviceProfiles()));
    app.MapPost("/api/system/h3c/profiles", (H3cProfileRequest request, Catalog rawCatalog) =>
    {
        try
        {
            rawCatalog.CreateH3cDeviceProfile(new(request.Id, request.Host, request.Port, request.Username,
                request.CredentialReference, request.HostKeySha256, request.Enabled, 0, 0));
            return Results.Created($"/api/system/h3c/profiles/{request.Id}", rawCatalog.GetH3cDeviceProfile(request.Id));
        }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            return Results.Conflict(new { error = $"Profile could not be saved ({ex.GetType().Name})." });
        }
    });
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

internal sealed record RawIssueQuarantineRequest(string Detail);
internal sealed record H3cProfileRequest(string Id, string Host, int Port, string Username,
    string CredentialReference, string HostKeySha256, bool Enabled = true);
internal sealed record H3cTaskDefinitionRequest(string Id, string ProfileId, string Command,
    long TimeoutMs = 30000, bool Enabled = true);
