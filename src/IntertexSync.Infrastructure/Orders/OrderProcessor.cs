using IntertexSync.Core.Contracts;
using IntertexSync.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntertexSync.Infrastructure.Orders;

/// <summary>
/// Оркестрация жизненного цикла заказа: KeyCRM вебхук → 1С операции → обновление KeyCRM.
/// Все шаги идемпотентны (ключ = операция+order+checksum). Ошибки не роняют цикл: заказ
/// уходит в «Помилка синхронізації» с причиной. Валюта — только USD (DEC-008).
/// В dry-run: X-Dry-Run в 1С + запись в KeyCRM только в лог (ГЕЙТ R-12/go-live).
/// </summary>
public sealed class OrderProcessor
{
    private readonly IKeyCrmClient _keyCrm;
    private readonly I1CConnector _oneC;
    private readonly IConflictLog _conflicts;
    private readonly OrderSyncOptions _options;
    private readonly ILogger<OrderProcessor> _log;

    private const string OrderInclude = "buyer,products.offer,manager,custom_fields,payments";

    public OrderProcessor(
        IKeyCrmClient keyCrm, I1CConnector oneC, IConflictLog conflicts,
        IOptions<OrderSyncOptions> options, ILogger<OrderProcessor> log)
    {
        _keyCrm = keyCrm;
        _oneC = oneC;
        _conflicts = conflicts;
        _options = options.Value;
        _log = log;
    }

    /// <summary>Резерв: клиент → заказ покупателя → резерв → статус «Зарезервовано» либо «Помилка».</summary>
    public async Task<OrderProcessResult> ReserveAsync(long orderId, CancellationToken ct = default)
    {
        var view = await FetchAsync(orderId, ct);

        if (view.Currency != "USD")
            return await FailAsync(orderId, "reserve", OrderOutcome.CurrencyNotSupported,
                $"Валюта {view.Currency} ≠ USD — требуется ручной пересчёт (Prom, DEC-008)", ct);

        var invalid = ValidateItems(view);
        if (invalid is not null)
            return await FailAsync(orderId, "reserve", OrderOutcome.ValidationFailed, invalid, ct);

        var wh = RequireWarehouse();
        var dry = _options.DryRun;

        // 1. Контрагент (анти-дубли в 1С), идемпотентно по buyer id.
        var customer = await _oneC.UpsertCustomerAsync(
            new CustomerRequest(view.Buyer.Id, view.Buyer.FullName, view.Buyer.Phones, view.Buyer.Emails, null),
            idempotencyKey: $"cust:{view.Buyer.Id}", dryRun: dry, ct);

        // 2. Заказ покупателя, идемпотентно по order id.
        var order = new OrderRequest(orderId, customer.Guid, wh, view.ManagerId, view.Currency,
            Comment: $"KeyCRM #{orderId}", view.Items, view.ItemsChecksum);
        var doc = await _oneC.UpsertOrderAsync(order, idempotencyKey: $"order:{orderId}", dryRun: dry, ct);

        // 3. Резерв (без частичных: при нехватке — исключение с деталями).
        try
        {
            await _oneC.ReserveAsync(orderId, wh, idempotencyKey: $"reserve:{orderId}:{view.ItemsChecksum}", dryRun: dry, ct);
        }
        catch (Sync1CException ex) when (ex.Code == Sync1CErrorCode.InsufficientStock)
        {
            var reason = "Недостатньо залишку: " + string.Join("; ", ex.Details);
            return await FailAsync(orderId, "reserve", OrderOutcome.InsufficientStock, reason, ct);
        }
        catch (Sync1CException ex)
        {
            return await FailAsync(orderId, "reserve", OrderOutcome.OneCError, $"{ex.Code}: {ex.Message}", ct);
        }

        // 4. Успех → статус «Зарезервовано» + номер документа 1С.
        await UpdateKeyCrmAsync(orderId, _options.StatusReserved,
            new Dictionary<string, string?> { [_options.CfOneCDocNumber] = doc.Number }, ct);

        _log.LogInformation("Заказ {Order} зарезервирован (1С {Doc}), dryRun={Dry}", orderId, doc.Number, dry);
        return new OrderProcessResult(orderId, "reserve", OrderOutcome.Success,
            _options.StatusReserved, doc.Number, doc.Guid, null, dry);
    }

    /// <summary>Реализация (списание): сверка состава по чексумме → провести → «Готово до відправки».</summary>
    public async Task<OrderProcessResult> ShipAsync(long orderId, CancellationToken ct = default)
    {
        var view = await FetchAsync(orderId, ct);
        var dry = _options.DryRun;
        try
        {
            var doc = await _oneC.ShipAsync(orderId, view.ItemsChecksum, idempotencyKey: $"ship:{orderId}", dryRun: dry, ct);
            await UpdateKeyCrmAsync(orderId, _options.StatusReadyToShip,
                new Dictionary<string, string?> { [_options.CfOneCDocNumber] = doc.Number }, ct);
            _log.LogInformation("Заказ {Order} реализован (1С {Doc}), dryRun={Dry}", orderId, doc.Number, dry);
            return new OrderProcessResult(orderId, "ship", OrderOutcome.Success,
                _options.StatusReadyToShip, doc.Number, doc.Guid, null, dry);
        }
        catch (Sync1CException ex)
        {
            var outcome = ex.Code == Sync1CErrorCode.OrderModified ? OrderOutcome.ValidationFailed : OrderOutcome.OneCError;
            return await FailAsync(orderId, "ship", outcome, $"{ex.Code}: {ex.Message}", ct);
        }
    }

