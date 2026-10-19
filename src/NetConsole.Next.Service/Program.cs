using Microsoft.Extensions.Hosting;
using NetConsole.Next.Service;

using var singleInstance = SingleInstanceGuard.Acquire();
var settings = RuntimeSettings.FromEnvironment();
var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(options => options.ServiceName = "NetConsoleNext");
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(45));
builder.WebHost.UseUrls($"http://127.0.0.1:{settings.WebPort}");
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<Catalog>();
builder.Services.AddSingleton<IRawSegmentWriterFactory, RawSegmentWriterFactory>();
builder.Services.AddSingleton<IngressStats>();
builder.Services.AddSingleton<RuntimeStatus>();
builder.Services.AddSingleton<RuntimeResources>();
builder.Services.AddSingleton<FileLogDiagnostics>();
builder.Services.AddHostedService<ServiceRunLifecycle>();
builder.Services.AddHostedService<ProcessResourceMonitor>();
builder.Services.AddHostedService<UdpSyslogWorker>();
builder.Services.AddHostedService<RawIntegrityAuditService>();

var app = builder.Build();
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
    runtimeStatus.AssessRecovery(recovery);
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
        collectionState = status.CollectionState,
        collectionError = status.CollectionError,
        udpReceived = ingress.Received,
        udpAccepted = ingress.Accepted,
        udpDropped = ingress.Dropped,
        udpQueueDepth = ingress.QueueDepth,
        udpQueueHighWatermark = ingress.QueueHighWatermark,
        udpQueueCapacity = UdpSyslogWorker.QueueCapacity,
        rawFlushedRecords = ingress.Flushed,
        rawWriteLatency = ingress.RawFlushLatency,
        lastReceivedMs = ingress.LastReceivedMs == 0 ? (long?)null : ingress.LastReceivedMs,
        lastPersistedMs = ingress.LastPersistedMs == 0 ? (long?)null : ingress.LastPersistedMs,
        openSegmentsOnDisk = rawCatalog.OpenSegmentCount(),
        rawSegments = storage?.RawSegments,
        sqliteIntegrityAtStartup = rawCatalog.IntegrityStatus,
        rawWriteAvailable = rawCatalog.RawWriteAvailable,
        logWriteFailures = logDiagnostics.WriteFailures,
        recovery = rawCatalog.RecoverySummary,
        integrityAuditCount = storage?.IntegrityAuditCount,
        recentIntegrityAudits = storage?.RecentIntegrityAudits,
        resources = resource
    });
});
app.MapGet("/api/system/tasks", (Catalog rawCatalog) => Results.Ok(rawCatalog.RecentTaskRuns()));

app.Run();
