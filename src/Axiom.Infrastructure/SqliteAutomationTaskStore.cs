using System.Text.Json;
using Axiom.Domain;
using Microsoft.Data.Sqlite;

namespace Axiom.Infrastructure;

// Capability parameters are versioned JSON, independent of plugin-specific columns.
public sealed partial class SqliteAutomationTaskStore : IDisposable
{
    private static readonly JsonSerializerOptions TaskJson = new() { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    private readonly SqliteConnection connection;
    private readonly SemaphoreSlim gate = new(1, 1);
    public SqliteAutomationTaskStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 2 }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        var storedVersion = Convert.ToInt32(cmd.ExecuteScalar());
        if (storedVersion > 4) { connection.Dispose(); throw new InvalidDataException("任务数据库版本较新，请更新 Axiom。"); }
        cmd.CommandText = "PRAGMA busy_timeout=2000; PRAGMA synchronous=FULL; CREATE TABLE IF NOT EXISTS automation_tasks(id TEXT PRIMARY KEY, version INTEGER NOT NULL, configuration TEXT NOT NULL); CREATE TABLE IF NOT EXISTS host_startup(id INTEGER PRIMARY KEY CHECK(id=1), boot_id INTEGER NOT NULL); PRAGMA user_version=" + Math.Max(2, storedVersion) + ";";
        cmd.ExecuteNonQuery();
        CreateWindowOnceAttempts();
    }
    public async Task<AutomationTask[]> ListAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            using var cmd = connection.CreateCommand(); cmd.CommandText = "SELECT configuration FROM automation_tasks ORDER BY id;";
            using var reader = cmd.ExecuteReader(); var list = new List<AutomationTask>();
            while (reader.Read())
            {
                var task = JsonSerializer.Deserialize<AutomationTask>(reader.GetString(0), TaskJson) ?? throw new InvalidDataException("任务配置无效。");
                TaskDetectionExpressions.ValidateTask(task); list.Add(task);
            }
            return [.. list];
        }
        finally { gate.Release(); }
    }
    public async Task<bool> ClaimBootStartAsync(uint bootId, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            using var transaction = connection.BeginTransaction(); using var cmd = connection.CreateCommand(); cmd.Transaction = transaction;
            cmd.CommandText = "INSERT INTO host_startup(id,boot_id) VALUES(1,$boot) ON CONFLICT(id) DO UPDATE SET boot_id=$boot WHERE boot_id<>$boot;";
            cmd.Parameters.AddWithValue("$boot", (long)bootId);
            var first = cmd.ExecuteNonQuery() == 1;
            token.ThrowIfCancellationRequested(); transaction.Commit(); return first;
        }
        finally { gate.Release(); }
    }
    public async Task<AutomationTask> SaveAsync(AutomationTask task, CancellationToken token)
    {
        TaskDetectionExpressions.ValidateTask(task);
        await gate.WaitAsync(token);
        try
        {
            using var tx = connection.BeginTransaction(); using var cmd = connection.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "SELECT version FROM automation_tasks WHERE id=$id;"; cmd.Parameters.AddWithValue("$id", task.Id);
            var current = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
            if (current != task.Version) throw new InvalidOperationException("任务已在别处更改，请重新读取。");
            if (current == 0)
            {
                cmd.CommandText = "SELECT COUNT(*) FROM automation_tasks;";
                if (Convert.ToInt64(cmd.ExecuteScalar()) >= AutomationTaskLimits.MaximumTasks) throw new InvalidOperationException("最多保存 128 个扩展任务。");
            }
            var saved = task with { Version = checked(current + 1) };
            cmd.CommandText = "INSERT INTO automation_tasks VALUES($id,$version,$config) ON CONFLICT(id) DO UPDATE SET version=$version,configuration=$config;";
            cmd.Parameters.AddWithValue("$version", saved.Version); cmd.Parameters.AddWithValue("$config", JsonSerializer.Serialize(saved)); cmd.ExecuteNonQuery();
            if (task.SchemaVersion >= 3) { cmd.CommandText = "PRAGMA user_version;"; var version = Convert.ToInt32(cmd.ExecuteScalar()); cmd.CommandText = "PRAGMA user_version=" + Math.Max(version, task.SchemaVersion) + ";"; cmd.ExecuteNonQuery(); }
            token.ThrowIfCancellationRequested(); tx.Commit(); return saved;
        }
        finally { gate.Release(); }
    }
    public async Task DeleteAsync(string id, long version, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            using var cmd = connection.CreateCommand(); cmd.CommandText = "DELETE FROM automation_tasks WHERE id=$id AND version=$version;";
            cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$version", version); token.ThrowIfCancellationRequested();
            if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("任务已在别处更改，请重新读取。");
        }
        finally { gate.Release(); }
    }
    public void Dispose() { connection.Dispose(); gate.Dispose(); }
}
