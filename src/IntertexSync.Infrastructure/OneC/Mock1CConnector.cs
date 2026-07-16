using System.Collections.Concurrent;
using IntertexSync.Core.Contracts;
using IntertexSync.Core.Models;

namespace IntertexSync.Infrastructure.OneC;

/// <summary>
/// Мок 1С для разработки (macOS/Linux) и unit-тестов. Эмулирует учётное поведение:
/// остатки, резервы, идемпотентность, дефицит, валюту USD-only.
/// Поведение соответствует контракту 05_API_CONTRACT.md.
/// </summary>
public sealed class Mock1CConnector : I1CConnector
{
    private readonly ConcurrentDictionary<string, decimal> _stock = new();      // sku|wh -> qty
    private readonly ConcurrentDictionary<string, decimal> _reserved = new();   // sku|wh -> qty
    private readonly ConcurrentDictionary<long, OrderRequest> _orders = new();
    private readonly ConcurrentDictionary<long, string> _orderWarehouse = new();
    private readonly ConcurrentDictionary<long, DocumentRef> _realizations = new();
    private readonly ConcurrentDictionary<long, CustomerRequest> _customers = new();
    private readonly ConcurrentDictionary<string, string> _idempotency = new();
    private readonly ConcurrentDictionary<long, DocumentRef> _payments = new();
    private int _docCounter;

    public IReadOnlyList<Warehouse1C> Warehouses { get; } = new List<Warehouse1C>
    {
        new("wh-shop1-guid", "000000001", "Магазин №1", true),
        new("wh-shop2-guid", "000000002", "Магазин №2", true),
        new("wh-shop3-guid", "000000003", "Магазин №3", true),
        new("wh-shop4-guid", "000000004", "Магазин №4 (сборка)", true),
        new("wh-store1-guid", "000000005", "Склад №1", true),
        new("wh-store2-guid", "000000006", "Склад №2", true),
    };

    public void SetStock(string sku, string warehouseGuid, decimal qty) => _stock[$"{sku}|{warehouseGuid}"] = qty;
    /// <summary>Тестовая установка резерва напрямую (в т.ч. над остатком — для проверки зажима available&lt;0).</summary>
    public void SetReserved(string sku, string warehouseGuid, decimal qty) => _reserved[$"{sku}|{warehouseGuid}"] = qty;
    public decimal GetReserved(string sku, string warehouseGuid) => _reserved.GetValueOrDefault($"{sku}|{warehouseGuid}");

    public Task<HealthInfo> HealthAsync(CancellationToken ct = default)
        => Task.FromResult(new HealthInfo("ok", "Mock УТП для Украины 1.2.35.1", "0.1.0-mock", DateTime.UtcNow));

    public Task<IReadOnlyList<Warehouse1C>> GetWarehousesAsync(CancellationToken ct = default)
        => Task.FromResult(Warehouses);

    public Task<Page<Product1C>> GetProductsAsync(DateTime? updatedSince, int page, int limit, CancellationToken ct = default)
        => Task.FromResult(new Page<Product1C>(Array.Empty<Product1C>(), page, limit, 0));

    public Task<Page<StockRow1C>> GetStocksAsync(string? warehouseGuid, DateTime? updatedSince, int page, int limit, CancellationToken ct = default)
    {
        var rows = _stock
            .Where(kv => warehouseGuid is null || kv.Key.EndsWith($"|{warehouseGuid}", StringComparison.Ordinal))
            .Select(kv =>
            {
                var parts = kv.Key.Split('|');
                var reserved = _reserved.GetValueOrDefault(kv.Key);
                return new StockRow1C(parts[0], parts[1], kv.Value, reserved, kv.Value - reserved);
            })
            .ToList();
        return Task.FromResult(new Page<StockRow1C>(rows, page, limit, rows.Count));
    }

    public Task<CustomerResult> UpsertCustomerAsync(CustomerRequest request, string idempotencyKey, bool dryRun = false, CancellationToken ct = default)
    {
        var existed = _customers.ContainsKey(request.KeycrmBuyerId);
        if (!dryRun) _customers[request.KeycrmBuyerId] = request;
        return Task.FromResult(new CustomerResult($"ctr-{request.KeycrmBuyerId}", !existed, existed ? "keycrm_id" : "none"));
    }

