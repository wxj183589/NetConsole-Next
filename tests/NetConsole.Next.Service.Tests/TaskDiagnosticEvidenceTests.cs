using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NetConsole.Next.Service;

internal static class TaskDiagnosticEvidenceTests
{
    private static int _checks;
    public static async Task RunAsync()
    {
        _checks = 0;
        await LegacySinglePreservesVerifiedEvidence();
        await MultiCommandZipExcludesConfiguration();
        await PartialAndMissingRemainExplicit();
        await TamperedEvidenceFailsClosed();
        await CurrentScopeAndTargetCannotDrift();
        await CurrentSessionCannotSurviveRevocation();
        await DownloadRechecksEachChunkAndCancels();
        await RedactionRemainsBoundedAndConcurrentReadsStayIndependent();
        await FullDiagnosticKeepsItsOwnBoundAcrossSealingRecoveryAndDownload();
        Console.WriteLine($"TASK_DIAGNOSTIC_EVIDENCE_TESTS={_checks}/{_checks} PASS");
    }

    private static async Task FullDiagnosticKeepsItsOwnBoundAcrossSealingRecoveryAndDownload()
    {
        using var f = new Fixture();
        const string command = H3cDiagnosticCapture.Command;
        var sensitive = "<fixture>" + command + "\n" + new string('x', 300 * 1024) +
            "\npassword simple synthetic-diagnostic-password\n-----BEGIN PRIVATE KEY-----\nsynthetic-key\n-----END PRIVATE KEY-----\n<fixture>";
        var redacted = await f.Seal([command], [sensitive]);
        var text = Encoding.UTF8.GetString(f.Download(redacted.Id, 0).Bytes);
        using (var raw = JsonDocument.Parse(File.ReadAllText(f.Path(redacted.Raw[0]))))
        {
            var stored = Encoding.UTF8.GetString(raw.RootElement.GetProperty("output_utf8_base64").GetBytesFromBase64());
            Assert(stored == text && !stored.Contains("synthetic-diagnostic-password") && !stored.Contains("synthetic-key") &&
                stored.Contains("<REDACTED>"), "full diagnostic is sanitized before Raw sealing and remains downloadable above 256 KiB");
        }
        using (var zip = new ZipArchive(new MemoryStream(f.Download(redacted.Id).Bytes)))
            Assert(zip.Entries.Select(item => item.FullName).SequenceEqual(new[] { "manifest.json", "command-001.txt" }),
                "full diagnostic reuses the existing immutable manifest/TXT/ZIP evidence contract");
        Assert(f.Catalog.PublishH3cTaskFacts(redacted.Id).Status == "NO_SUPPORTED_FACT_PROJECTION",
            "large diagnostic Raw does not become structured configuration or device facts");

        const string prefix = "<fixture>display diagnostic-information\n";
        const string suffix = "\n<fixture>";
        var bodyBytes = H3cDiagnosticCapture.MaximumOutputBytes - prefix.Length - suffix.Length;
        var exact = prefix + string.Concat(Enumerable.Repeat(new string('x', 64) + "\n", bodyBytes / 65)) + new string('x', bodyBytes % 65) + suffix;
        var maximum = await f.Seal([command], [exact]);
        var bytes = f.Download(maximum.Id, 0).Bytes;
        Assert(bytes.Length == 4 * 1024 * 1024 && Encoding.UTF8.GetString(bytes) == exact,
            "exactly 4 MiB survives diagnostic sealing and verified download");
        var relative = maximum.Raw[0].RelativePath.Replace("'", "''", StringComparison.Ordinal);
        // Simulate a sealed file whose catalog/manifest transaction was absent;
        // do not create a dangling foreign key before startup validation.
        f.Execute($"DELETE FROM task_run_raw_outputs WHERE task_run_id='{maximum.Id}'; DELETE FROM raw_segments WHERE relative_path='{relative}'");
        var restarted = new Catalog(f.Settings); restarted.Initialize();
        var recovery = restarted.RecoverRawSegments();
        Assert(recovery.FailedFiles == 0 && recovery.IndexedOrphanFiles == 1 && recovery.BytesRead >= bytes.Length &&
            f.NewService(restarted).Download(f.Owner, maximum.Id, 0, CancellationToken.None)!.Bytes.SequenceEqual(bytes),
            "recovery scans and reindexes a 4 MiB orphan without reducing or rejecting its verified evidence");
        foreach (var item in new[] { (Command: command, Size: 4 * 1024 * 1024 + 1),
            (Command: H3cCommandPolicy.DisplayVersion, Size: 256 * 1024 + 1) })
        {
            try
            {
                await f.Seal([item.Command], [new string('x', item.Size)]);
                throw new InvalidOperationException("oversized command output was accepted");
            }
            catch (InvalidDataException)
            { Assert(true, "diagnostic and ordinary output limits remain distinct at sealing"); }
        }
    }

