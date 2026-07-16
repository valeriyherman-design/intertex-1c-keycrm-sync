using IntertexSync.Core.Contracts;
using Microsoft.Data.Sqlite;

namespace IntertexSync.Infrastructure.Storage;

/// <summary>
/// Единое SQLite-хранилище сервиса (WAL): очередь событий, идемпотентность,
/// таблицы соответствий, журнал конфликтов, снапшот остатков, записанных в KeyCRM (LIM-03).
/// </summary>
public sealed class SqliteStore : IEventQueue, IIdempotencyStore, IMappingStore, IConflictLog, IDisposable
{
    private readonly string _connectionString;
    // Экспоненциальный backoff: 30с, 2м, 8м, 32м, 2ч; далее Dead.
    internal static readonly TimeSpan[] RetryDelays =
    {
        TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(8),
        TimeSpan.FromMinutes(32), TimeSpan.FromHours(2),
    };
    public int MaxAttempts => RetryDelays.Length + 1;

    public SqliteStore(string dbPath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
        Initialize();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private void Initialize()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;

            CREATE TABLE IF NOT EXISTS events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                type TEXT NOT NULL,
                payload TEXT NOT NULL,
                dedup_key TEXT NOT NULL UNIQUE,
                order_key TEXT NULL,
                status INTEGER NOT NULL DEFAULT 0,
                attempts INTEGER NOT NULL DEFAULT 0,
                last_error TEXT NULL,
                created_at TEXT NOT NULL,
                next_attempt_at TEXT NULL,
                processed_at TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_events_status ON events(status, next_attempt_at);
            CREATE INDEX IF NOT EXISTS ix_events_order ON events(order_key, status);

            CREATE TABLE IF NOT EXISTS idempotency (
                key TEXT PRIMARY KEY,
                result TEXT NOT NULL,
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS mappings (
                kind TEXT NOT NULL,
                keycrm_id TEXT NOT NULL,
                one_c_value TEXT NOT NULL,
                comment TEXT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY (kind, keycrm_id)
            );

            CREATE TABLE IF NOT EXISTS conflicts (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                entity TEXT NOT NULL,
                entity_id TEXT NOT NULL,
                description TEXT NOT NULL,
                data TEXT NULL,
                created_at TEXT NOT NULL
            );

            -- Снапшот остатков, записанных нами в KeyCRM по каждому складу (LIM-03:
            -- API KeyCRM не отдаёт пер-складское чтение, поэтому храним last-pushed сами).
            CREATE TABLE IF NOT EXISTS pushed_stocks (
                warehouse_id INTEGER NOT NULL,
                sku TEXT NOT NULL,
                quantity TEXT NOT NULL,
                pushed_at TEXT NOT NULL,
                PRIMARY KEY (warehouse_id, sku)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    // ---------- IEventQueue ----------

    public async Task<bool> EnqueueAsync(SyncEvent evt, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO events(type, payload, dedup_key, order_key, status, attempts, created_at)
            VALUES ($type, $payload, $dedup, $order, 0, 0, $now)
            ON CONFLICT(dedup_key) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("$type", evt.Type);
        cmd.Parameters.AddWithValue("$payload", evt.PayloadJson);
        cmd.Parameters.AddWithValue("$dedup", evt.DedupKey);
        cmd.Parameters.AddWithValue("$order", (object?)evt.OrderKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<SyncEvent?> DequeueAsync(CancellationToken ct = default)
    {
        await using var c = Open();
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);

        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        // Берём старейшее готовое событие, чей заказ сейчас не обрабатывается.
        cmd.CommandText = """
            SELECT id, type, payload, dedup_key, order_key, status, attempts, last_error, created_at
            FROM events e
            WHERE e.status = 0
              AND (e.next_attempt_at IS NULL OR e.next_attempt_at <= $now)
              AND (e.order_key IS NULL OR NOT EXISTS (
                    SELECT 1 FROM events p WHERE p.order_key = e.order_key AND p.status = 1))
            ORDER BY e.id LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));

        SyncEvent? evt = null;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            if (await r.ReadAsync(ct))
            {
                evt = new SyncEvent
                {
                    Id = r.GetInt64(0),
                    Type = r.GetString(1),
                    PayloadJson = r.GetString(2),
                    DedupKey = r.GetString(3),
                    OrderKey = r.IsDBNull(4) ? null : r.GetString(4),
                    Status = SyncEventStatus.Processing,
                    Attempts = r.GetInt32(6),
                    LastError = r.IsDBNull(7) ? null : r.GetString(7),
                    CreatedAtUtc = DateTime.Parse(r.GetString(8)).ToUniversalTime(),
                };
            }
        }

        if (evt is null) { await tx.CommitAsync(ct); return null; }

        await using var upd = c.CreateCommand();
        upd.Transaction = tx;
        upd.CommandText = "UPDATE events SET status = 1, attempts = attempts + 1 WHERE id = $id;";
        upd.Parameters.AddWithValue("$id", evt.Id);
        await upd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);

        evt.Attempts += 1;
        return evt;
    }

    public async Task MarkDoneAsync(long id, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE events SET status = 2, processed_at = $now, last_error = NULL WHERE id = $id;";
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkFailedAsync(long id, string error, bool retryable, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var read = c.CreateCommand();
        read.CommandText = "SELECT attempts FROM events WHERE id = $id;";
        read.Parameters.AddWithValue("$id", id);
        var attempts = Convert.ToInt32(await read.ExecuteScalarAsync(ct) ?? 0);

        await using var cmd = c.CreateCommand();
        if (!retryable || attempts >= MaxAttempts)
        {
            // Не-повторяемая бизнес-ошибка или исчерпаны попытки → Dead (разбор вручную).
            cmd.CommandText = "UPDATE events SET status = 4, last_error = $err, processed_at = $now WHERE id = $id;";
        }
        else
        {
            var delay = RetryDelays[Math.Min(attempts - 1, RetryDelays.Length - 1)];
            cmd.CommandText = "UPDATE events SET status = 0, last_error = $err, next_attempt_at = $next WHERE id = $id;";
            cmd.Parameters.AddWithValue("$next", DateTime.UtcNow.Add(delay).ToString("O"));
        }
        cmd.Parameters.AddWithValue("$err", error);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<QueueStats> GetStatsAsync(CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT
              SUM(CASE WHEN status = 0 THEN 1 ELSE 0 END),
              SUM(CASE WHEN status = 1 THEN 1 ELSE 0 END),
              SUM(CASE WHEN status = 3 THEN 1 ELSE 0 END),
              SUM(CASE WHEN status = 4 THEN 1 ELSE 0 END),
              MAX(CASE WHEN status = 2 THEN processed_at END)
            FROM events;
            """;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new QueueStats(
            r.IsDBNull(0) ? 0 : r.GetInt32(0),
            r.IsDBNull(1) ? 0 : r.GetInt32(1),
            r.IsDBNull(2) ? 0 : r.GetInt32(2),
            r.IsDBNull(3) ? 0 : r.GetInt32(3),
            r.IsDBNull(4) ? null : DateTime.Parse(r.GetString(4)).ToUniversalTime());
    }

    public async Task<bool> RetryAsync(long id, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            UPDATE events SET status = 0, attempts = 0, next_attempt_at = NULL
            WHERE id = $id AND status IN (3, 4);
            """;
        cmd.Parameters.AddWithValue("$id", id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    // ---------- IIdempotencyStore ----------

    public async Task<string?> TryGetResultAsync(string key, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT result FROM idempotency WHERE key = $k;";
        cmd.Parameters.AddWithValue("$k", key);
        return (string?)await cmd.ExecuteScalarAsync(ct);
    }

    public async Task SaveResultAsync(string key, string resultJson, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO idempotency(key, result, created_at) VALUES ($k, $r, $now)
            ON CONFLICT(key) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$r", resultJson);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---------- IMappingStore ----------

    public async Task<string?> GetAsync(string kind, string keycrmId, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT one_c_value FROM mappings WHERE kind = $kind AND keycrm_id = $id;";
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$id", keycrmId);
        return (string?)await cmd.ExecuteScalarAsync(ct);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync(string kind, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT keycrm_id, one_c_value FROM mappings WHERE kind = $kind;";
        cmd.Parameters.AddWithValue("$kind", kind);
        var result = new Dictionary<string, string>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) result[r.GetString(0)] = r.GetString(1);
        return result;
    }

    public async Task SetAsync(string kind, string keycrmId, string oneCValue, string? comment = null, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO mappings(kind, keycrm_id, one_c_value, comment, updated_at)
            VALUES ($kind, $id, $val, $comment, $now)
            ON CONFLICT(kind, keycrm_id) DO UPDATE SET one_c_value = $val, comment = $comment, updated_at = $now;
            """;
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$id", keycrmId);
        cmd.Parameters.AddWithValue("$val", oneCValue);
        cmd.Parameters.AddWithValue("$comment", (object?)comment ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---------- IConflictLog ----------

    public async Task WriteAsync(string entity, string entityId, string description, string? dataJson = null, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO conflicts(entity, entity_id, description, data, created_at)
            VALUES ($e, $eid, $d, $data, $now);
            """;
        cmd.Parameters.AddWithValue("$e", entity);
        cmd.Parameters.AddWithValue("$eid", entityId);
        cmd.Parameters.AddWithValue("$d", description);
        cmd.Parameters.AddWithValue("$data", (object?)dataJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<ConflictRecord>> GetRecentAsync(int limit = 100, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, entity, entity_id, description, data, created_at FROM conflicts ORDER BY id DESC LIMIT $n;";
        cmd.Parameters.AddWithValue("$n", limit);
        var list = new List<ConflictRecord>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new ConflictRecord(
                r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                DateTime.Parse(r.GetString(5)).ToUniversalTime()));
        }
        return list;
    }

    public void Dispose() => SqliteConnection.ClearAllPools();
}
