using System.Text.Json;
using IntertexSync.Core.Contracts;
using IntertexSync.Infrastructure.OneC;
using IntertexSync.Infrastructure.Storage;
using IntertexSync.Infrastructure.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace IntertexSync.Tests;

public sealed class StockSyncServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"itx-stock-{Guid.NewGuid():N}.db");
    private readonly SqliteStore _store;
    private readonly Mock1CConnector _oneC = new();
    private readonly FakeKeyCrmClient _keyCrm = new();

    // GUID-ы шести мест из мока
    private const string Shop1 = "wh-shop1-guid", Shop4 = "wh-shop4-guid", Central = "wh-store1-guid";

    public StockSyncServiceTests() => _store = new SqliteStore(_dbPath);
    public void Dispose() { _store.Dispose(); try { File.Delete(_dbPath); } catch { } }

    private StockSyncService Service(bool dryRun = true, string[]? active = null) =>
        new(_oneC, _keyCrm, _store,
            Options.Create(new StockSyncOptions
            {
                KeyCrmWarehouseId = 1,
                DryRun = dryRun,
                Active1CWarehouseGuids = active ?? Array.Empty<string>(),
            }),
            NullLogger<StockSyncService>.Instance);

    [Fact]
    public async Task Aggregates_AvailableAcrossWarehouses_IntoSingleSku()
    {
        // Один SKU лежит на трёх местах — KeyCRM должен получить сумму (DEC-010).
        _oneC.SetStock("SKU-A", Shop1, 10m);
        _oneC.SetStock("SKU-A", Shop4, 5.5m);
        _oneC.SetStock("SKU-A", Central, 20m);

        var res = await Service(dryRun: false).SyncAsync();

        Assert.Equal(1, res.TotalSkus);
        Assert.Equal(35.5m, _keyCrm.LastPushed[("1", "SKU-A")]); // 10 + 5.5 + 20
    }

    [Fact]
    public async Task SubtractsReserve_InAvailable()
    {
        _oneC.SetStock("SKU-B", Shop1, 10m);
        await _oneC.UpsertOrderAsync(Order(1, ("SKU-B", 4m), Shop1), "k1");
        await _oneC.ReserveAsync(1, Shop1, "r1"); // 4 в резерв → available 6

        var res = await Service(dryRun: false).SyncAsync();
        Assert.Equal(6m, _keyCrm.LastPushed[("1", "SKU-B")]);
    }

    [Fact]
    public async Task OnlyChangedSkus_ArePushed_OnSecondRun()
    {
        _oneC.SetStock("SKU-A", Shop1, 10m);
        _oneC.SetStock("SKU-C", Shop1, 3m);
        var first = await Service(dryRun: false).SyncAsync();
        Assert.Equal(2, first.Changed);

        _keyCrm.Reset();
        // Второй прогон без изменений — ничего не отправляем.
        var second = await Service(dryRun: false).SyncAsync();
        Assert.Equal(0, second.Changed);
        Assert.Equal(0, second.Pushed);
        Assert.Empty(_keyCrm.PutCalls);
    }

    [Fact]
    public async Task ChangedStock_PushesOnlyThatSku()
    {
        _oneC.SetStock("SKU-A", Shop1, 10m);
        _oneC.SetStock("SKU-C", Shop1, 3m);
        await Service(dryRun: false).SyncAsync();

        _keyCrm.Reset();
        _oneC.SetStock("SKU-A", Shop1, 12m); // изменился только A
        var res = await Service(dryRun: false).SyncAsync();

        Assert.Equal(1, res.Changed);
        Assert.Equal(12m, _keyCrm.LastPushed[("1", "SKU-A")]);
        Assert.False(_keyCrm.LastPushed.ContainsKey(("1", "SKU-C")));
    }

    [Fact]
    public async Task VanishedSku_IsZeroedNotDeleted_R13()
    {
        _oneC.SetStock("SKU-A", Shop1, 10m);
        await Service(dryRun: false).SyncAsync();

        _keyCrm.Reset();
        _oneC.SetStock("SKU-A", Shop1, 0m); // пропал остаток (или товар неактуален)
        var res = await Service(dryRun: false).SyncAsync();

        // Отправлено обнуление, а не удаление.
        Assert.Equal(0m, _keyCrm.LastPushed[("1", "SKU-A")]);
        Assert.Equal(1, res.Changed);
    }

    [Fact]
    public async Task DryRun_DoesNotCallKeyCrm_ButReportsChanges()
    {
        _oneC.SetStock("SKU-A", Shop1, 10m);
        var res = await Service(dryRun: true).SyncAsync();

        Assert.True(res.DryRun);
        Assert.Equal(1, res.Changed);
        Assert.Equal(0, res.Pushed);
        Assert.Empty(_keyCrm.PutCalls); // ГЕЙТ R-12: в живой KeyCRM не пишем
    }

    [Fact]
    public async Task ActiveWarehouseFilter_ExcludesLegacyPlaces()
    {
        _oneC.SetStock("SKU-A", Shop1, 10m);      // активный
        _oneC.SetStock("SKU-A", "wh-legacy", 99m); // не в списке активных
        var res = await Service(dryRun: false, active: new[] { Shop1, Shop4, Central }).SyncAsync();

        Assert.Equal(10m, _keyCrm.LastPushed[("1", "SKU-A")]); // 99 из legacy не учтён
    }

    private static Core.Models.OrderRequest Order(long id, (string sku, decimal qty) item, string wh) => new(
        id, "ctr-1", wh, 3, "USD", null,
        new[] { new Core.Models.OrderItem(item.sku, item.qty, 10m, 0m, item.qty * 10m) },
        $"chk-{id}");

    /// <summary>Фейковый клиент KeyCRM: записывает вызовы PutStocks, не ходит в сеть.</summary>
    private sealed class FakeKeyCrmClient : IKeyCrmClient
    {
        public readonly List<(long Wh, List<(string Sku, decimal Qty)> Items)> PutCalls = new();
        public readonly Dictionary<(string Wh, string Sku), decimal> LastPushed = new();

        public Task PutStocksAsync(long warehouseId, IReadOnlyList<(string Sku, decimal Quantity)> stocks, CancellationToken ct = default)
        {
            PutCalls.Add((warehouseId, stocks.ToList()));
            foreach (var (sku, qty) in stocks) LastPushed[(warehouseId.ToString(), sku)] = qty;
            return Task.CompletedTask;
        }

        public void Reset() { PutCalls.Clear(); LastPushed.Clear(); }

        public Task<JsonDocument> GetAsync(string path, IReadOnlyDictionary<string, string>? query = null, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task UpdateOrderAsync(long orderId, object payload, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<JsonDocument> GetOrderAsync(long orderId, string include, CancellationToken ct = default)
            => throw new NotImplementedException();
    }
}
