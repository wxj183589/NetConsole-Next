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
builder.Services.AddSingleton<IngressStats>();
builder.Services.AddSingleton<RuntimeStatus>();
builder.Services.AddSingleton<RuntimeResources>();
builder.Services.AddHostedService<ServiceRunLifecycle>();
builder.Services.AddHostedService<ProcessResourceMonitor>();
builder.Services.AddHostedService<UdpSyslogWorker>();

var app = builder.Build();
var catalog = app.Services.GetRequiredService<Catalog>();
catalog.Initialize();
var recovery = catalog.RecoverRawSegments();
if (recovery.FailedFiles > 0)
    app.Logger.LogWarning("Raw recovery left {FailedFiles} file(s) untouched because they could not be inspected.", recovery.FailedFiles);
app.Logger.LogInformation("Raw recovery scanned {ScannedFiles} file(s), recovered {OpenFiles} open file(s), preserved {EvidenceFiles} recovery evidence file(s) ({EvidenceBytes} bytes), repaired {RepairedFiles} tail(s), and found {MissingFiles} missing catalog file(s).",
    recovery.ScannedFiles, recovery.RecoveredOpenFiles, recovery.RecoveryEvidenceFiles, recovery.RecoveryEvidenceBytes,
    recovery.RepairedTailFiles, recovery.MissingFiles);

var processStartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
app.MapGet("/", () => Results.Text("NetConsole Next Core service"));
app.MapGet("/api/system/health", (RuntimeSettings runtime, IngressStats ingress, Catalog rawCatalog,
    RuntimeStatus status, RuntimeResources resources) =>
{
    var resource = resources.Snapshot;
    var segments = rawCatalog.GetRawCounts();
    return Results.Ok(new
    {
        status = status.ServiceState,
        serviceState = status.ServiceState,
        processId = Environment.ProcessId,
        startedAtMs = processStartedAt,
        syslogEnabled = runtime.SyslogPort > 0,
        collectionState = status.CollectionState,
        collectionError = status.CollectionError,
        udpReceived = ingress.Received,
        udpAccepted = ingress.Accepted,
        udpDropped = ingress.Dropped,
        udpQueueDepth = ingress.QueueDepth,
        udpQueueCapacity = UdpSyslogWorker.QueueCapacity,
        rawFlushedRecords = ingress.Flushed,
        lastReceivedMs = ingress.LastReceivedMs == 0 ? (long?)null : ingress.LastReceivedMs,
        lastPersistedMs = ingress.LastPersistedMs == 0 ? (long?)null : ingress.LastPersistedMs,
        openSegmentsOnDisk = rawCatalog.OpenSegmentCount(),
        rawSegments = segments,
        sqliteIntegrityAtStartup = rawCatalog.IntegrityStatus,
        recovery = rawCatalog.RecoverySummary,
        resources = resource
    });
});
app.MapGet("/api/system/tasks", (Catalog rawCatalog) => Results.Ok(rawCatalog.RecentTaskRuns()));

app.Run();