    private static async Task LegacySinglePreservesVerifiedEvidence()
    {
        using var f = new Fixture();
        const string output = "<fixture>display version\n设备诊断 ✓\n<fixture>";
        var run = await f.Seal([H3cCommandPolicy.DisplayVersion], [output]);
        var manifest = f.Manifest(run.Id);
        var command = manifest.Commands.Single();
        var text = f.Download(run.Id, 0);
        Assert(manifest.Status == "COMPLETE" && command.Availability == "READY" && manifest.CanDownload &&
            command.ByteCount == Encoding.UTF8.GetByteCount(output) && command.DownloadByteCount == command.ByteCount &&
            command.OutputSha256 == Hash(Encoding.UTF8.GetBytes(output)) && command.DownloadSha256 == command.OutputSha256 &&
            Encoding.UTF8.GetString(text.Bytes) == output && !command.Redacted,
            "legacy single-command UTF8 TXT retains exact sealed output and separately named download hashes");
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert(!json.Contains("raw/") && !json.Contains("relativePath") && !json.Contains("parsedJson") &&
            !json.Contains("FIXTURE_REFERENCE") && !json.Contains("credential") && !json.Contains(f.Profile),
            "manifest exposes only safe command evidence metadata");
        var files = f.Files(); var raw = File.ReadAllBytes(f.Path(run.Raw[0])); var taskCount = f.Scalar("SELECT COUNT(*) FROM task_runs");
        Assert(f.Service.Manifest(f.Owner, run.Id, CancellationToken.None)?.Commands.Single() == command && f.Service.CanRead(f.Owner, run.Id, text.Access),
            "current principal may read an unchanged target");
        var afterRestart = f.NewService(new Catalog(f.Settings)).Download(f.Owner, run.Id, 0, CancellationToken.None)!;
        Assert(afterRestart.Bytes.SequenceEqual(text.Bytes) && files.SequenceEqual(f.Files()) && raw.SequenceEqual(File.ReadAllBytes(f.Path(run.Raw[0])) ) &&
            taskCount == f.Scalar("SELECT COUNT(*) FROM task_runs"), "reading and restarting evidence services creates no files, tasks or new collection");
    }

    private static async Task MultiCommandZipExcludesConfiguration()
    {
        using var f = new Fixture();
        var run = await f.Seal([H3cCommandPolicy.DisplayVersion, H3cCommandPolicy.DisplayCurrentConfiguration,
            H3cCommandPolicy.DisplayWlanMeshProfile, H3cCommandPolicy.DisplayInterfaceBrief],
            ["version ok", "password cipher fixture-secret", "mesh-profile fixture-secret", "ports ok\ncommunity private-value\n-----BEGIN PRIVATE KEY-----\nkey-material\n-----END PRIVATE KEY-----\n"]);
        var manifest = f.Manifest(run.Id);
        Assert(manifest.Status == "PARTIAL" && manifest.CanDownload && manifest.Commands[1].Availability == "CONFIG_EXCLUDED" &&
            manifest.Commands[2].Availability == "UNSUPPORTED_COMMAND" && !manifest.Commands[1].CanDownload &&
            manifest.Commands[1].OutputSha256 is null, "configuration and profile commands are explicitly excluded without metadata leakage");
        var download = f.Download(run.Id);
        using var zip = new ZipArchive(new MemoryStream(download.Bytes));
        Assert(zip.Entries.Select(item => item.FullName).SequenceEqual(new[] { "manifest.json", "command-001.txt", "command-004.txt" }),
            "ZIP contains the bounded manifest and only individually authorized diagnostic members");
        using var manifestReader = new StreamReader(zip.GetEntry("manifest.json")!.Open());
        Assert(await manifestReader.ReadToEndAsync() == JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            "ZIP manifest describes the actual commands and exclusions");
        var txt = f.Download(run.Id, 3);
        var sanitized = Encoding.UTF8.GetString(txt.Bytes);
        Assert(!sanitized.Contains("private-value") && !sanitized.Contains("key-material") && sanitized.Contains("ports ok") &&
            sanitized.Contains("<REDACTED>") && manifest.Commands[3].Redacted && manifest.Commands[3].DownloadSha256 == Hash(txt.Bytes) &&
            manifest.Commands[3].OutputSha256 != manifest.Commands[3].DownloadSha256,
            "defensive secret-line/private-key redaction preserves original proof and hashes the actual download separately");
        Expect(() => f.Download(run.Id, 1), "DIAGNOSTIC_COMMAND_EXCLUDED");
        Expect(() => f.Download(run.Id, 2), "DIAGNOSTIC_COMMAND_EXCLUDED");
        Expect(() => f.Download(run.Id, -1), "INVALID_COMMAND_INDEX", 400);
        Expect(() => f.Download(run.Id, 4), "INVALID_COMMAND_INDEX", 400);
        var config = await f.Seal([H3cCommandPolicy.DisplayCurrentConfiguration], ["password cipher fixture-secret"]);
        Assert(!f.Manifest(config.Id).CanDownload, "configuration-only task has no diagnostic download");
        Expect(() => f.Download(config.Id), "DIAGNOSTIC_NO_RAW");
    }

