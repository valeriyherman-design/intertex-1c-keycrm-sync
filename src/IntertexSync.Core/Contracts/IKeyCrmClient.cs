using System.Text.Json;

namespace IntertexSync.Core.Contracts;

/// <summary>
/// Клиент Open API KeyCRM (https://openapi.keycrm.app/v1).
/// Реализация обязана соблюдать лимит 60 запросов/мин (LIM-02) и backoff на 429/5xx.
/// </summary>
public interface IKeyCrmClient
{
    /// <summary>GET произвольного ресурса с пагинацией/фильтрами. path без ведущего '/'.</summary>
    Task<JsonDocument> GetAsync(string path, IReadOnlyDictionary<string, string>? query = null, CancellationToken ct = default);

    /// <summary>PUT /offers/stocks — запись остатков по складу (warehouse_id обязателен, LIM-03).</summary>
    Task PutStocksAsync(long warehouseId, IReadOnlyList<(string Sku, decimal Quantity)> stocks, CancellationToken ct = default);

    /// <summary>PUT /order/{id} — обновление заказа (статус, кастомные поля).</summary>
    Task UpdateOrderAsync(long orderId, object payload, CancellationToken ct = default);

    /// <summary>GET /order/{id} c include (products.offer, buyer, payments, shipping, custom_fields).</summary>
    Task<JsonDocument> GetOrderAsync(long orderId, string include, CancellationToken ct = default);
}
