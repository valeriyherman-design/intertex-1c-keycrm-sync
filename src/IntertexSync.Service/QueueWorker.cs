using System.Text.Json;
using IntertexSync.Core.Contracts;
using IntertexSync.Core.Models;
using IntertexSync.Infrastructure.Orders;
using Microsoft.Extensions.Options;

namespace IntertexSync.Service;

/// <summary>
/// Фоновый обработчик очереди. Берёт события по одному (события одного заказа —
/// строго последовательно, IEventQueue гарантирует), маршрутизирует по статусу на
/// действия OrderProcessor. Сбой → повтор с backoff; бизнес-ошибка 1С → без повтора.
/// </summary>
public sealed class QueueWorker : BackgroundService
{
    private readonly IEventQueue _queue;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<QueueWorker> _log;
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2);

    public QueueWorker(IEventQueue queue, IServiceScopeFactory scopes, ILogger<QueueWorker> log)
    {
        _queue = queue;
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("QueueWorker запущен");
        while (!stoppingToken.IsCancellationRequested)
        {
            SyncEvent? evt = null;
            try
            {
                evt = await _queue.DequeueAsync(stoppingToken);
                if (evt is null) { await Task.Delay(IdleDelay, stoppingToken); continue; }

                _log.LogInformation("Обработка события {Id} {Type} (попытка {Attempt})", evt.Id, evt.Type, evt.Attempts);
                await HandleAsync(evt, stoppingToken);
                await _queue.MarkDoneAsync(evt.Id, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // штатная остановка: событие останется Processing → подберётся после рестарта
            }
            catch (Sync1CException ex) when (evt is not null)
            {
                _log.LogWarning("Событие {Id}: бизнес-ошибка 1С {Code}: {Message}", evt.Id, ex.Code, ex.Message);
                await _queue.MarkFailedAsync(evt.Id, $"{ex.Code}: {ex.Message}", ex.Retryable, stoppingToken);
            }
            catch (Exception ex) when (evt is not null)
            {
                _log.LogError(ex, "Событие {Id}: непредвиденная ошибка", evt.Id);
                await _queue.MarkFailedAsync(evt.Id, ex.Message, retryable: true, stoppingToken);
            }
        }
        _log.LogInformation("QueueWorker остановлен");
    }

    private async Task HandleAsync(SyncEvent evt, CancellationToken ct)
    {
        var (orderId, statusId, isPayment) = ParseEvent(evt);
        if (orderId is null)
        {
            _log.LogInformation("Событие {Type} без order id — пропущено", evt.Type);
            return;
        }

        using var scope = _scopes.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<OrderProcessor>();
        var opts = scope.ServiceProvider.GetRequiredService<IOptions<OrderSyncOptions>>().Value;

        if (isPayment)
        {
            await processor.RegisterPaymentsAsync(orderId.Value, ct);
            return;
        }

        if (statusId is null)
        {
            _log.LogInformation("Заказ {Order}: событие без статуса — пропущено", orderId);
            return;
        }

        OrderProcessResult result;
        if (opts.ReserveTriggerStatuses.Contains(statusId.Value))
            result = await processor.ReserveAsync(orderId.Value, ct);
        else if (opts.ShipTriggerStatuses.Contains(statusId.Value))
            result = await processor.ShipAsync(orderId.Value, ct);
        else if (opts.CancelTriggerStatuses.Contains(statusId.Value))
            result = await processor.CancelAsync(orderId.Value, ct);
        else
        {
            _log.LogInformation("Заказ {Order}: статус {Status} не триггерит действий", orderId, statusId);
            return;
        }

        _log.LogInformation("Заказ {Order} [{Action}] → {Outcome}", result.OrderId, result.Action, result.Outcome);
    }

    /// <summary>Извлечь order id / status id / признак оплаты из полезной нагрузки вебхука.</summary>
    private (long? OrderId, int? StatusId, bool IsPayment) ParseEvent(SyncEvent evt)
    {
        var isPayment = evt.Type.Contains("payment", StringComparison.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(evt.PayloadJson);
            var root = doc.RootElement;
            var ctx = root.TryGetProperty("context", out var c) ? c : root;

            long? orderId = TryLong(ctx, "id") ?? TryLong(ctx, "order_id");
            int? statusId = (int?)(TryLong(ctx, "status_id")
                ?? (ctx.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Object ? TryLong(st, "id") : null));
            return (orderId, statusId, isPayment);
        }
        catch (JsonException)
        {
            _log.LogWarning("Событие {Id}: не удалось разобрать payload", evt.Id);
            return (null, null, isPayment);
        }
    }

    private static long? TryLong(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64()
        : e.TryGetProperty(name, out var s) && s.ValueKind == JsonValueKind.String && long.TryParse(s.GetString(), out var l) ? l
        : null;
}