    /// <summary>Отмена до реализации: снять резерв (идемпотентно).</summary>
    public async Task<OrderProcessResult> CancelAsync(long orderId, CancellationToken ct = default)
    {
        var dry = _options.DryRun;
        // Если уже есть реализация — резерв снимать нельзя, нужен возврат (отдельный процесс).
        var state = await _oneC.GetOrderStateAsync(orderId, ct);
        if (state.Realization is not null)
        {
            var msg = $"Отмена после реализации {state.Realization.Number}: требуется документ возврата, не снятие резерва (ТЗ п.13)";
            await _conflicts.WriteAsync("order", orderId.ToString(), msg, null, ct);
            return await FailAsync(orderId, "cancel", OrderOutcome.OneCError, msg, ct);
        }

        await _oneC.UnreserveAsync(orderId, idempotencyKey: $"unreserve:{orderId}", ct);
        _log.LogInformation("Заказ {Order}: резерв снят, dryRun={Dry}", orderId, dry);
        return new OrderProcessResult(orderId, "cancel", OrderOutcome.Success, null, null, null, null, dry);
    }

    /// <summary>Регистрация оплат заказа в 1С (идемпотентно по payment id).</summary>
    public async Task<OrderProcessResult> RegisterPaymentsAsync(long orderId, CancellationToken ct = default)
    {
        var doc = await _keyCrm.GetOrderAsync(orderId, "payments", ct);
        var root = doc.RootElement;
        if (root.TryGetProperty("data", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.Object) root = d;

        var dry = _options.DryRun;
        var count = 0;
        if (root.TryGetProperty("payments", out var pays) && pays.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var p in pays.EnumerateArray())
            {
                if (p.TryGetProperty("is_expense", out var exp) && exp.ValueKind == System.Text.Json.JsonValueKind.True) continue;
                var payId = p.GetProperty("id").GetInt64();
                var amount = p.TryGetProperty("actual_amount", out var a) ? a.GetDecimal() : 0m;
                var currency = p.TryGetProperty("actual_currency", out var cu) ? cu.GetString() ?? "USD" : "USD";
                if (currency != "USD")
                {
                    await _conflicts.WriteAsync("payment", payId.ToString(), $"Оплата {payId} в {currency} ≠ USD — пропущена (DEC-008)", null, ct);
                    continue;
                }
                var methodId = p.TryGetProperty("payment_method_id", out var m) ? m.GetInt32().ToString() : "other";
                await _oneC.RegisterPaymentAsync(
                    new PaymentRequest(payId, orderId, amount, currency, methodId, DateTime.UtcNow),
                    idempotencyKey: $"payment:{payId}", dryRun: dry, ct);
                count++;
            }
        }
        _log.LogInformation("Заказ {Order}: зарегистрировано оплат {Count}, dryRun={Dry}", orderId, count, dry);
        return new OrderProcessResult(orderId, "payment", OrderOutcome.Success, null, null, null, null, dry);
    }

    // ---- helpers ----

    private async Task<OrderView> FetchAsync(long orderId, CancellationToken ct)
    {
        var doc = await _keyCrm.GetOrderAsync(orderId, OrderInclude, ct);
        return KeyCrmOrderMapper.Map(doc);
    }

    private static string? ValidateItems(OrderView v)
    {
        if (v.Items.Count == 0) return "Пустой состав заказа";
        foreach (var i in v.Items)
        {
            if (string.IsNullOrWhiteSpace(i.Sku)) return $"Позиция без SKU: «{i.Sku}»";
            if (i.Quantity <= 0) return $"Некорректное количество {i.Quantity} по SKU {i.Sku}";
        }
        return null;
    }

    private string RequireWarehouse()
    {
        if (!_options.DryRun && string.IsNullOrWhiteSpace(_options.DefaultWarehouseGuid))
            throw new InvalidOperationException("OrderSync: для боевого режима нужен DefaultWarehouseGuid (склад отгрузки, DEC-010).");
        return _options.DefaultWarehouseGuid;
    }

    private async Task<OrderProcessResult> FailAsync(long orderId, string action, OrderOutcome outcome, string reason, CancellationToken ct)
    {
        _log.LogWarning("Заказ {Order} [{Action}] → {Outcome}: {Reason}", orderId, action, outcome, reason);
        await _conflicts.WriteAsync("order", orderId.ToString(), $"[{action}] {outcome}: {reason}", null, ct);
        await UpdateKeyCrmAsync(orderId, _options.StatusSyncError,
            new Dictionary<string, string?> { [_options.CfSyncError] = reason }, ct);
        return new OrderProcessResult(orderId, action, outcome, _options.StatusSyncError, null, null, reason, _options.DryRun);
    }

    /// <summary>Обновление статуса и служебных полей заказа в KeyCRM. В dry-run — только лог.</summary>
    private async Task UpdateKeyCrmAsync(long orderId, int statusId, IReadOnlyDictionary<string, string?> customFields, CancellationToken ct)
    {
        var fields = customFields.Where(kv => !string.IsNullOrEmpty(kv.Key) && kv.Value is not null)
            .Select(kv => new { uuid = kv.Key, value = kv.Value }).ToArray();

        if (_options.DryRun)
        {
            _log.LogInformation("  [dry-run] KeyCRM order {Order}: status_id={Status}, custom_fields={Fields}",
                orderId, statusId, string.Join(",", fields.Select(f => $"{f.uuid}={f.value}")));
            return;
        }

        // Боевой режим: PUT /order/{id}. Точную схему custom_fields подтвердить на go-live.
        object payload = statusId > 0
            ? new { status_id = statusId, custom_fields = fields }
            : new { custom_fields = fields };
        await _keyCrm.UpdateOrderAsync(orderId, payload, ct);
    }
}
