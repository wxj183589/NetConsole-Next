using System.Reflection;
using Microsoft.Data.Sqlite;

namespace NetConsole.Next.Service;

internal sealed class Catalog(RuntimeSettings settings)
{
    private string DatabasePath => Path.Combine(settings.DataRoot, "catalog.db");
    public string RawRoot => Path.Combine(settings.DataRoot, "raw");

    public void Initialize()
    {
        Directory.CreateDirectory(settings.DataRoot);
        var marker = Path.Combine(settings.DataRoot, ".netconsole-next-root");
        if (!File.Exists(marker))
        {
            if (Directory.EnumerateFileSystemEntries(settings.DataRoot).Any())
                throw new InvalidOperationException("Refusing a nonempty unmarked data directory.");
            File.WriteAllText(marker, "NETCONSOLE_NEXT_V1\n");
        }
        else if (File.ReadAllText(marker).Trim() != "NETCONSOLE_NEXT_V1")
            throw new InvalidOperationException("Data directory marker is unknown.");

        Directory.CreateDirectory(RawRoot);
        using var db = Open();
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("schema_v1.sql")
            ?? throw new InvalidOperationException("Embedded schema_v1.sql was not found.");
        using var reader = new StreamReader(resource);
        using var command = db.CreateCommand();
        command.CommandText = reader.ReadToEnd();
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false
        }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return db;
    }

    public void RegisterFinalizedSegment(string relative, long count, long first, long last, string sha, long bytes)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        var path = relative.Replace('\\', '/');

        using (var existing = db.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = @"SELECT source_id,state,first_received_ms,last_received_ms,
                record_count,byte_count,sha256
                FROM raw_segments WHERE relative_path=$path";
            existing.Parameters.AddWithValue("$path", path);
            using var reader = existing.ExecuteReader();
            if (reader.Read())
            {
                var same = reader.GetString(0) == "syslog-local"
                    && reader.GetString(1) == "FINALIZED"
                    && reader.GetInt64(2) == first
                    && reader.GetInt64(3) == last
                    && reader.GetInt64(4) == count
                    && reader.GetInt64(5) == bytes
                    && reader.GetString(6) == sha;
                if (!same)
                    throw new InvalidOperationException($"Raw segment catalog metadata conflicts for '{path}'.");
                transaction.Commit();
                return;
            }
        }

        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"INSERT INTO raw_segments
            (id,source_id,relative_path,state,first_received_ms,last_received_ms,
             record_count,byte_count,sha256,sealed_ms)
            VALUES ($id,'syslog-local',$path,'FINALIZED',$first,$last,$count,$bytes,$sha,$now)";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$first", first);
        command.Parameters.AddWithValue("$last", last);
        command.Parameters.AddWithValue("$count", count);
        command.Parameters.AddWithValue("$bytes", bytes);
        command.Parameters.AddWithValue("$sha", sha);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    // Includes live .open files; pending crash recovery needs a future G1 scanner.
    public int OpenSegmentCount() => Directory.Exists(RawRoot)
        ? Directory.EnumerateFiles(RawRoot, "*.open", SearchOption.AllDirectories).Take(10001).Count()
        : 0;
}
