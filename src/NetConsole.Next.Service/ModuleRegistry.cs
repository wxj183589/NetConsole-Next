using Microsoft.Data.Sqlite;

namespace NetConsole.Next.Service;

internal static class ModuleRegistryRouting
{
    public static string? ResolveApiModule(PathString path)
    {
        var value = path.Value ?? string.Empty;
        if (value.StartsWith("/api/system/devices", StringComparison.Ordinal) ||
            value.StartsWith("/api/system/device-", StringComparison.Ordinal)) return "devices";
        if (value.StartsWith("/api/system/zte/", StringComparison.Ordinal)) return "zte";
        if (value.StartsWith("/api/system/h3c/", StringComparison.Ordinal)) return "h3c";
        if (value.StartsWith("/api/system/config-backups", StringComparison.Ordinal) ||
            value.StartsWith("/api/system/config-backup-schedules", StringComparison.Ordinal)) return "config-backups";
        if (value.StartsWith("/api/system/network-planning/", StringComparison.Ordinal)) return "network-planning";
        if (value.StartsWith("/api/system/terminal/", StringComparison.Ordinal)) return "terminal";
        if (value.StartsWith("/api/system/tasks", StringComparison.Ordinal)) return "tasks";
        if (value.StartsWith("/api/system/diagnostics/", StringComparison.Ordinal)) return "tools";
        if (value.StartsWith("/api/system/wireless/", StringComparison.Ordinal)) return "wireless";
        if (value.StartsWith("/api/system/rail-reference/", StringComparison.Ordinal)) return "rail";
        if (value.StartsWith("/api/system/host-tools", StringComparison.Ordinal)) return "tools";
        if (value.StartsWith("/api/system/files/", StringComparison.Ordinal)) return "files";
        return null;
    }
}

internal sealed partial class Catalog
{
    public IReadOnlyList<ModuleStateSnapshot> ListModuleStates()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT module_key,label,enabled,is_core,updated_ms,updated_by FROM module_registry ORDER BY module_key";
        using var reader = command.ExecuteReader();
        var result = new List<ModuleStateSnapshot>();
        while (reader.Read())
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2) != 0,
                reader.GetInt32(3) != 0, reader.GetInt64(4), reader.GetString(5)));
        return result;
    }

    public bool IsModuleEnabled(string key)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT enabled FROM module_registry WHERE module_key=$key";
        command.Parameters.AddWithValue("$key", key);
        return Convert.ToInt32(command.ExecuteScalar() ?? 0, System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    public ModuleStateSnapshot SetModuleEnabled(string key, bool enabled, string actor)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 64 ||
            key.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            throw new ArgumentException("Module key is invalid.", nameof(key));
        if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("Actor is required.", nameof(actor));

        using var db = Open();
        using var transaction = db.BeginTransaction();
        bool core;
        string label;
        using (var lookup = db.CreateCommand())
        {
            lookup.Transaction = transaction;
            lookup.CommandText = "SELECT label,is_core FROM module_registry WHERE module_key=$key";
            lookup.Parameters.AddWithValue("$key", key);
            using var reader = lookup.ExecuteReader();
            if (!reader.Read()) throw new KeyNotFoundException($"Module '{key}' was not found.");
            label = reader.GetString(0);
            core = reader.GetInt32(1) != 0;
        }
        if (core && !enabled) throw new InvalidOperationException("Core modules cannot be disabled.");

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using (var update = db.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE module_registry SET enabled=$enabled,updated_ms=$now,updated_by=$actor WHERE module_key=$key";
            update.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
            update.Parameters.AddWithValue("$now", now);
            update.Parameters.AddWithValue("$actor", actor);
            update.Parameters.AddWithValue("$key", key);
            update.ExecuteNonQuery();
        }
        transaction.Commit();
        TryRecordDeviceAuditEvent(actor, "MODULE_STATE_CHANGED", "SUCCEEDED", key, null, Array.Empty<string>(),
            enabled ? "ENABLED" : "DISABLED");
        return new(key, label, enabled, core, now, actor);
    }
}
