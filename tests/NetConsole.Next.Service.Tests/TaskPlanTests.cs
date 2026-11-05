using Microsoft.Data.Sqlite;
using NetConsole.Next.Service;
using System.Net;

internal static class TaskPlanTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "next-task-plan-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new RuntimeSettings { DataRoot = root, WebPort = 18772, SyslogPort = 0,
                SyslogBind = IPAddress.Loopback, SyslogReceiveBufferBytes = 1024 * 1024 };
            var catalog = new Catalog(settings);
            catalog.Initialize();
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
            var partial = catalog.GetTaskPlan("plan-1")!;
            Assert(partial.State == "PARTIAL_FAILED" && partial.Items[0].RunId == "run-a" && partial.Items[1].State == "FAILED",
                "parent state must aggregate independent child run outcomes");
            Console.WriteLine("TASK_PLAN_PERSISTENCE_TESTS=PASS");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
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
