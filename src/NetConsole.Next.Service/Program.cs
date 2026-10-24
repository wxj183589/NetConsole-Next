using NetConsole.Next.Service;

var settings = RuntimeSettings.FromEnvironment();
var builder = WebApplication.CreateBuilder(args);
// A read-only P0 local health endpoint. Remote administration needs auth/TLS first.
builder.WebHost.UseUrls($"http://127.0.0.1:{settings.WebPort}");
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<Catalog>();
builder.Services.AddSingleton<IngressStats>();
builder.Services.AddHostedService<UdpSyslogWorker>();

var app = builder.Build();
var db = app.Services.GetRequiredService<Catalog>();
db.Initialize();

app.MapGet("/", () => Results.Text("NetConsole Next P0 Core prototype"));
app.MapGet("/api/system/health", (RuntimeSettings runtime, IngressStats ingress, Catalog catalog) =>
    Results.Ok(new
    {
        status = "poc-running",
        version = "p0",
        syslogEnabled = runtime.SyslogPort > 0,
        udpReceived = ingress.Received,
        udpAccepted = ingress.Accepted,
        udpDropped = ingress.Dropped,
        rawFlushedRecords = ingress.Flushed,
        openSegmentsOnDisk = catalog.OpenSegmentCount()
    }));

app.Run();
