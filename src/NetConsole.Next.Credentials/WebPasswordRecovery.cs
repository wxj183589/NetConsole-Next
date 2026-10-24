using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace NetConsole.Next.Credentials;

public sealed record WebPasswordResetResult(string Username, int RevokedSessions, long AtMs);

public static class WebPasswordRecovery
{
    private const int PasswordIterations = 210_000;

    public static WebPasswordResetResult Reset(string dataRoot, string username, string newPassword, string source = "local-manager")
    {
        var root = ValidateManagedRoot(dataRoot);
        username = username.Trim();
        if (username is not ("admin" or "superadmin")) throw new ArgumentException("Only admin or superadmin can be recovered.", nameof(username));
        if (string.IsNullOrEmpty(newPassword) || (newPassword.Length < 8 && !string.Equals(newPassword, username, StringComparison.Ordinal)))
            throw new ArgumentException("Custom password must contain at least 8 characters.", nameof(newPassword));
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var db = new SqliteConnection($"Data Source={Path.Combine(root, "catalog.db")}");
        db.Open();
        using var pragma = db.CreateCommand(); pragma.CommandText = "PRAGMA busy_timeout=10000; PRAGMA foreign_keys=ON;"; pragma.ExecuteNonQuery();
        using var transaction = db.BeginTransaction();
        EnsureAuditTable(db, transaction);
        string? id = null;
        using (var find = db.CreateCommand())
        {
            find.Transaction = transaction; find.CommandText = "SELECT id FROM web_users WHERE username=$username AND disabled=0"; find.Parameters.AddWithValue("$username", username);
            id = find.ExecuteScalar()?.ToString();
        }
        if (id is null)
        {
            Audit(db, transaction, username, now, source, "NOT_FOUND");
            transaction.Commit();
            throw new InvalidOperationException("The requested Web user does not exist.");
        }
        var salt = RandomNumberGenerator.GetBytes(24);
        var hash = Rfc2898DeriveBytes.Pbkdf2(newPassword, salt, PasswordIterations, HashAlgorithmName.SHA256, 32);
        try
        {
            using var update = db.CreateCommand(); update.Transaction = transaction;
            update.CommandText = "UPDATE web_users SET password_salt=$salt,password_hash=$hash,password_iterations=$iterations,updated_ms=$now WHERE id=$id";
            update.Parameters.AddWithValue("$salt", salt); update.Parameters.AddWithValue("$hash", hash); update.Parameters.AddWithValue("$iterations", PasswordIterations); update.Parameters.AddWithValue("$now", now); update.Parameters.AddWithValue("$id", id); update.ExecuteNonQuery();
            using var revoke = db.CreateCommand(); revoke.Transaction = transaction;
            revoke.CommandText = "UPDATE web_sessions SET revoked_ms=$now WHERE user_id=$id AND revoked_ms IS NULL";
            revoke.Parameters.AddWithValue("$now", now); revoke.Parameters.AddWithValue("$id", id); var revoked = revoke.ExecuteNonQuery();
            Audit(db, transaction, username, now, source, "RESET");
            transaction.Commit();
            return new(username, revoked, now);
        }
        finally { CryptographicOperations.ZeroMemory(hash); CryptographicOperations.ZeroMemory(salt); }
    }

    public static string ValidateManagedRoot(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot)) throw new ArgumentException("Data root is required.", nameof(dataRoot));
        var full = Path.GetFullPath(dataRoot);
        if (!Directory.Exists(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Data root must exist and cannot be a reparse point.");
        var marker = Path.Combine(full, ".netconsole-next-root");
        if (!File.Exists(marker) || (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0 || File.ReadAllText(marker).Trim() != "NETCONSOLE_NEXT_V1")
            throw new InvalidOperationException("Data root is not a marked NetConsole Next data directory.");
        return full;
    }

    private static void EnsureAuditTable(SqliteConnection db, SqliteTransaction transaction)
    {
        using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = @"CREATE TABLE IF NOT EXISTS web_password_reset_audit(
            id TEXT PRIMARY KEY, username TEXT NOT NULL, at_ms INTEGER NOT NULL,
            source TEXT NOT NULL, result TEXT NOT NULL)";
        command.ExecuteNonQuery();
    }

    private static void Audit(SqliteConnection db, SqliteTransaction transaction, string username, long atMs, string source, string result)
    {
        using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO web_password_reset_audit(id,username,at_ms,source,result) VALUES($id,$username,$at,$source,$result)";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N")); command.Parameters.AddWithValue("$username", username);
        command.Parameters.AddWithValue("$at", atMs); command.Parameters.AddWithValue("$source", source); command.Parameters.AddWithValue("$result", result); command.ExecuteNonQuery();
    }
}
