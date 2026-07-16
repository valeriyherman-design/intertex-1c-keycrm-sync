using IntertexSync.Core.Models;

namespace IntertexSync.Core.Contracts;

/// <summary>
/// Логический контракт операций 1С (handover-1c/05_API_CONTRACT.md).
/// Транспорт (COM / HTTP) — деталь реализации драйвера (DEC-009).
/// Все операции записи идемпотентны по idempotencyKey.
/// </summary>
public interface I1CConnector
{
    Task<HealthInfo> HealthAsync(CancellationToken ct = default);

    Task<IReadOnlyList<Warehouse1C>> GetWarehousesAsync(CancellationToken ct = default);

    Task<Page<Product1C>> GetProductsAsync(DateTime? updatedSince, int page, int limit, CancellationToken ct = default);

    Task<Page<StockRow1C>> GetStocksAsync(string? warehouseGuid, DateTime? updatedSince, int page, int limit, CancellationToken ct = default);

    /// <summary>Найти или создать контрагента. Анти-дубли: keycrm_id → телефон → email.</summary>
    Task<CustomerResult> UpsertCustomerAsync(CustomerRequest request, string idempotencyKey, bool dryRun = false, CancellationToken ct = default);

    /// <summary>Создать/обновить ЗаказПокупателя (идемпотентно по KeycrmOrderId).</summary>
    Task<DocumentRef> UpsertOrderAsync(OrderRequest request, string idempotencyKey, bool dryRun = false, CancellationToken ct = default);

    /// <summary>Резерв всего состава. При нехватке — Sync1CException(INSUFFICIENT_STOCK) с деталями, фиктивный резерв не создаётся.</summary>
    Task<IReadOnlyList<StockRow1C>> ReserveAsync(long keycrmOrderId, string warehouseGuid, string idempotencyKey, bool dryRun = false, CancellationToken ct = default);

    /// <summary>Снятие резерва. Идемпотентно: повторное снятие не ошибка.</summary>
    Task<bool> UnreserveAsync(long keycrmOrderId, string idempotencyKey, CancellationToken ct = default);

    /// <summary>Создать и провести Реализацию. Сверяет itemsChecksum (ORDER_MODIFIED при расхождении).</summary>
    Task<DocumentRef> ShipAsync(long keycrmOrderId, string itemsChecksum, string idempotencyKey, bool dryRun = false, CancellationToken ct = default);

    /// <summary>Возврат от покупателя. Исходная реализация не удаляется.</summary>
    Task<DocumentRef> CreateReturnAsync(ReturnRequest request, string idempotencyKey, bool dryRun = false, CancellationToken ct = default);

    /// <summary>Регистрация оплаты (идемпотентно по KeycrmPaymentId).</summary>
    Task<DocumentRef> RegisterPaymentAsync(PaymentRequest request, string idempotencyKey, bool dryRun = false, CancellationToken ct = default);

    Task<OrderState1C> GetOrderStateAsync(long keycrmOrderId, CancellationToken ct = default);

    Task<IReadOnlyList<Transfer1C>> GetTransfersAsync(DateTime since, CancellationToken ct = default);
}
