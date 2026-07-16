using IntertexSync.Core.Contracts;
using IntertexSync.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntertexSync.Infrastructure.Sync;

public sealed class StockSyncOptions
{
    /// <summary>ID склада KeyCRM «Основной», куда пишем агрегат. Для боевой записи ОБЯЗАН
    /// быть задан явно и провалидирован (LIM-04: Open API списком складов не отдаёт).</summary>
    public long KeyCrmWarehouseId { get; set; } = 1;

    /// <summary>GUID шести активных мест 1С (Магазины 1-4, Контейнер, Центральный).
    /// Для боевой записи ОБЯЗАТЕЛЕН (DEC-010) — иначе в агрегат попадут транзитные/брак/legacy
    /// склады и остаток раздуется (oversell). В dry-run пусто = агрегировать все (для отладки).</summary>
    public string[] Active1CWarehouseGuids { get; set; } = Array.Empty<string>();

    /// <summary>ГЕЙТ безопасности: пока true — только логируем, что БЫЛО БЫ отправлено, без записи в KeyCRM.
    /// Включать (false) только после подтверждения сопоставления SKU (R-12) и решения владельца.</summary>
    public bool DryRun { get; set; } = true;

    /// <summary>Максимум SKU в одном PUT /offers/stocks.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>Защита от массового обнуления: если из снапшота «пропало» больше этой доли SKU
    /// (аномально пустой/усохший ответ 1С), прогон прерывается без записи (R-13 — для единичных).</summary>
    public double MaxVanishFraction { get; set; } = 0.5;
}

public sealed record StockSyncResult(
    int TotalSkus,
    int Changed,
    int Pushed,
    bool DryRun,
    IReadOnlyList<(string Sku, decimal Old, decimal New)> Sample,
    bool Skipped = false,
    bool Aborted = false);

/// <summary>
/// Синхронизация остатков 1С → KeyCRM (DEC-010): суммирует доступный остаток по 6 местам
/// 1С в один агрегат на SKU и пишет в единственный склад KeyCRM «Основной». Отправляет
/// только изменившиеся против last-pushed снапшота SKU (LIM-03). 1С — истинный остаток
/// (DEC-004); из KeyCRM ничего не удаляем (R-13: товар без остатка → количество 0, не delete).
/// Прогоны не перекрываются (singleton + админ-эндпоинт): защита семафором (R-11).
/// </summary>
public sealed class StockSyncService
{
    private const int PageLimit = 1000;
    private const int MaxPages = 10_000; // backstop от рантайм-цикла

    private readonly I1CConnector _oneC;
    private readonly IKeyCrmClient _keyCrm;
    private readonly IStockSnapshot _snapshot;
    private readonly IConflictLog _conflicts;
    private readonly StockSyncOptions _options;
    private readonly ILogger<StockSyncService> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public StockSyncService(
        I1CConnector oneC, IKeyCrmClient keyCrm, IStockSnapshot snapshot, IConflictLog conflicts,
        IOptions<StockSyncOptions> options, ILogger<StockSyncService> log)
    {
        _oneC = oneC;
        _keyCrm = keyCrm;
        _snapshot = snapshot;
        _conflicts = conflicts;
        _options = options.Value;
        _log = log;
    }