    public Task<DocumentRef> UpsertOrderAsync(OrderRequest request, string idempotencyKey, bool dryRun = false, CancellationToken ct = default)
    {
        if (request.Currency != "USD")
            throw new Sync1CException(Sync1CErrorCode.CurrencyNotSupported, $"Валюта {request.Currency} не поддерживается (только USD, DEC-008)");
        if (request.Items.Count == 0)
            throw new Sync1CException(Sync1CErrorCode.ValidationError, "Пустой состав заказа");
        if (_realizations.ContainsKey(request.KeycrmOrderId) && !_orders[request.KeycrmOrderId].ItemsChecksum.Equals(request.ItemsChecksum, StringComparison.Ordinal))
            throw new Sync1CException(Sync1CErrorCode.OrderModified, "Заказ уже реализован, изменение состава запрещено");

        // Заказ сохраняется в моке и в dry-run — чтобы последующий dry-run резерв мог
        // осмысленно проверить доступность (репетиция). Реальные X-Dry-Run в 1С не пишут.
        _orders[request.KeycrmOrderId] = request;
        _orderWarehouse[request.KeycrmOrderId] = request.WarehouseGuid;
        return Task.FromResult(new DocumentRef($"ord-{request.KeycrmOrderId}", $"УТ-{request.KeycrmOrderId:D8}", DateTime.UtcNow, false));
    }

    public Task<IReadOnlyList<StockRow1C>> ReserveAsync(long keycrmOrderId, string warehouseGuid, string idempotencyKey, bool dryRun = false, CancellationToken ct = default)
    {
        // Идемпотентность: повторный резерв того же заказа — не дублируется.
        if (_idempotency.ContainsKey(idempotencyKey))
            return GetStocksForOrder(keycrmOrderId, warehouseGuid);

        if (!_orders.TryGetValue(keycrmOrderId, out var order))
            throw new Sync1CException(Sync1CErrorCode.OrderNotFound, $"Заказ {keycrmOrderId} не найден");

        // Сначала проверка ВСЕХ позиций (никаких частичных резервов).
        var deficits = new List<string>();
        foreach (var item in order.Items)
        {
            var key = $"{item.Sku}|{warehouseGuid}";
            var available = _stock.GetValueOrDefault(key) - _reserved.GetValueOrDefault(key);
            if (available < item.Quantity)
                deficits.Add($"{{\"sku\":\"{item.Sku}\",\"requested\":{item.Quantity},\"available\":{available}}}");
        }
        if (deficits.Count > 0)
            throw new Sync1CException(Sync1CErrorCode.InsufficientStock, "Недостатньо залишку", retryable: false, details: deficits);

        if (!dryRun)
        {
            foreach (var item in order.Items)
                _reserved.AddOrUpdate($"{item.Sku}|{warehouseGuid}", item.Quantity, (_, v) => v + item.Quantity);
            _idempotency[idempotencyKey] = "reserved";
        }
        return GetStocksForOrder(keycrmOrderId, warehouseGuid);
    }

    public Task<bool> UnreserveAsync(long keycrmOrderId, string idempotencyKey, CancellationToken ct = default)
    {
        if (!_orders.TryGetValue(keycrmOrderId, out var order)) return Task.FromResult(false);
        if (_idempotency.ContainsKey(idempotencyKey)) return Task.FromResult(true); // уже снят

        var wh = _orderWarehouse.GetValueOrDefault(keycrmOrderId, "");
        foreach (var item in order.Items)
        {
            var key = $"{item.Sku}|{wh}";
            _reserved.AddOrUpdate(key, 0, (_, v) => Math.Max(0, v - item.Quantity));
        }
        _idempotency[idempotencyKey] = "unreserved";
        return Task.FromResult(true);
    }

