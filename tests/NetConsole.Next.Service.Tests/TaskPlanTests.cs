using Microsoft.Data.Sqlite;
using NetConsole.Next.Service;
using System.Net;
using System.Text.Json;

internal static class TaskPlanTests
{
    public static void Run()
    {
        var repository = RepositoryRoot();
        var root = Path.Combine(repository.Parent!.FullName, "test-data", "next-task-plan-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new RuntimeSettings { DataRoot = root, WebPort = 18772, SyslogPort = 0,
                SyslogBind = IPAddress.Loopback, SyslogReceiveBufferBytes = 1024 * 1024 };
            var catalog = new Catalog(settings);
            catalog.Initialize();
            var rollbackSql = File.ReadAllText(Path.Combine(repository.FullName, "storage", "schema_v26_rollback.sql"));
            Execute(root, rollbackSql);
            Assert(Scalar(root, "SELECT MAX(version) FROM schema_versions") == 25 &&
                Scalar(root, "SELECT COUNT(*) FROM sqlite_master WHERE name='task_plan_retries'") == 0,
                "empty v26 rollback produces a v25 database");
            catalog.CreateH3cDeviceProfile(new(Guid.NewGuid().ToString("N"), "192.0.2.101", 22, "fixture", "FIXTURE_REFERENCE",
                string.Empty, true, 0, 0));
            InsertDefinition(root, "plan-definition-a", "profile-a");
            InsertDefinition(root, "plan-definition-b", "profile-b");
            catalog.CreateTaskPlan("plan-1", "H3C_SSH_READONLY", "actor", ["display version"], "claims-1",
                [(0, "profile-a", "plan-definition-a"), (1, "profile-b", "plan-definition-b")]);
            var queued = catalog.GetTaskPlan("plan-1")!;
            Assert(queued.State == "QUEUED" && queued.Items.Count == 2 && queued.Items.All(item => item.State == "QUEUED"),
                "a parent plan must persist all child targets before execution");
            InsertRun(root, "run-a", "plan-definition-a", "SUCCEEDED");
            InsertRun(root, "run-b", "plan-definition-b", "FAILED");
            catalog.SetTaskPlanItemRun("plan-1", 0, "run-a");
            catalog.SetTaskPlanItemRun("plan-1", 1, "run-b");
            var legacy = Snapshot(root, "device_inventory", "task_definitions", "task_plans", "task_plan_items", "task_runs");
            using (var db = Open(root))
            {
                Catalog.ApplyV26Migration(db);
                Catalog.ApplyV26Migration(db);
            }
            Assert(Snapshot(root, "device_inventory", "task_definitions", "task_plans", "task_plan_items", "task_runs") == legacy &&
                Scalar(root, "SELECT COUNT(*) FROM schema_versions WHERE version=26") == 1 &&
                Scalar(root, "SELECT COUNT(*) FROM task_plan_retries") == 0,
                "v25 device/definition/plan/item/run rows survive an idempotent v26 upgrade unchanged");
            Execute(root, rollbackSql);
            Assert(Scalar(root, "SELECT MAX(version) FROM schema_versions") == 25 &&
                Snapshot(root, "device_inventory", "task_definitions", "task_plans", "task_plan_items", "task_runs") == legacy,
                "empty-history rollback preserves every legacy row");
            using (var db = Open(root)) Catalog.ApplyV26Migration(db);
            InsertRun(root, "run-b-retry", "plan-definition-b", "QUEUED");
            catalog.CreateTaskPlanRetry("plan-1", 1, "run-b", "run-b-retry", [0], "actor");
            var partial = catalog.GetTaskPlan("plan-1")!;
            Assert(partial.State == "PARTIAL_FAILED" && partial.Items[0].RunId == "run-a" && partial.Items[1].State == "FAILED",
                "parent state must aggregate independent child run outcomes");
            var retries = catalog.GetTaskPlanRetries("plan-1");
            Assert(retries.Count == 1 && retries[0].OriginalRunId == "run-b" && retries[0].RetryRunId == "run-b-retry" &&
                retries[0].CommandIndexes.SequenceEqual([0]) && partial.Items[1].RunId == "run-b",
                "retry history must preserve the original plan item association");
            Assert(JsonSerializer.Serialize(new Catalog(settings).GetTaskPlanRetries("plan-1")) == JsonSerializer.Serialize(retries),
                "retry metadata can be read from a new Catalog instance");
            var beforeRejectedRollback = Snapshot(root, "device_inventory", "task_definitions", "task_plans", "task_plan_items", "task_runs", "task_plan_retries", "schema_versions");
            var rejected = false;
            try { Execute(root, rollbackSql); }
            catch (SqliteException ex) when (ex.Message.Contains("V26_ROLLBACK_RETRY_HISTORY_EXISTS", StringComparison.Ordinal))
            { rejected = true; }
            Assert(rejected && Snapshot(root, "device_inventory", "task_definitions", "task_plans", "task_plan_items", "task_runs", "task_plan_retries", "schema_versions") == beforeRejectedRollback,
                "rollback with retry history must abort atomically and retain all history and the v26 marker");
            using (var db = Open(root)) Catalog.ApplyV26Migration(db);
            Assert(new Catalog(settings).GetTaskPlanRetries("plan-1").Count == 1,
                "reapplying v26 after rejected rollback cannot erase persisted retry history");
            Console.WriteLine("TASK_PLAN_PERSISTENCE_TESTS=PASS");
            Console.WriteLine("TASK_PLAN_V26_UPGRADE_ROLLBACK_TESTS=PASS");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static DirectoryInfo RepositoryRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "tools", "workspace_paths.py"))) return directory;
        throw new InvalidOperationException("Repository root unavailable.");
    }

    private static string Snapshot(string root, params string[] tables)
    {
        using var db = Open(root);
        var result = new Dictionary<string, List<object?[]>>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            using var command = db.CreateCommand();
            command.CommandText = "SELECT * FROM " + table + " ORDER BY rowid";
            using var reader = command.ExecuteReader();
            var rows = new List<object?[]>();
            while (reader.Read()) rows.Add(Enumerable.Range(0, reader.FieldCount)
                .Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index)).ToArray());
            result.Add(table, rows);
        }
        return JsonSerializer.Serialize(result);
    }

    private static long Scalar(string root, string sql)
    {
        using var db = Open(root); using var command = db.CreateCommand();
        command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void Execute(string root, string sql)
    {
        using var db = Open(root); using var command = db.CreateCommand();
        command.CommandText = sql; command.ExecuteNonQuery();
    }

    private static void InsertDefinition(string root, string id, string target)
    {
        using var db = Open(root);
        using var command = db.CreateCommand();
        command.CommandText = @"INSERT INTO task_definitions(id,task_type,target,request_json,timeout_ms,enabled,restart_policy,created_ms,updated_ms)
            VALUES($id,'H3C_SSH_READONLY',$target,$request,1000,1,'NEVER',$now,$now)";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$target", target);
        command.Parameters.AddWithValue("$request", "{\"command\":\"display version\",\"commands\":[\"display version\"]}");
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); command.ExecuteNonQuery();
    }

    private static void InsertRun(string root, string id, string definition, string state)
    {
        using var db = Open(root);
        using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO task_runs(id,task_type,state,definition_id,ended_ms) VALUES($id,'H3C_SSH_READONLY',$state,$definition,$now)";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$definition", definition); command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); command.ExecuteNonQuery();
    }

    private static SqliteConnection Open(string root)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db"), Pooling = false }.ToString());
        db.Open(); return db;
    }

    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
