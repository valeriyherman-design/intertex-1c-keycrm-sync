using IntertexSync.Core.Contracts;
using IntertexSync.Core.Models;

namespace IntertexSync.Service;

/// <summary>
/// Фоновый обработчик очереди событий. Берёт события по одному (события одного
/// заказа — строго последовательно, гарантирует IEventQueue.DequeueAsync),
/// выполняет обработчик, при сбое планирует повтор с экспоненциальной задержкой.
/// Обработчики конкретных типов событий добавляются на Этапах 3–6.
/// </summary>
public sealed class QueueWorker : BackgroundService
{
    private readonly IEventQueue _queue;
    private readonly IServiceProvider _services;
    private readonly ILogger<QueueWorker> _log;
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2);

    public QueueWorker(IEventQueue queue, IServiceProvider services, ILogger<QueueWorker> log)
    {
        _queue = queue;
        _services = services;
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
                if (evt is null)
                {
                    await Task.Delay(IdleDelay, stoppingToken);
                    continue;
                }

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

    private Task HandleAsync(SyncEvent evt, CancellationToken ct)
    {
        // Маршрутизация по типам событий. Реальные обработчики (резерв, списание,
        // оплаты, остатки) подключаются на Этапах 3–6 согласно BACKLOG.md.
        switch (evt.Type)
        {
            case "webhook.ping":
                return Task.CompletedTask;
            default:
                _log.LogInformation("Обработчик для {Type} ещё не реализован — событие принято к сведению", evt.Type);
                return Task.CompletedTask;
        }
    }
}
