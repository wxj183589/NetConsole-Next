using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace NetConsole.Next.Service;

internal sealed record TaskPlanItemSnapshot(
    int Ordinal,
    string Target,
    string DefinitionId,
    string? RunId,
    string State,
    string? Error);

internal sealed record TaskPlanSnapshot(
    string Id,
    string TaskType,
    string State,
    string ActorUserId,
    IReadOnlyList<string> CommandPlan,
    string ClaimsRevision,
    long CreatedMs,
    long UpdatedMs,
    IReadOnlyList<TaskPlanItemSnapshot> Items);

internal sealed record TaskPlanReportItemSnapshot(
    TaskPlanItemSnapshot Item,
    TaskRunSnapshot? Run,
    IReadOnlyList<TaskCommandSnapshot> Commands);

internal sealed record TaskPlanReportSnapshot(
    TaskPlanSnapshot Plan,
    IReadOnlyList<TaskPlanReportItemSnapshot> Items);

internal sealed partial class Catalog
{
    public void CreateTaskPlan(string id, string taskType, string actorUserId,
        IReadOnlyList<string> commandPlan, string claimsRevision,
        IReadOnlyList<(int Ordinal, string Target, string DefinitionId)> items)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(char.IsControl))
            throw new ArgumentException("Task plan id is invalid.", nameof(id));
        if (taskType != "H3C_SSH_READONLY") throw new ArgumentException("Unsupported task plan type.", nameof(taskType));
        if (string.IsNullOrWhiteSpace(actorUserId)) throw new ArgumentException("Task plan actor is required.", nameof(actorUserId));
        if (string.IsNullOrWhiteSpace(claimsRevision)) throw new ArgumentException("Task plan claims revision is required.", nameof(claimsRevision));
        var commands = commandPlan?.ToArray() ?? throw new ArgumentNullException(nameof(commandPlan));
        if (commands.Length is < 1 or > 32 || commands.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Task plan must contain between one and 32 commands.", nameof(commandPlan));
        var targets = items?.OrderBy(item => item.Ordinal).ToArray() ?? throw new ArgumentNullException(nameof(items));
        if (targets.Length is < 1 or > 64 || targets.Select(item => item.Ordinal).Distinct().Count() != targets.Length ||
            targets.Select(item => item.Target).Distinct(StringComparer.Ordinal).Count() != targets.Length ||
            targets.Any(item => string.IsNullOrWhiteSpace(item.Target) || string.IsNullOrWhiteSpace(item.DefinitionId)))
            throw new ArgumentException("Task plan must contain 1-64 distinct targets.", nameof(items));

        var now = UtcNowMs();
        using var db = Open();
        using var transaction = db.BeginTransaction();
        using (var insert = db.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = @"INSERT INTO task_plans
                (id,task_type,state,actor_user_id,command_plan_json,claims_revision,created_ms,updated_ms)
                VALUES($id,$type,'QUEUED',$actor,$commands,$claims,$now,$now)";
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$type", taskType);
            insert.Parameters.AddWithValue("$actor", actorUserId);
            insert.Parameters.AddWithValue("$commands", JsonSerializer.Serialize(commands));
            insert.Parameters.AddWithValue("$claims", claimsRevision);
            insert.Parameters.AddWithValue("$now", now);
            insert.ExecuteNonQuery();
        }
        foreach (var item in targets)
        {
            using var insert = db.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = @"INSERT INTO task_plan_items
                (plan_id,ordinal,target,definition_id,state)
                VALUES($plan,$ordinal,$target,$definition,'QUEUED')";
            insert.Parameters.AddWithValue("$plan", id);
            insert.Parameters.AddWithValue("$ordinal", item.Ordinal);
            insert.Parameters.AddWithValue("$target", item.Target);
            insert.Parameters.AddWithValue("$definition", item.DefinitionId);
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void SetTaskPlanItemRun(string planId, int ordinal, string runId)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = @"UPDATE task_plan_items SET run_id=$run,state='QUEUED',error=NULL
            WHERE plan_id=$plan AND ordinal=$ordinal";
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$plan", planId);
        command.Parameters.AddWithValue("$ordinal", ordinal);
        if (command.ExecuteNonQuery() != 1) throw new KeyNotFoundException("Task plan item was not found.");
        RefreshTaskPlanState(db, planId);
    }

    public void FailTaskPlanItem(string planId, int ordinal, string error)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = @"UPDATE task_plan_items SET state='FAILED',error=$error
            WHERE plan_id=$plan AND ordinal=$ordinal AND run_id IS NULL";
        command.Parameters.AddWithValue("$error", error.Length > 512 ? error[..512] : error);
        command.Parameters.AddWithValue("$plan", planId);
        command.Parameters.AddWithValue("$ordinal", ordinal);
        command.ExecuteNonQuery();
        RefreshTaskPlanState(db, planId);
    }

    public TaskPlanSnapshot? GetTaskPlan(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        using var db = Open();
        RefreshTaskPlanState(db, id);
        using var plan = db.CreateCommand();
        plan.CommandText = @"SELECT id,task_type,state,actor_user_id,command_plan_json,claims_revision,created_ms,updated_ms
            FROM task_plans WHERE id=$id";
        plan.Parameters.AddWithValue("$id", id);
        using var reader = plan.ExecuteReader();
        if (!reader.Read()) return null;
        var commandPlan = JsonSerializer.Deserialize<string[]>(reader.GetString(4)) ?? Array.Empty<string>();
        var snapshot = new TaskPlanSnapshot(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), commandPlan, reader.GetString(5), reader.GetInt64(6), reader.GetInt64(7),
            Array.Empty<TaskPlanItemSnapshot>());
        reader.Close();
        using var items = db.CreateCommand();
        items.CommandText = @"SELECT i.ordinal,i.target,i.definition_id,i.run_id,
                COALESCE(r.state,i.state),COALESCE(r.failure_reason,i.error)
            FROM task_plan_items i LEFT JOIN task_runs r ON r.id=i.run_id
            WHERE i.plan_id=$id ORDER BY i.ordinal";
        items.Parameters.AddWithValue("$id", id);
        using var itemReader = items.ExecuteReader();
        var result = new List<TaskPlanItemSnapshot>();
        while (itemReader.Read())
            result.Add(new(itemReader.GetInt32(0), itemReader.GetString(1), itemReader.GetString(2),
                itemReader.IsDBNull(3) ? null : itemReader.GetString(3), itemReader.GetString(4),
                itemReader.IsDBNull(5) ? null : itemReader.GetString(5)));
        return snapshot with { Items = result };
    }

    public IReadOnlyList<string> GetTaskPlanRunIds(string id)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT run_id FROM task_plan_items WHERE plan_id=$id AND run_id IS NOT NULL ORDER BY ordinal";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    private static void RefreshTaskPlanState(SqliteConnection db, string planId)
    {
        using var state = db.CreateCommand();
        state.CommandText = @"SELECT i.state,COALESCE(r.state,i.state)
            FROM task_plan_items i LEFT JOIN task_runs r ON r.id=i.run_id WHERE i.plan_id=$id";
        state.Parameters.AddWithValue("$id", planId);
        using var reader = state.ExecuteReader();
        var states = new List<string>();
        while (reader.Read()) states.Add(reader.GetString(1));
        reader.Close();
        if (states.Count == 0) return;
        var terminal = states.All(IsTerminalPlanState);
        var next = terminal
            ? states.All(item => item == "SUCCEEDED") ? "SUCCEEDED"
            : states.All(item => item is "FAILED" or "CANCELLED") ? (states.Any(item => item == "CANCELLED") ? "CANCELLED" : "FAILED")
            : "PARTIAL_FAILED"
            : states.Any(item => item is "RUNNING" or "CONNECTING") ? "RUNNING" : "QUEUED";
        using var update = db.CreateCommand();
        update.CommandText = "UPDATE task_plans SET state=$state,updated_ms=$now WHERE id=$id";
        update.Parameters.AddWithValue("$state", next);
        update.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        update.Parameters.AddWithValue("$id", planId);
        update.ExecuteNonQuery();
    }

    private static bool IsTerminalPlanState(string state) => state is "SUCCEEDED" or "FAILED" or "CANCELLED" or "INTERRUPTED" or "TIMED_OUT";
}