    private static async Task PartialAndMissingRemainExplicit()
    {
        using var f = new Fixture();
        var run = await f.Seal([H3cCommandPolicy.DisplayVersion, H3cCommandPolicy.DisplayInterfaceBrief, H3cCommandPolicy.DisplayDevice],
            ["version", "incomplete ports"], "FAILED", ["COMPLETE", "PARTIAL"]);
        var manifest = f.Manifest(run.Id);
        Assert(manifest.Status == "PARTIAL" && manifest.CanDownload && manifest.Commands[1].CaptureStatus == "PARTIAL" &&
            manifest.Commands[2].Availability == "NO_RAW" && !manifest.Commands[2].CanDownload,
            "failed multi-command run retains sealed partial evidence and marks unexecuted commands without inventing output");
        using var zip = new ZipArchive(new MemoryStream(f.Download(run.Id).Bytes));
        Assert(zip.Entries.Count == 3 && Encoding.UTF8.GetString(f.Download(run.Id, 1).Bytes) == "incomplete ports",
            "partial download succeeds only for the existing sealed records");
        Expect(() => f.Download(run.Id, 2), "DIAGNOSTIC_NO_RAW");
        var active = f.Start([H3cCommandPolicy.DisplayVersion]);
        Assert(f.Manifest(active.Id).Commands[0].Availability == "TASK_ACTIVE", "active task is not mislabeled as complete evidence");
        Expect(() => f.Download(active.Id), "DIAGNOSTIC_TASK_ACTIVE");
        var empty = await f.Seal([H3cCommandPolicy.DisplayVersion], [""]);
        Assert(f.Download(empty.Id, 0).Bytes.Length == 0 && f.Manifest(empty.Id).Commands[0].ByteCount == 0,
            "sealed zero-byte output remains valid empty evidence");
    }

