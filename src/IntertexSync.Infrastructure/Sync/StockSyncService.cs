using IntertexSync.Core.Contracts;
using IntertexSync.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntertexSync.Infrastructure.Sync;

public sealed class StockSyncOptions
{
    /// <summary>ID склада KeyCRM «Основной», куда пишем агрегат (LIM-04: узнаётся при первой записи).</summary>
    public long KeyCrmWarehouseId { get; set; } = 1;

    /// <summary>GUID шести активных мест 1С (Магазины 1-4, Контейнер, Центральный).
    /// Пусто = агрегировать все склады, что вернула 1С (пустые/legacy в выгрузке остатков не участвуют).</summary>
    public string[] Active1CWarehouseGuids { get; set; } = Array.Empty<string>();

    /// <summary>ГЕЙТ безопасности: пока true — только логируем, что БЫЛО БЫ отправлено, без записи в KeyCRM.
    /// Включать запись (false) только после подтверждения сопоставления SKU (R-12) и решения владельца.</summary>
    public bool DryRun { get; set; } = true;

    /// <summary>Максимум SKU в одном PUT /offers/stocks.</summary>
    public int BatchSize { get; set; } = 500;
}

public sealed record StockSyncResult(
    int TotalSkus,
    int Changed,
    int Pushed,
    bool DryRun,
    IReadOnlyList<(string Sku, decimal Old, decimal New)> Sample);

/// <summary>
/// Синхронизация остатков 1С → KeyCRM (DEC-010): суммирует доступный остаток по 6 местам
/// 1С в один агрегат на SKU и пишет в единственный склад KeyCRM «Основной». Отправляет
/// только изменившиеся против last-pushed снапшота SKU (LIM-03). 1С — истинный остаток
/// (DEC-004); из KeyCRM ничего не удаляем (R-13: товар без остатка → количество 0, а не delete).
/// </summary>
public sealed class StockSyncService
{
    private readonly I1CConnector _oneC;
    private readonly IKeyCrmClient _keyCrm;
    private readonly IStockSnapshot _snapshot;
    private readonly StockSyncOptions _options;
    private readonly ILogger<StockSyncService> _log;

    public StockSyncService(
        I1CConnector oneC, IKeyCrmClient keyCrm, IStockSnapshot snapshot,
        IOptions<StockSyncOptions> options, ILogger<StockSyncService> log)
    {
        _oneC = oneC;
        _keyCrm = keyCrm;
        _snapshot = snapshot;
        _options = options.Value;
        _log = log;
    }

    public async Task<StockSyncResult> SyncAsync(CancellationToken ct = default)
    {
        // 1. Прочитать остатки из 1С (все страницы), собрать per-warehouse строки.
        var rows = await ReadAll1CStocksAsync(ct);

        // 2. Агрегировать доступный остаток по SKU по активным складам (DEC-010).
        var active = _options.Active1CWarehouseGuids.Length == 0
            ? null
            : new HashSet<string>(_options.Active1CWarehouseGuids, StringComparer.Ordinal);

        var aggregated = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (active is not null && !active.Contains(row.WarehouseGuid)) continue;
            aggregated[row.Sku] = aggregated.GetValueOrDefault(row.Sku) + row.Available;
        }

        // 3. Сравнить со снапшотом last-pushed. Отправляем только изменившиеся.
        var pushed = await _snapshot.GetAllAsync(_options.KeyCrmWarehouseId, ct);
        var changed = new List<(string Sku, decimal Old, decimal New)>();
        foreach (var (sku, qty) in aggregated)
        {
            var old = pushed.GetValueOrDefault(sku);
            if (old != qty) changed.Add((sku, old, qty));
        }
        // R-13: SKU, который был в снапшоте, но пропал из выгрузки 1С — обнуляем остаток
        // в KeyCRM (товар не удаляем; из выгрузки он мог пропасть как неактуальный).
        foreach (var (sku, old) in pushed)
        {
            if (!aggregated.ContainsKey(sku) && old != 0m)
                changed.Add((sku, old, 0m));
        }

        _log.LogInformation("Остатки: SKU всего {Total}, изменилось {Changed} (dryRun={DryRun})",
            aggregated.Count, changed.Count, _options.DryRun);

        // 4. Отправить в KeyCRM (если не dry-run) батчами; обновить снапшот.
        var pushedCount = 0;
        if (!_options.DryRun && changed.Count > 0)
        {
            foreach (var batch in changed.Chunk(_options.BatchSize))
            {
                await _keyCrm.PutStocksAsync(
                    _options.KeyCrmWarehouseId,
                    batch.Select(c => (c.Sku, c.New)).ToList(), ct);
                pushedCount += batch.Length;
            }
            await _snapshot.SetAsync(
                _options.KeyCrmWarehouseId,
                changed.Select(c => (c.Sku, c.New)).ToList(), ct);
        }
        else if (_options.DryRun && changed.Count > 0)
        {
            foreach (var c in changed.Take(20))
                _log.LogInformation("  [dry-run] {Sku}: {Old} → {New}", c.Sku, c.Old, c.New);
        }

        return new StockSyncResult(
            aggregated.Count, changed.Count, pushedCount, _options.DryRun,
            changed.Take(10).ToList());
    }

    private async Task<List<StockRow1C>> ReadAll1CStocksAsync(CancellationToken ct)
    {
        var all = new List<StockRow1C>();
        var page = 1;
        while (true)
        {
            var p = await _oneC.GetStocksAsync(warehouseGuid: null, updatedSince: null, page: page, limit: 1000, ct);
            all.AddRange(p.Data);
            if (p.Data.Count == 0 || all.Count >= p.Total || p.Data.Count < 1000) break;
            page++;
        }
        return all;
    }
}
