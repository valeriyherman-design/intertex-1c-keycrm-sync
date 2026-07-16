using System.Globalization;
using System.Text.Json;
using IntertexSync.Core.Contracts;
using IntertexSync.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntertexSync.Infrastructure.Orders;

/// <summary>
/// Оркестрация жизненного цикла заказа: KeyCRM вебхук → 1С операции → обновление KeyCRM.
/// Все шаги идемпотентны. Бизнес-ошибки 1С (нехватка, изменён состав) → «Помилка синхронізації»
/// с причиной; ТРАНЗИЕНТНЫЕ ошибки 1С (Locked/InternalError) пробрасываются → повтор в QueueWorker.
/// Валюта — только USD (DEC-008): не-USD источники и явная не-USD валюта → «Помилка».
/// В dry-run: X-Dry-Run в 1С + запись в KeyCRM только в лог (ГЕЙТ R-12/go-live).
/// </summary>
public sealed class OrderProcessor
{
    private readonly IKeyCrmClient _keyCrm;
    private readonly I1CConnector _oneC;
    private readonly IConflictLog _conflicts;
    private readonly IMappingStore _mappings;
    private readonly OrderSyncOptions _options;
    private readonly ILogger<OrderProcessor> _log;

    private const string OrderInclude = "buyer,products.offer,manager,custom_fields,payments";
    private const string ReserveChecksumKind = "reserve_checksum"; // last-reserved состав по заказу

    public OrderProcessor(
        IKeyCrmClient keyCrm, I1CConnector oneC, IConflictLog conflicts, IMappingStore mappings,
        IOptions<OrderSyncOptions> options, ILogger<OrderProcessor> log)
    {
        _keyCrm = keyCrm;
        _oneC = oneC;
        _conflicts = conflicts;
        _mappings = mappings;
        _options = options.Value;
        _log = log;
    }

    /// <summary>Резерв: клиент → заказ покупателя → резерв → «Зарезервовано» либо «Помилка».
    /// Замена-к-цели: при изменении состава старый резерв снимается перед новым (без накопления).</summary>
    public async Task<OrderProcessResult> ReserveAsync(long orderId, CancellationToken ct = default)
    {
        var view = await FetchAsync(orderId, ct);

        if (!IsUsd(view))
            return await FailAsync(orderId, "reserve", OrderOutcome.CurrencyNotSupported,
                $"Валюта {view.Currency} / источник {view.SourceId} ≠ USD — ручной пересчёт (Prom, DEC-008)", ct);

        var invalid = ValidateItems(view);
        if (invalid is not null)
            return await FailAsync(orderId, "reserve", OrderOutcome.ValidationFailed, invalid, ct);

        var wh = RequireWarehouse();
        var dry = _options.DryRun;

        try
        {
            // Идемпотентность/замена-к-цели: если состав уже зарезервирован тем же checksum — ничего не делаем.
            var lastChecksum = await _mappings.GetAsync(ReserveChecksumKind, orderId.ToString(), ct);
            if (lastChecksum == view.ItemsChecksum)
            {
                _log.LogInformation("Заказ {Order}: резерв актуален (состав не изменился) — повтор пропущен", orderId);
                return new OrderProcessResult(orderId, "reserve", OrderOutcome.Success, _options.StatusReserved, null, null, null, dry);
            }

            // 1. Контрагент (анти-дубли), идемпотентно по buyer id.
            var customer = await _oneC.UpsertCustomerAsync(
                new CustomerRequest(view.Buyer.Id, view.Buyer.FullName, view.Buyer.Phones, view.Buyer.Emails, null),
                idempotencyKey: $"cust:{view.Buyer.Id}", dryRun: dry, ct);

            // 2. Заказ покупателя (идемпотентно; на реальной 1С обновляет состав).
            var order = new OrderRequest(orderId, customer.Guid, wh, view.ManagerId, view.Currency,
                Comment: $"KeyCRM #{orderId}", view.Items, view.ItemsChecksum);
            var doc = await _oneC.UpsertOrderAsync(order, idempotencyKey: $"order:{orderId}:{view.ItemsChecksum}", dryRun: dry, ct);

            // 3. Состав изменился → снять прежний резерв, чтобы новый не накапливался.
            if (lastChecksum is not null)
                await _oneC.UnreserveAsync(orderId, idempotencyKey: $"unreserve:{orderId}:{lastChecksum}", dryRun: dry, ct);

            // 4. Резерв (без частичных).
            await _oneC.ReserveAsync(orderId, wh, idempotencyKey: $"reserve:{orderId}:{view.ItemsChecksum}", dryRun: dry, ct);

            if (!dry)
                await _mappings.SetAsync(ReserveChecksumKind, orderId.ToString(), view.ItemsChecksum, ct: ct);

            await UpdateKeyCrmAsync(orderId, _options.StatusReserved,
                new Dictionary<string, string?> { [_options.CfOneCDocNumber] = doc.Number }, ct);

            _log.LogInformation("Заказ {Order} зарезервирован (1С {Doc}), dryRun={Dry}", orderId, doc.Number, dry);
            return new OrderProcessResult(orderId, "reserve", OrderOutcome.Success,
                _options.StatusReserved, doc.Number, doc.Guid, null, dry);
        }
        catch (Sync1CException ex) when (ex.Code == Sync1CErrorCode.InsufficientStock)
        {
            return await FailAsync(orderId, "reserve", OrderOutcome.InsufficientStock,
                "Недостатньо залишку: " + string.Join("; ", ex.Details), ct);
        }
        catch (Sync1CException ex) when (!ex.Retryable)
        {
            return await FailAsync(orderId, "reserve", OrderOutcome.OneCError, $"{ex.Code}: {ex.Message}", ct);
        }
        // Транзиентные (Locked/InternalError) — пробрасываются в QueueWorker для повтора с backoff.
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
        catch (Sync1CException ex) when (ex.Code == Sync1CErrorCode.OrderModified)
        {
            return await FailAsync(orderId, "ship", OrderOutcome.ValidationFailed, $"{ex.Code}: {ex.Message}", ct);
        }
        catch (Sync1CException ex) when (!ex.Retryable)
        {
            return await FailAsync(orderId, "ship", OrderOutcome.OneCError, $"{ex.Code}: {ex.Message}", ct);
        }
        // Транзиентные — пробрасываются для повтора.
    }