    private static async Task TamperedEvidenceFailsClosed()
    {
        var mutations = new (string Name, Action<Fixture, Run> Mutate, string Code)[]
        {
            ("file bytes", (f,r) => File.AppendAllText(f.Path(r.Raw[0]), "x"), "DIAGNOSTIC_RAW_INVALID"),
            ("missing file", (f,r) => File.Delete(f.Path(r.Raw[0])), "DIAGNOSTIC_RAW_INVALID"),
            ("output hash", (f,r) => f.Execute("UPDATE task_run_raw_outputs SET sha256='" + new string('0',64) + "'"), "DIAGNOSTIC_RAW_INVALID"),
            ("output bytes", (f,r) => f.Execute("UPDATE task_run_raw_outputs SET byte_count=1"), "DIAGNOSTIC_RAW_INVALID"),
            ("segment hash", (f,r) => f.Execute("UPDATE raw_segments SET sha256='" + new string('0',64) + "' WHERE task_run_id='" + r.Id + "'"), "DIAGNOSTIC_RAW_INVALID"),
            ("segment source", (f,r) => f.Execute("UPDATE raw_segments SET source_id='syslog-local' WHERE task_run_id='" + r.Id + "'"), "DIAGNOSTIC_RAW_INVALID"),
            ("segment run", (f,r) => f.Execute("UPDATE raw_segments SET task_run_id='" + Guid.NewGuid().ToString("N") + "' WHERE task_run_id='" + r.Id + "'"), "DIAGNOSTIC_RAW_INVALID"),
            ("segment state", (f,r) => f.Execute("UPDATE raw_segments SET state='INCOMPLETE' WHERE task_run_id='" + r.Id + "'"), "DIAGNOSTIC_RAW_INVALID"),
            ("record count", (f,r) => f.Execute("UPDATE raw_segments SET record_count=2 WHERE task_run_id='" + r.Id + "'"), "DIAGNOSTIC_RAW_INVALID"),
            ("catalog bytes", (f,r) => f.Execute("UPDATE raw_segments SET byte_count=3 WHERE task_run_id='" + r.Id + "'"), "DIAGNOSTIC_RAW_INVALID"),
            ("plan command", (f,r) => f.Execute("UPDATE task_run_raw_outputs SET command='display device'"), "DIAGNOSTIC_RAW_INVALID"),
            ("path traversal", (f,r) => f.Execute("UPDATE task_run_raw_outputs SET relative_path='../catalog.db'"), "DIAGNOSTIC_RAW_INVALID"),
            ("output limit", (f,r) => f.Execute("UPDATE task_run_raw_outputs SET byte_count=262145"), "DIAGNOSTIC_SIZE_LIMIT"),
            ("record limit", (f,r) => File.WriteAllBytes(f.Path(r.Raw[0]), new byte[512*1024+1]), "DIAGNOSTIC_SIZE_LIMIT"),
            ("UTF8", (f,r) => f.Rewrite(r, o => { var data = new byte[]{0xc0,0x80}; o["output_utf8_base64"]=Convert.ToBase64String(data); o["output_byte_count"]=2; o["output_sha256"]=Hash(data); }), "DIAGNOSTIC_RAW_INVALID"),
            ("record profile", (f,r) => f.Rewrite(r,o => o["device_profile_id"]=Guid.NewGuid().ToString("N")), "DIAGNOSTIC_RAW_INVALID"),
            ("record run", (f,r) => f.Rewrite(r,o => o["task_run_id"]=Guid.NewGuid().ToString("N")), "DIAGNOSTIC_RAW_INVALID"),
            ("record host", (f,r) => f.Rewrite(r,o => o["device_host"]="192.0.2.250"), "DIAGNOSTIC_RAW_INVALID"),
            ("record command", (f,r) => f.Rewrite(r,o => o["command"]="display device"), "DIAGNOSTIC_RAW_INVALID"),
            ("record index", (f,r) => f.Rewrite(r,o => o["command_index"]=1), "DIAGNOSTIC_RAW_INVALID"),
            ("capture status", (f,r) => f.Rewrite(r,o => o["capture_status"]="PARTIAL"), "DIAGNOSTIC_RAW_INVALID"),
            ("missing capture", (f,r) => f.Rewrite(r,o => o.Remove("capture_status")), "DIAGNOSTIC_RAW_INVALID"),
            ("capture time", (f,r) => f.Rewrite(r,o => o["captured_ms"]=1), "DIAGNOSTIC_RAW_INVALID"),
        };
        foreach (var mutation in mutations)
        {
            using var f = new Fixture();
            var run = await f.Seal([H3cCommandPolicy.DisplayVersion], ["valid fixture"]);
            mutation.Mutate(f, run);
            Assert(!f.Manifest(run.Id).CanDownload && f.Manifest(run.Id).Commands.Single().Availability == "RAW_INVALID", mutation.Name + " is rejected in manifest");
            Expect(() => f.Download(run.Id), mutation.Code, mutation.Code == "DIAGNOSTIC_SIZE_LIMIT" ? 413 : 409);
        }
        using var multi = new Fixture();
        var mixed = await multi.Seal([H3cCommandPolicy.DisplayVersion, H3cCommandPolicy.DisplayDevice], ["first", "second"]);
        File.AppendAllText(multi.Path(mixed.Raw[1]), "corrupt");
        Assert(!multi.Manifest(mixed.Id).CanDownload && multi.Manifest(mixed.Id).Commands[0].CanDownload,
            "one corrupt member blocks the bundle without invalidating an independently verified command");
        Expect(() => multi.Download(mixed.Id), "DIAGNOSTIC_RAW_INVALID");
        Assert(Encoding.UTF8.GetString(multi.Download(mixed.Id, 0).Bytes) == "first", "single selection never exports another damaged command");
    }

