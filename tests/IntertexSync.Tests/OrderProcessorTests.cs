using System.Text;
using System.Text.Json;
using IntertexSync.Core.Contracts;
using IntertexSync.Core.Models;
using IntertexSync.Infrastructure.OneC;
using IntertexSync.Infrastructure.Orders;
using IntertexSync.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace IntertexSync.Tests;

public sealed class OrderProcessorTests : IDisposable
{
    private const string Shop4 = "wh-shop4-guid";
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"itx-order-{Guid.NewGuid():N}.db");
    private readonly SqliteStore _store;
    private readonly Mock1CConnector _oneC = new();
    private readonly FakeKeyCrm _keyCrm = new();

    public OrderProcessorTests() => _store = new SqliteStore(_dbPath);
    public void Dispose() { _store.Dispose(); try { File.Delete(_dbPath); } catch { } }

    private OrderProcessor Proc(bool dryRun = false) =>
        new(_keyCrm, _oneC, _store,
            Options.Create(new OrderSyncOptions
            {
                DryRun = dryRun,
                DefaultWarehouseGuid = Shop4,
                ReserveTriggerStatuses = new[] { 26 },
                ShipTriggerStatuses = new[] { 28 },
                CancelTriggerStatuses = new[] { 19 },
                StatusReserved = 100,
                StatusReadyToShip = 101,
                StatusSyncError = 199,
                CfOneCDocNumber = "OR_DOC",
                CfSyncError = "OR_ERR",
            }),
            NullLogger<OrderProcessor>.Instance);

    [Fact]
    public async Task Reserve_EnoughStock_Success_SetsReservedStatus()
    {
        _keyCrm.SetOrder(1780, OrderJson(1780, 26, "USD", ("SKU-A", 2m, 10m)));
        _oneC.SetStock("SKU-A", Shop4, 10m);

        var r = await Proc().ReserveAsync(1780);

        Assert.Equal(OrderOutcome.Success, r.Outcome);
        Assert.Equal(100, r.NewKeyCrmStatusId);
        Assert.Equal(2m, _oneC.GetReserved("SKU-A", Shop4));
        Assert.Contains(_keyCrm.UpdateCalls, u => u.OrderId == 1780 && u.Payload.Contains("\"status_id\":100"));
    }

    [Fact]
    public async Task Reserve_InsufficientStock_SyncError_WithReason()
    {
        _keyCrm.SetOrder(1781, OrderJson(1781, 26, "USD", ("SKU-A", 5m, 10m)));
        _oneC.SetStock("SKU-A", Shop4, 3m); // не хватает

        var r = await Proc().ReserveAsync(1781);

        Assert.Equal(OrderOutcome.InsufficientStock, r.Outcome);
        Assert.Equal(199, r.NewKeyCrmStatusId);
        Assert.Contains("SKU-A", r.ErrorReason);
        Assert.Equal(0m, _oneC.GetReserved("SKU-A", Shop4)); // фиктивного резерва нет
        Assert.Contains(_keyCrm.UpdateCalls, u => u.Payload.Contains("\"status_id\":199"));
    }

    [Fact]
    public async Task Reserve_NonUsd_CurrencyNotSupported()
    {
        _keyCrm.SetOrder(1782, OrderJson(1782, 26, "UAH", ("SKU-A", 1m, 10m)));
        var r = await Proc().ReserveAsync(1782);
        Assert.Equal(OrderOutcome.CurrencyNotSupported, r.Outcome);
        Assert.Equal(199, r.NewKeyCrmStatusId);
    }

    [Fact]
    public async Task Reserve_EmptyItems_ValidationFailed()
    {
        _keyCrm.SetOrder(1783, OrderJson(1783, 26, "USD"));
        var r = await Proc().ReserveAsync(1783);
        Assert.Equal(OrderOutcome.ValidationFailed, r.Outcome);
    }

    [Fact]
    public async Task Reserve_Twice_NoDoubleReserve()
    {
        _keyCrm.SetOrder(1784, OrderJson(1784, 26, "USD", ("SKU-A", 2m, 10m)));
        _oneC.SetStock("SKU-A", Shop4, 10m);
        await Proc().ReserveAsync(1784);
        await Proc().ReserveAsync(1784); // повторный вебхук
        Assert.Equal(2m, _oneC.GetReserved("SKU-A", Shop4));
    }

    [Fact]
    public async Task Ship_AfterReserve_Success_DecrementsStock()
    {
        _keyCrm.SetOrder(1785, OrderJson(1785, 28, "USD", ("SKU-A", 2m, 10m)));
        _oneC.SetStock("SKU-A", Shop4, 10m);
        await Proc().ReserveAsync(1785);

        var r = await Proc().ShipAsync(1785);

        Assert.Equal(OrderOutcome.Success, r.Outcome);
        Assert.Equal(101, r.NewKeyCrmStatusId);
        var stocks = await _oneC.GetStocksAsync(Shop4, null, 1, 100);
        Assert.Equal(8m, stocks.Data.Single(s => s.Sku == "SKU-A").Quantity); // 10 - 2
    }

    [Fact]
    public async Task Cancel_BeforeShip_SnimaetReserve()
    {
        _keyCrm.SetOrder(1786, OrderJson(1786, 19, "USD", ("SKU-A", 2m, 10m)));
        _oneC.SetStock("SKU-A", Shop4, 10m);
        await Proc().ReserveAsync(1786);
        Assert.Equal(2m, _oneC.GetReserved("SKU-A", Shop4));

        var r = await Proc().CancelAsync(1786);
        Assert.Equal(OrderOutcome.Success, r.Outcome);
        Assert.Equal(0m, _oneC.GetReserved("SKU-A", Shop4));
    }

    [Fact]
    public async Task Cancel_AfterShip_RequiresReturn_NotUnreserve()
    {
        _keyCrm.SetOrder(1787, OrderJson(1787, 28, "USD", ("SKU-A", 2m, 10m)));
        _oneC.SetStock("SKU-A", Shop4, 10m);
        await Proc().ReserveAsync(1787);
        await Proc().ShipAsync(1787);

        var r = await Proc().CancelAsync(1787);
        Assert.Equal(OrderOutcome.OneCError, r.Outcome); // после реализации — нужен возврат (ТЗ п.13)
    }

    [Fact]
    public async Task Payment_RegistersInOneC()
    {
        _keyCrm.SetOrder(1788, OrderJsonWithPayment(1788, payId: 1442, amount: 45m, currency: "USD"));
        var r = await Proc().RegisterPaymentsAsync(1788);
        Assert.Equal(OrderOutcome.Success, r.Outcome);
    }

    [Fact]
    public async Task DryRun_DoesNotWriteToKeyCrm()
    {
        _keyCrm.SetOrder(1789, OrderJson(1789, 26, "UAH", ("SKU-A", 1m, 10m)));
        var r = await Proc(dryRun: true).ReserveAsync(1789);
        Assert.Equal(OrderOutcome.CurrencyNotSupported, r.Outcome);
        Assert.Empty(_keyCrm.UpdateCalls); // dry-run: в живой KeyCRM не пишем
    }

    // ---- построение JSON заказа KeyCRM (по снапшоту §6) ----
    private static string OrderJson(long id, int status, string currency, params (string sku, decimal qty, decimal price)[] items)
    {
        var products = string.Join(",", items.Select(i =>
            $"{{\"sku\":\"{i.sku}\",\"quantity\":{i.qty.ToString(System.Globalization.CultureInfo.InvariantCulture)}," +
            $"\"price\":{i.price.ToString(System.Globalization.CultureInfo.InvariantCulture)}," +
            $"\"price_sold\":{i.price.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}"));
        return $"{{\"id\":{id},\"status_id\":{status},\"currency_code\":\"{currency}\"," +
               $"\"buyer\":{{\"id\":4075,\"full_name\":\"Тест Клієнт\",\"phone\":[\"+380671112233\"],\"email\":[]}}," +
               $"\"manager\":{{\"id\":3}},\"products\":[{products}]}}";
    }

    private static string OrderJsonWithPayment(long id, long payId, decimal amount, string currency) =>
        $"{{\"id\":{id},\"status_id\":3,\"currency_code\":\"USD\"," +
        $"\"buyer\":{{\"id\":4075,\"full_name\":\"Тест\",\"phone\":[],\"email\":[]}}," +
        $"\"products\":[],\"payments\":[{{\"id\":{payId},\"actual_amount\":{amount.ToString(System.Globalization.CultureInfo.InvariantCulture)}," +
        $"\"actual_currency\":\"{currency}\",\"payment_method_id\":2,\"is_expense\":false}}]}}";

    /// <summary>Фейковый клиент KeyCRM: отдаёт заранее заданный заказ, записывает обновления.</summary>
    private sealed class FakeKeyCrm : IKeyCrmClient
    {
        private readonly Dictionary<long, string> _orders = new();
        public readonly List<(long OrderId, string Payload)> UpdateCalls = new();

        public void SetOrder(long id, string json) => _orders[id] = json;

        public Task<JsonDocument> GetOrderAsync(long orderId, string include, CancellationToken ct = default)
            => Task.FromResult(JsonDocument.Parse(_orders[orderId]));

        public Task UpdateOrderAsync(long orderId, object payload, CancellationToken ct = default)
        {
            UpdateCalls.Add((orderId, JsonSerializer.Serialize(payload)));
            return Task.CompletedTask;
        }

        public Task<JsonDocument> GetAsync(string path, IReadOnlyDictionary<string, string>? query = null, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task PutStocksAsync(long warehouseId, IReadOnlyList<(string Sku, decimal Quantity)> stocks, CancellationToken ct = default)
            => throw new NotImplementedException();
    }
}