    /// <summary>Отмена до реализации: снять резерв (идемпотентно). После реализации — нужен возврат.</summary>
    public async Task<OrderProcessResult> CancelAsync(long orderId, CancellationToken ct = default)
    {
        var dry = _options.DryRun;
        var state = await _oneC.GetOrderStateAsync(orderId, ct);
        if (state.Realization is not null)
        {
            var msg = $"Отмена после реализации {state.Realization.Number}: требуется документ возврата, не снятие резерва (ТЗ п.13)";
            await _conflicts.WriteAsync("order", orderId.ToString(), msg, null, ct);
            return await FailAsync(orderId, "cancel", OrderOutcome.OneCError, msg, ct);
        }

        await _oneC.UnreserveAsync(orderId, idempotencyKey: $"unreserve:{orderId}:cancel", dryRun: dry, ct);
        if (!dry) await _mappings.SetAsync(ReserveChecksumKind, orderId.ToString(), "", ct: ct); // резерв снят
        _log.LogInformation("Заказ {Order}: резерв снят, dryRun={Dry}", orderId, dry);
        return new OrderProcessResult(orderId, "cancel", OrderOutcome.Success, null, null, null, null, dry);
    }

    /// <summary>Регистрация оплат заказа в 1С (идемпотентно по payment id). Толерантный разбор.</summary>
    public async Task<OrderProcessResult> RegisterPaymentsAsync(long orderId, CancellationToken ct = default)
    {
        var doc = await _keyCrm.GetOrderAsync(orderId, "payments", ct);
        var root = doc.RootElement;
        if (root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object) root = d;

        var dry = _options.DryRun;
        var count = 0;
        if (root.TryGetProperty("payments", out var pays) && pays.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in pays.EnumerateArray())
            {
                try
                {
                    if (p.TryGetProperty("is_expense", out var exp) && exp.ValueKind == JsonValueKind.True) continue;
                    var payId = GetLong(p, "id");
                    if (payId is null) { await _conflicts.WriteAsync("payment", orderId.ToString(), "Оплата без id — пропущена", p.ToString(), ct); continue; }
                    var amount = GetDecimal(p, "actual_amount") ?? GetDecimal(p, "amount") ?? 0m;
                    var currency = GetString(p, "actual_currency") ?? GetString(p, "source_currency") ?? "USD";
                    if (currency != "USD")
                    {
                        await _conflicts.WriteAsync("payment", payId.ToString()!, $"Оплата {payId} в {currency} ≠ USD — пропущена (DEC-008)", null, ct);
                        continue;
                    }
                    var methodId = (GetLong(p, "payment_method_id")?.ToString()) ?? "other";
                    await _oneC.RegisterPaymentAsync(
                        new PaymentRequest(payId.Value, orderId, amount, currency, methodId, DateTime.UtcNow),
                        idempotencyKey: $"payment:{payId}", dryRun: dry, ct);
                    count++;
                }
                catch (Sync1CException ex) when (!ex.Retryable)
                {
                    await _conflicts.WriteAsync("payment", orderId.ToString(), $"Ошибка оплаты: {ex.Code}: {ex.Message}", null, ct);
                }
            }
        }
        _log.LogInformation("Заказ {Order}: зарегистрировано оплат {Count}, dryRun={Dry}", orderId, count, dry);
        return new OrderProcessResult(orderId, "payment", OrderOutcome.Success, null, null, null, null, dry);
    }

    // ---- helpers ----

    private async Task<OrderView> FetchAsync(long orderId, CancellationToken ct)
        => KeyCrmOrderMapper.Map(await _keyCrm.GetOrderAsync(orderId, OrderInclude, ct));

    /// <summary>USD только если и валюта USD, и источник не в списке не-USD (Prom). Fail-closed по источнику.</summary>
    private bool IsUsd(OrderView v) => v.Currency == "USD" && !_options.NonUsdSourceIds.Contains(v.SourceId);

    private static string? ValidateItems(OrderView v)
    {
        if (v.Items.Count == 0) return "Пустой состав заказа";
        foreach (var i in v.Items)
        {
            if (string.IsNullOrWhiteSpace(i.Sku)) return "Позиция без SKU";
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

        object payload = statusId > 0 ? new { status_id = statusId, custom_fields = fields } : new { custom_fields = fields };
        await _keyCrm.UpdateOrderAsync(orderId, payload, ct);
    }

    private static long? GetLong(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64()
        : e.TryGetProperty(name, out var s) && s.ValueKind == JsonValueKind.String && long.TryParse(s.GetString(), out var l) ? l : null;

    private static decimal? GetDecimal(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal()
        : e.TryGetProperty(name, out var s) && s.ValueKind == JsonValueKind.String && decimal.TryParse(s.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var dd) ? dd : null;

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