    public async Task<StockSyncResult> SyncAsync(CancellationToken ct = default)
    {
        // Не допускаем перекрывающиеся прогоны — иначе PUT и запись снапшота из двух
        // потоков переплетутся и снапшот навсегда разойдётся с KeyCRM (LIM-03).
        if (!await _gate.WaitAsync(0, ct))
        {
            _log.LogWarning("Синхронизация остатков уже выполняется — повторный запуск пропущен");
            return new StockSyncResult(0, 0, 0, _options.DryRun, Array.Empty<(string, decimal, decimal)>(), Skipped: true);
        }
        try
        {
            ValidateLivePreconditions();

            // 1. Прочитать остатки из 1С (все страницы).
            var rows = await ReadAll1CStocksAsync(ct);

            // 2. Агрегировать доступный остаток по SKU по активным складам (DEC-010).
            //    Отрицательный available (over-reserve) зажимаем в 0 — витрина не бывает <0.
            var active = _options.Active1CWarehouseGuids.Length == 0
                ? null
                : new HashSet<string>(_options.Active1CWarehouseGuids, StringComparer.Ordinal);

            var aggregated = new Dictionary<string, decimal>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (active is not null && !active.Contains(row.WarehouseGuid)) continue;
                aggregated[row.Sku] = aggregated.GetValueOrDefault(row.Sku) + Math.Max(0m, row.Available);
            }

            var pushed = await _snapshot.GetAllAsync(_options.KeyCrmWarehouseId, ct);

            // 3. ЗАЩИТА от массового обнуления: если аномально много SKU «пропало» из выгрузки
            //    (пустой/частичный ответ 1С), прерываем прогон — не обнуляем весь каталог.
            if (pushed.Count > 0)
            {
                var vanished = pushed.Keys.Count(sku => !aggregated.ContainsKey(sku));
                var fraction = (double)vanished / pushed.Count;
                if (aggregated.Count == 0 || fraction > _options.MaxVanishFraction)
                {
                    var msg = $"Аномальный ответ 1С по остаткам: получено {aggregated.Count} SKU, " +
                              $"пропало {vanished}/{pushed.Count} ({fraction:P0}). Прогон прерван, обнуление НЕ выполнено.";
                    _log.LogError(msg);
                    await _conflicts.WriteAsync("stock_sync", "aggregate", msg, null, ct);
                    return new StockSyncResult(aggregated.Count, 0, 0, _options.DryRun,
                        Array.Empty<(string, decimal, decimal)>(), Aborted: true);
                }
            }

            // 4. Diff против снапшота last-pushed (LIM-03): шлём только изменившиеся.
            var changed = new List<(string Sku, decimal Old, decimal New)>();
            foreach (var (sku, qty) in aggregated)
            {
                var old = pushed.GetValueOrDefault(sku);
                if (old != qty) changed.Add((sku, old, qty));
            }
            // R-13: SKU был в снапшоте, но пропал из выгрузки → обнулить (не удалять).
            foreach (var (sku, old) in pushed)
                if (!aggregated.ContainsKey(sku) && old != 0m)
                    changed.Add((sku, old, 0m));

            _log.LogInformation("Остатки: SKU всего {Total}, изменилось {Changed} (dryRun={DryRun})",
                aggregated.Count, changed.Count, _options.DryRun);

            // 5. Отправить в KeyCRM (если не dry-run) батчами; обновить снапшот только по
            //    фактически отправленным SKU (частичный сбой не «продвинет» неотправленные).
            var pushedCount = 0;
            if (!_options.DryRun)
            {
                foreach (var batch in changed.Chunk(_options.BatchSize))
                {
                    await _keyCrm.PutStocksAsync(
                        _options.KeyCrmWarehouseId,
                        batch.Select(c => (c.Sku, c.New)).ToList(), ct);
                    // снапшот продвигаем поштучно-по-батчу: если следующий батч упадёт,
                    // уже отправленные останутся зафиксированными, повтор не задублирует.
                    await _snapshot.SetAsync(
                        _options.KeyCrmWarehouseId,
                        batch.Select(c => (c.Sku, c.New)).ToList(), ct);
                    pushedCount += batch.Length;
                }
            }
            else
            {
                foreach (var c in changed.Take(20))
                    _log.LogInformation("  [dry-run] {Sku}: {Old} → {New}", c.Sku, c.Old, c.New);
            }

            return new StockSyncResult(
                aggregated.Count, changed.Count, pushedCount, _options.DryRun,
                changed.Take(10).ToList());
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ValidateLivePreconditions()
    {
        if (_options.DryRun) return;
        if (_options.Active1CWarehouseGuids.Length == 0)
            throw new InvalidOperationException(
                "StockSync: для боевой записи требуется явный список Active1CWarehouseGuids (6 мест, DEC-010) — иначе транзит/брак/legacy раздуют остаток.");
        if (_options.KeyCrmWarehouseId <= 0)
            throw new InvalidOperationException(
                "StockSync: для боевой записи требуется валидный KeyCrmWarehouseId склада «Основной» (LIM-04).");
    }

    private async Task<List<StockRow1C>> ReadAll1CStocksAsync(CancellationToken ct)
    {
        var all = new List<StockRow1C>();
        int? reportedTotal = null;
        for (var page = 1; page <= MaxPages; page++)
        {
            var p = await _oneC.GetStocksAsync(warehouseGuid: null, updatedSince: null, page: page, limit: PageLimit, ct);
            all.AddRange(p.Data);
            reportedTotal = p.Total;
            // Авторитетный стоп — неполная/пустая страница; Total используем лишь как сверку.
            if (p.Data.Count < PageLimit) break;
        }
        if (reportedTotal is int t && t != all.Count)
            _log.LogWarning("Остатки: 1С сообщил Total={Total}, собрано {Count} — расхождение (проверить пагинацию)", t, all.Count);
        return all;
    }
}
