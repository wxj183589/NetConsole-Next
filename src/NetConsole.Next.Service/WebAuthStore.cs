using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace NetConsole.Next.Service;

internal sealed record WebUserIdentity(string UserId, string Username, string Role, string SessionId);
internal sealed record WebLoginResult(WebUserIdentity User, string SessionToken, string CsrfToken);

internal sealed class WebAuthStore
{
    private const int PasswordIterations = 210_000;
    private const int SessionHours = 12;
    private readonly RuntimeSettings _settings;
    private readonly ConcurrentDictionary<string, LoginWindow> _loginWindows = new(StringComparer.OrdinalIgnoreCase);

    public WebAuthStore(RuntimeSettings settings) => _settings = settings;

    public string InstanceId
    {
        get
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT instance_id FROM instance_identity WHERE id=1";
            return (string?)command.ExecuteScalar() ?? throw new InvalidDataException("Web instance identity is missing.");
        }
    }

    public string InstanceIdOrUnknown
    {
        get { try { return InstanceId; } catch { return "unavailable"; } }
    }

    public WebUserIdentity? ValidateSession(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = @"SELECT s.id,u.id,u.username,u.role FROM web_sessions s
            JOIN web_users u ON u.id=s.user_id
            WHERE s.token_hash=$token AND s.revoked_ms IS NULL AND s.expires_ms>$now AND u.disabled=0";
        command.Parameters.AddWithValue("$token", HashToken(token));
        command.Parameters.AddWithValue("$now", now);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var identity = new WebUserIdentity(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(0));
        reader.Close();
        using var update = db.CreateCommand();
        update.CommandText = "UPDATE web_sessions SET last_seen_ms=$now WHERE id=$id";
        update.Parameters.AddWithValue("$now", now);
        update.Parameters.AddWithValue("$id", identity.SessionId);
        update.ExecuteNonQuery();
        return identity;
    }

    public bool ValidateCsrf(string sessionToken, string csrfToken)
    {
        if (string.IsNullOrWhiteSpace(csrfToken)) return false;
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT csrf_hash FROM web_sessions WHERE token_hash=$token AND revoked_ms IS NULL";
        command.Parameters.AddWithValue("$token", HashToken(sessionToken));
        var stored = command.ExecuteScalar() as byte[];
        return stored is not null && CryptographicOperations.FixedTimeEquals(stored, HashToken(csrfToken));
    }

    public WebLoginResult? TryLogin(string username, string password, string? remoteIp, out bool rateLimited)
    {
        rateLimited = false;
        username = username.Trim();
        var key = $"{remoteIp ?? "unknown"}|{username}";
        if (!CanAttempt(key)) { rateLimited = true; return null; }
        WebUserIdentity? identity = null;
        using (var db = Open())
        {
            using var command = db.CreateCommand();
            command.CommandText = "SELECT id,username,role,password_salt,password_hash,password_iterations FROM web_users WHERE username=$username AND disabled=0";
            command.Parameters.AddWithValue("$username", username);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                var salt = (byte[])reader[3]; var expected = (byte[])reader[4]; var iterations = reader.GetInt32(5);
                var actual = Derive(password, salt, iterations);
                var valid = CryptographicOperations.FixedTimeEquals(actual, expected);
                CryptographicOperations.ZeroMemory(actual);
                if (valid) identity = new WebUserIdentity(reader.GetString(0), reader.GetString(1), reader.GetString(2), string.Empty);
            }
        }
        if (identity is null) { RegisterFailure(key); return null; }
        _loginWindows.TryRemove(key, out _);
        var sessionToken = RandomToken(); var csrfToken = RandomToken();
        var sessionId = Guid.NewGuid().ToString("N"); var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using (var db = Open())
        using (var command = db.CreateCommand())
        {
            command.CommandText = @"INSERT INTO web_sessions(id,user_id,token_hash,csrf_hash,created_ms,last_seen_ms,expires_ms,remote_ip)
                VALUES($id,$user,$token,$csrf,$now,$now,$expires,$ip)";
            command.Parameters.AddWithValue("$id", sessionId); command.Parameters.AddWithValue("$user", identity.UserId);
            command.Parameters.AddWithValue("$token", HashToken(sessionToken)); command.Parameters.AddWithValue("$csrf", HashToken(csrfToken));
            command.Parameters.AddWithValue("$now", now); command.Parameters.AddWithValue("$expires", now + TimeSpan.FromHours(SessionHours).TotalMilliseconds);
            command.Parameters.AddWithValue("$ip", (object?)remoteIp ?? DBNull.Value); command.ExecuteNonQuery();
        }
        return new WebLoginResult(identity with { SessionId = sessionId }, sessionToken, csrfToken);
    }

    public void RevokeSession(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return;
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "UPDATE web_sessions SET revoked_ms=$now WHERE token_hash=$token AND revoked_ms IS NULL";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); command.Parameters.AddWithValue("$token", HashToken(token)); command.ExecuteNonQuery();
    }

    public bool ChangePassword(WebUserIdentity actor, string username, string? currentPassword, string newPassword)
    {
        if (!string.Equals(actor.Username, username, StringComparison.OrdinalIgnoreCase) && actor.Role != "SUPERADMIN") return false;
        using var db = Open(); using var transaction = db.BeginTransaction();
        using var read = db.CreateCommand(); read.Transaction = transaction;
        read.CommandText = "SELECT id,password_salt,password_hash,password_iterations FROM web_users WHERE username=$username AND disabled=0"; read.Parameters.AddWithValue("$username", username);
        using var reader = read.ExecuteReader(); if (!reader.Read()) return false;
        var id = reader.GetString(0); var salt = (byte[])reader[1]; var expected = (byte[])reader[2]; var iterations = reader.GetInt32(3);
        if (string.Equals(actor.Username, username, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(currentPassword)) return false;
            var actual = Derive(currentPassword, salt, iterations); var valid = CryptographicOperations.FixedTimeEquals(actual, expected); CryptographicOperations.ZeroMemory(actual); if (!valid) return false;
        }
        reader.Close();
        var nextSalt = RandomNumberGenerator.GetBytes(24); var nextHash = Derive(newPassword, nextSalt, PasswordIterations);
        using var update = db.CreateCommand(); update.Transaction = transaction;
        update.CommandText = "UPDATE web_users SET password_salt=$salt,password_hash=$hash,password_iterations=$iterations,updated_ms=$now WHERE id=$id";
        update.Parameters.AddWithValue("$salt", nextSalt); update.Parameters.AddWithValue("$hash", nextHash); update.Parameters.AddWithValue("$iterations", PasswordIterations); update.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); update.Parameters.AddWithValue("$id", id); update.ExecuteNonQuery();
        transaction.Commit(); CryptographicOperations.ZeroMemory(nextHash); return true;
    }

    internal static void SeedDefaults(SqliteConnection db, SqliteTransaction? transaction = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Seed(db, transaction, "admin", "ADMIN", "admin", now); Seed(db, transaction, "superadmin", "SUPERADMIN", "superadmin", now);
        using var identity = db.CreateCommand(); identity.Transaction = transaction; identity.CommandText = "INSERT OR IGNORE INTO instance_identity(id,instance_id,created_ms) VALUES(1,$id,$now)"; identity.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N")); identity.Parameters.AddWithValue("$now", now); identity.ExecuteNonQuery();
    }

    private static void Seed(SqliteConnection db, SqliteTransaction? transaction, string username, string role, string password, long now)
    {
        var salt = RandomNumberGenerator.GetBytes(24); var hash = Derive(password, salt, PasswordIterations);
        using var command = db.CreateCommand(); command.Transaction = transaction; command.CommandText = @"INSERT OR IGNORE INTO web_users(id,username,role,password_salt,password_hash,password_iterations,created_ms,updated_ms) VALUES($id,$username,$role,$salt,$hash,$iterations,$now,$now)";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N")); command.Parameters.AddWithValue("$username", username); command.Parameters.AddWithValue("$role", role); command.Parameters.AddWithValue("$salt", salt); command.Parameters.AddWithValue("$hash", hash); command.Parameters.AddWithValue("$iterations", PasswordIterations); command.Parameters.AddWithValue("$now", now); command.ExecuteNonQuery(); CryptographicOperations.ZeroMemory(hash);
    }

    private bool CanAttempt(string key) { if (!_loginWindows.TryGetValue(key, out var value)) return true; if (value.ResetAt < DateTimeOffset.UtcNow) { _loginWindows.TryRemove(key, out _); return true; } return value.Count < 5; }
    private void RegisterFailure(string key) => _loginWindows.AddOrUpdate(key, _ => new LoginWindow(1, DateTimeOffset.UtcNow.AddMinutes(1)), (_, value) => value with { Count = value.Count + 1 });
    private SqliteConnection Open() { var db = new SqliteConnection($"Data Source={Path.Combine(_settings.DataRoot, "catalog.db")}"); db.Open(); using var command = db.CreateCommand(); command.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;"; command.ExecuteNonQuery(); return db; }
    private static byte[] HashToken(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
    private static byte[] Derive(string password, byte[] salt, int iterations) => Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
    private static string RandomToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private sealed record LoginWindow(int Count, DateTimeOffset ResetAt);
}