    private static async Task CurrentScopeAndTargetCannotDrift()
    {
        using var f = new Fixture();
        var run = await f.Seal([H3cCommandPolicy.DisplayVersion], ["ok"]);
        var baseline = f.Download(run.Id);
        var localSite = baseline.Access.Scope.SiteId;
        var scope = new P2B5SiteScopeDecision("AUTHORIZED", false, new HashSet<string>{localSite}, "fixture");
        Assert(f.Catalog.ReadTaskDiagnosticEvidence(run.Id, scope, CancellationToken.None)?.Manifest.CanDownload == true &&
            f.Catalog.ReadTaskDiagnosticEvidence(run.Id, scope with { AllowedSiteReferences = new HashSet<string>{"other"} }, CancellationToken.None) is null,
            "frozen and current Site both constrain reads even when Raw exists");
        foreach (var mutation in new[] {
            "UPDATE h3c_device_profiles SET host='192.0.2.250'", "UPDATE h3c_device_profiles SET port=23",
            "UPDATE device_inventory SET enabled=0", "UPDATE h3c_device_profiles SET enabled=0",
            "UPDATE device_inventory SET site_id='other'", "UPDATE collection_sources SET site_id='other' WHERE kind='H3C_SSH'",
            "UPDATE task_definitions SET request_json=json_set(request_json,'$.scopeSnapshot.siteId','other')" })
        {
            using var changed = new Fixture();
            var item = await changed.Seal([H3cCommandPolicy.DisplayVersion], ["ok"]);
            changed.Execute("INSERT OR IGNORE INTO sites(id,name) VALUES('other','Other')");
            var access = changed.Download(item.Id).Access;
            changed.Execute(mutation);
            Assert(changed.Service.Manifest(changed.Owner, item.Id, CancellationToken.None) is null &&
                !changed.Service.CanRead(changed.Owner, item.Id, access), "current endpoint, target or frozen Site mutation hides prior evidence: " + mutation);
        }
    }

    private static async Task CurrentSessionCannotSurviveRevocation()
    {
        using var f = new Fixture();
        var run = await f.Seal([H3cCommandPolicy.DisplayVersion], ["ok"]);
        var access = f.Download(run.Id).Access;
        foreach (var mutation in new[] { "UPDATE web_sessions SET revoked_ms=1", "UPDATE web_sessions SET expires_ms=1",
            "UPDATE web_users SET disabled=1 WHERE username='superadmin'", "UPDATE web_users SET role='ADMIN' WHERE username='superadmin'" })
        {
            f.Execute(mutation);
            Assert(f.Service.Manifest(f.Owner, run.Id, CancellationToken.None) is null && !f.Service.CanRead(f.Owner, run.Id, access),
                "cached identity loses evidence access after logout, expiry, disable or loss of Site authority");
            f.Execute("UPDATE web_sessions SET revoked_ms=NULL,expires_ms=9999999999999; UPDATE web_users SET disabled=0,role='SUPERADMIN' WHERE username='superadmin'");
        }
        Assert(f.Scalar("SELECT MIN(last_seen_ms) FROM web_sessions") == f.InitialSeen,
            "download authorization rechecks do not write session activity or other persistent state");
    }

    private static async Task DownloadRechecksEachChunkAndCancels()
    {
        using var f = new Fixture();
        var run = await f.Seal([H3cCommandPolicy.DisplayVersion], [new string('x', 200_000)]);
        var item = f.Download(run.Id, 0);
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var lifetime = new Lifetime();
        var response = new TriggerStream(() => f.Execute("UPDATE web_sessions SET revoked_ms=1"));
        var context = new DefaultHttpContext { RequestServices = services };
        context.Features.Set<IHttpRequestLifetimeFeature>(lifetime); context.Response.Body = response;
        await new TaskDiagnosticDownloadResult(item, () => f.Service.CanRead(f.Owner, run.Id, item.Access)).ExecuteAsync(context);
        Assert(lifetime.Aborted && response.Length == TaskDiagnosticDownloadResult.ChunkBytes && context.Response.ContentLength == item.Bytes.Length,
            "logout after one output chunk aborts before any following bytes and cannot masquerade as a complete response");
        f.Execute("UPDATE web_sessions SET revoked_ms=NULL");
        using var cancelled = new CancellationTokenSource();
        var cancelContext = new DefaultHttpContext { RequestServices = services, RequestAborted = cancelled.Token };
        var cancelStream = new TriggerStream(cancelled.Cancel); cancelContext.Response.Body = cancelStream;
        await new TaskDiagnosticDownloadResult(f.Download(run.Id, 0), () => true).ExecuteAsync(cancelContext);
        Assert(cancelStream.Length == TaskDiagnosticDownloadResult.ChunkBytes && f.Download(run.Id, 0).Bytes.Length == 200_000,
            "client cancellation stops output promptly, releases the snapshot and permits a fresh read");
        var denied = new DefaultHttpContext { RequestServices = services }; denied.Response.Body = new MemoryStream();
        await new TaskDiagnosticDownloadResult(f.Download(run.Id, 0), () => false).ExecuteAsync(denied);
        Assert(denied.Response.StatusCode == 409 && !Encoding.UTF8.GetString(((MemoryStream)denied.Response.Body).ToArray()).Contains(new string('x',100)),
            "permission loss before writing returns only an error, never diagnostic content");
    }