    public Task<DocumentRef> ShipAsync(long keycrmOrderId, string itemsChecksum, string idempotencyKey, bool dryRun = false, CancellationToken ct = default)
    {
        // Идемпотентность: повтор возвращает тот же документ, второго списания нет.
        if (_realizations.TryGetValue(keycrmOrderId, out var existing))
            return Task.FromResult(existing);

        if (!_orders.TryGetValue(keycrmOrderId, out var order))
            throw new Sync1CException(Sync1CErrorCode.OrderNotFound, $"Заказ {keycrmOrderId} не найден");
        if (!order.ItemsChecksum.Equals(itemsChecksum, StringComparison.Ordinal))
            throw new Sync1CException(Sync1CErrorCode.OrderModified, "Состав заказа изменился после резерва");

        var wh = _orderWarehouse.GetValueOrDefault(keycrmOrderId, "");
        if (!dryRun)
        {
            foreach (var item in order.Items)
            {
                var key = $"{item.Sku}|{wh}";
                _stock.AddOrUpdate(key, 0, (_, v) => v - item.Quantity);
                _reserved.AddOrUpdate(key, 0, (_, v) => Math.Max(0, v - item.Quantity));
            }
        }
        var doc = new DocumentRef($"real-{keycrmOrderId}", $"РН-{Interlocked.Increment(ref _docCounter):D8}", DateTime.UtcNow, true);
        if (!dryRun) _realizations[keycrmOrderId] = doc;
        return Task.FromResult(doc);
    }

    public Task<DocumentRef> CreateReturnAsync(ReturnRequest request, string idempotencyKey, bool dryRun = false, CancellationToken ct = default)
    {
        if (!_realizations.ContainsKey(request.KeycrmOrderId))
            throw new Sync1CException(Sync1CErrorCode.OrderNotFound, $"Реализация по заказу {request.KeycrmOrderId} не найдена");
        var wh = _orderWarehouse.GetValueOrDefault(request.KeycrmOrderId, "");
        if (!dryRun)
        {
            foreach (var item in request.Items)
                _stock.AddOrUpdate($"{item.Sku}|{wh}", item.Quantity, (_, v) => v + item.Quantity);
        }
        return Task.FromResult(new DocumentRef($"ret-{request.KeycrmOrderId}", $"ВП-{Interlocked.Increment(ref _docCounter):D8}", DateTime.UtcNow, true));
    }

    public Task<DocumentRef> RegisterPaymentAsync(PaymentRequest request, string idempotencyKey, bool dryRun = false, CancellationToken ct = default)
    {
        if (request.Currency != "USD")
            throw new Sync1CException(Sync1CErrorCode.CurrencyNotSupported, $"Валюта {request.Currency} не поддерживается");
        // Идемпотентность по keycrm_payment_id: дубль оплаты не создаётся.
        if (_payments.TryGetValue(request.KeycrmPaymentId, out var existing)) return Task.FromResult(existing);
        var doc = new DocumentRef($"pay-{request.KeycrmPaymentId}", $"ПКО-{Interlocked.Increment(ref _docCounter):D8}", DateTime.UtcNow, true);
        if (!dryRun) _payments[request.KeycrmPaymentId] = doc;
        return Task.FromResult(doc);
    }

    public Task<OrderState1C> GetOrderStateAsync(long keycrmOrderId, CancellationToken ct = default)
    {
        _orders.TryGetValue(keycrmOrderId, out var order);
        _realizations.TryGetValue(keycrmOrderId, out var real);
        var wh = _orderWarehouse.GetValueOrDefault(keycrmOrderId, "");
        var reservedTotal = order?.Items.Sum(i => Math.Min(i.Quantity, _reserved.GetValueOrDefault($"{i.Sku}|{wh}"))) ?? 0;
        return Task.FromResult(new OrderState1C(
            order is null ? null : new DocumentRef($"ord-{keycrmOrderId}", $"УТ-{keycrmOrderId:D8}", DateTime.UtcNow, false),
            new ReserveState(reservedTotal > 0, reservedTotal),
            real,
            Array.Empty<DocumentRef>(),
            Array.Empty<(string, decimal)>()));
    }

    public Task<IReadOnlyList<Transfer1C>> GetTransfersAsync(DateTime since, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Transfer1C>>(Array.Empty<Transfer1C>());

    private Task<IReadOnlyList<StockRow1C>> GetStocksForOrder(long orderId, string warehouseGuid)
    {
        var order = _orders[orderId];
        var rows = order.Items.Select(i =>
        {
            var key = $"{i.Sku}|{warehouseGuid}";
            var qty = _stock.GetValueOrDefault(key);
            var res = _reserved.GetValueOrDefault(key);
            return new StockRow1C(i.Sku, warehouseGuid, qty, res, qty - res);
        }).ToList();
        return Task.FromResult<IReadOnlyList<StockRow1C>>(rows);
    }
}
