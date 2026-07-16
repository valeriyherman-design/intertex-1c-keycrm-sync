using IntertexSync.Core.Models;

namespace IntertexSync.Core.Contracts;

/// <summary>Событие обмена в очереди (вебхук KeyCRM или внутренняя команда).</summary>
public sealed class SyncEvent
{
    public long Id { get; set; }
    public string Type { get; set; } = "";           // webhook.order_status, sync.stocks, ...
    public string PayloadJson { get; set; } = "";
    /// <summary>Ключ идемпотентности события: одинаковые вебхуки не дублируются в очереди.</summary>
    public string DedupKey { get; set; } = "";
    /// <summary>Ключ последовательности: события одного заказа обрабатываются строго по одному.</summary>
    public string? OrderKey { get; set; }
    public SyncEventStatus Status { get; set; } = SyncEventStatus.Pending;
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? NextAttemptAtUtc { get; set; }
    public DateTime? ProcessedAtUtc { get; set; }
}

public enum SyncEventStatus { Pending = 0, Processing = 1, Done = 2, Failed = 3, Dead = 4 }

public interface IEventQueue
{
    /// <summary>Ставит событие в очередь. Возвращает false, если DedupKey уже есть (повторный вебхук).</summary>
    Task<bool> EnqueueAsync(SyncEvent evt, CancellationToken ct = default);

    /// <summary>Забирает следующее событие к обработке. Не выдаёт событие заказа, у которого уже есть Processing (блокировка параллельной обработки одного заказа).</summary>
    Task<SyncEvent?> DequeueAsync(CancellationToken ct = default);

    Task MarkDoneAsync(long id, CancellationToken ct = default);

    /// <summary>Неуспех: планирует повтор с экспоненциальной задержкой либо переводит в Dead после maxAttempts.</summary>
    Task MarkFailedAsync(long id, string error, bool retryable, CancellationToken ct = default);

    Task<QueueStats> GetStatsAsync(CancellationToken ct = default);

    /// <summary>Ручной повтор мёртвого/ошибочного события (админ-панель).</summary>
    Task<bool> RetryAsync(long id, CancellationToken ct = default);
}

public sealed record QueueStats(int Pending, int Processing, int Failed, int Dead, DateTime? LastSuccessUtc);

public interface IIdempotencyStore
{
    /// <summary>Возвращает сохранённый результат операции, если она уже выполнялась с этим ключом.</summary>
    Task<string?> TryGetResultAsync(string key, CancellationToken ct = default);
    Task SaveResultAsync(string key, string resultJson, CancellationToken ct = default);
}

/// <summary>Таблицы соответствий: склады, статусы, методы оплаты, менеджеры, виды цен.</summary>
public interface IMappingStore
{
    Task<string?> GetAsync(string kind, string keycrmId, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, string>> GetAllAsync(string kind, CancellationToken ct = default);
    Task SetAsync(string kind, string keycrmId, string oneCValue, string? comment = null, CancellationToken ct = default);
}

public static class MappingKinds
{
    public const string Warehouse = "warehouse";       // keycrm warehouse_id -> 1C склад GUID
    public const string Status = "status";             // keycrm status_id -> действие интеграции
    public const string PaymentMethod = "payment";     // keycrm method id -> тип документа 1С
    public const string Manager = "manager";           // keycrm user id -> 1C пользователь
    public const string PriceType = "price_type";      // вид цены 1С -> прайс KeyCRM
}

/// <summary>Журнал конфликтов: расхождения не перезаписываются молча (ТЗ п.12).</summary>
public interface IConflictLog
{
    Task WriteAsync(string entity, string entityId, string description, string? dataJson = null, CancellationToken ct = default);
    Task<IReadOnlyList<ConflictRecord>> GetRecentAsync(int limit = 100, CancellationToken ct = default);
}

public sealed record ConflictRecord(long Id, string Entity, string EntityId, string Description, string? DataJson, DateTime CreatedAtUtc);