    private static async Task RedactionRemainsBoundedAndConcurrentReadsStayIndependent()
    {
        using var f = new Fixture();
        // Replacing many short secret markers with <REDACTED> expands output;
        // the decoded Raw limit alone cannot bound the actual download.
        var expanded = await f.Seal([H3cCommandPolicy.DisplayVersion],
            [string.Concat(Enumerable.Repeat("psk\n", H3cRawOutput.MaximumBytes / 4))]);
        Assert(!f.Manifest(expanded.Id).CanDownload && f.Manifest(expanded.Id).Commands[0].Availability == "RAW_INVALID",
            "a valid sealed Raw whose redacted representation expands past 256KiB is rejected before download");
        Expect(() => f.Download(expanded.Id, 0), "DIAGNOSTIC_SIZE_LIMIT", 413);
        var run = await f.Seal([H3cCommandPolicy.DisplayVersion, H3cCommandPolicy.DisplayDevice], ["concurrent version", "concurrent device"]);
        var initial = f.Manifest(run.Id);
        var files = f.Files(); var tasks = f.Scalar("SELECT COUNT(*) FROM task_runs");
        var reads = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            var one = f.Download(run.Id, 0);
            using var zip = new ZipArchive(new MemoryStream(f.Download(run.Id).Bytes));
            using var reader = new StreamReader(zip.GetEntry("command-002.txt")!.Open());
            return Hash(one.Bytes) == initial.Commands[0].DownloadSha256 && reader.ReadToEnd() == "concurrent device";
        })));
        Assert(reads.All(value => value), "concurrent TXT and ZIP reads retain independent, verified command membership");
        Assert(files.SequenceEqual(f.Files()) && tasks == f.Scalar("SELECT COUNT(*) FROM task_runs") &&
            f.InitialSeen == f.Scalar("SELECT MIN(last_seen_ms) FROM web_sessions"),
            "concurrent exports create no files, tasks, device commands or session activity writes");
    }

    private sealed record Run(string Id, TaskDefinitionSnapshot Definition, IReadOnlyList<H3cRawOutputManifest> Raw);
    private sealed class Fixture : IDisposable
    {
        public readonly string Root = System.IO.Path.Combine(Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_WORKSPACE_ROOT") ??
            (OperatingSystem.IsWindows() ? @"D:\study\NetConsole-Next-Workspace" : System.IO.Path.Combine(System.IO.Path.GetTempPath(), "netconsole-next-workspace")),
            "test-data", "task-diagnostics-tests", Guid.NewGuid().ToString("N"));
        public readonly string Profile = Guid.NewGuid().ToString("N");
        public RuntimeSettings Settings { get; }
        public Catalog Catalog { get; }
        public TaskDiagnosticEvidenceService Service { get; }
        public WebUserIdentity Owner { get; }
        public string InitialSeen { get; }
        public Fixture()
        {
            Settings = new() { DataRoot = Root, SyslogBind = IPAddress.Loopback, SyslogPort = 0, WebPort = 18771,
                SyslogReceiveBufferBytes = 4 * 1024 * 1024 };
            Catalog = new(Settings); Catalog.Initialize();
            Owner = new WebAuthStore(Settings).TryLogin("superadmin", "superadmin", "127.0.0.1", out _)!.User;
            InitialSeen = Scalar("SELECT MIN(last_seen_ms) FROM web_sessions");
            Catalog.CreateH3cDeviceProfile(new(Profile, "192.0.2.71", 22, "fixture", "FIXTURE_REFERENCE",
                "SHA256:" + Convert.ToBase64String(new byte[32]).TrimEnd('='), true, 0, 0));
            Service = NewService(Catalog);
        }
        public TaskDiagnosticEvidenceService NewService(Catalog catalog) => new(catalog, new(Settings), new(new(Settings), new(Settings), catalog));
        public Run Start(string[] commands)
        {
            var id = Guid.NewGuid().ToString("N");
            var authorizer = new TaskSiteScopeAuthorizer(Catalog, new(Settings));
            Catalog.CreateTaskDefinition(id, "H3C_SSH_READONLY", Profile, commands[0], TimeSpan.FromSeconds(5),
                scopeSnapshot: authorizer.CaptureDefinition(Profile, "local-api-token"), commands: commands);
            var definition = Catalog.GetTaskDefinition(id)!;
            var run = Catalog.CreateTaskRun(definition);
            Catalog.TransitionTaskRun(run, "CONNECTING"); Catalog.TransitionTaskRun(run, "RUNNING");
            return new(run, definition, []);
        }
        public async Task<Run> Seal(string[] commands, string[] output, string terminal = "SUCCEEDED", string[]? captures = null)
        {
            var run = Start(commands); var raw = new List<H3cRawOutputManifest>();
            var sink = new H3cTaskRawResultSink(Catalog);
            for (var index = 0; index < output.Length; index++)
                raw.Add(await sink.PersistCommandAsync(run.Id, run.Definition, commands[index], output[index], captures?[index] ?? "COMPLETE", index));
            if (commands.Length == 1) Catalog.CompleteTaskRunWithRawOutput(run.Id, raw.Single(), terminal, terminal == "SUCCEEDED" ? null : "fixture failure");
            else Catalog.CompleteTaskRunWithRawOutputs(run.Id, raw, terminal, terminal == "SUCCEEDED" ? null : "fixture failure");
            return run with { Raw = raw };
        }
        public TaskDiagnosticManifest Manifest(string run) => Service.Manifest(Owner, run, CancellationToken.None) ?? throw new InvalidOperationException("missing fixture manifest");
        public TaskDiagnosticDownload Download(string run, int? index = null) => Service.Download(Owner, run, index, CancellationToken.None) ?? throw new InvalidOperationException("missing fixture download");
        public string Path(H3cRawOutputManifest raw) => System.IO.Path.Combine(Root, raw.RelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        public string[] Files() => Directory.GetFiles(Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        private SqliteConnection Open() { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = System.IO.Path.Combine(Root,"catalog.db"), Pooling=false, ForeignKeys=false }.ToString()); db.Open(); return db; }
        public void Execute(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText=sql; command.ExecuteNonQuery(); }
        public string Scalar(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText=sql; return Convert.ToString(command.ExecuteScalar())!; }
        public void Rewrite(Run run, Action<JsonObject> mutate)
        {
            var raw = JsonNode.Parse(File.ReadAllText(Path(run.Raw[0])))!.AsObject(); mutate(raw);
            var bytes = Encoding.UTF8.GetBytes(raw.ToJsonString() + "\n"); File.WriteAllBytes(Path(run.Raw[0]), bytes);
            Execute($"UPDATE raw_segments SET sha256='{Hash(bytes)}',byte_count={bytes.Length} WHERE task_run_id='{run.Id}'; UPDATE task_run_raw_outputs SET raw_segment_sha256='{Hash(bytes)}'");
        }
        public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(Root, true); }
    }
    private sealed class TriggerStream(Action trigger) : MemoryStream
    {
        private bool _triggered;
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { await base.WriteAsync(buffer, cancellationToken); if (!_triggered) { _triggered=true; trigger(); } }
    }
    private sealed class Lifetime : IHttpRequestLifetimeFeature
    {
        public CancellationToken RequestAborted { get; set; }
        public bool Aborted { get; private set; }
        public void Abort() => Aborted = true;
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); _checks++; }
    private static void Expect(Action action, string code, int status = 409)
    {
        try { action(); throw new InvalidOperationException("expected " + code); }
        catch (TaskDiagnosticException ex) { Assert(ex.Code == code && ex.StatusCode == status, "expected " + code + ", got " + ex.Code + "/" + ex.StatusCode); }
    }
}
