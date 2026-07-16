using IntertexSync.Core.Models;
using IntertexSync.Infrastructure.OneC;
using Xunit;

namespace IntertexSync.Tests;

/// <summary>Тесты учётных инвариантов на моке 1С (сценарии из ТЗ п.18).</summary>
public sealed class Mock1CTests
{
    private const string Wh = "wh-shop4-guid";

    private static OrderRequest Order(long id, params (string sku, decimal qty)[] items) => new(
        id, "ctr-1", Wh, 3, "USD", null,
        items.Select(i => new OrderItem(i.sku, i.qty, 10m, 0m, i.qty * 10m)).ToList(),
        ItemsChecksum: $"chk-{id}");

    [Fact]
    public async Task Fabric_FractionalQuantity_12_5m_Reserves()
    {
        var c = new Mock1CConnector();
        c.SetStock("FABRIC-1", Wh, 20.0m);
        await c.UpsertOrderAsync(Order(1, ("FABRIC-1", 12.5m)), "k1");
        var rows = await c.ReserveAsync(1, Wh, "res-1");
        Assert.Equal(12.5m, rows[0].Reserved);
        Assert.Equal(7.5m, rows[0].Available);
    }

    [Fact]
    public async Task InsufficientStock_NoPartialReserve()
    {
        var c = new Mock1CConnector();
        c.SetStock("A", Wh, 5m);
        c.SetStock("B", Wh, 1m);
        await c.UpsertOrderAsync(Order(2, ("A", 2m), ("B", 3m)), "k2");

        var ex = await Assert.ThrowsAsync<Sync1CException>(() => c.ReserveAsync(2, Wh, "res-2"));
        Assert.Equal(Sync1CErrorCode.InsufficientStock, ex.Code);
        Assert.Single(ex.Details); // дефицит только по B
        Assert.Equal(0m, c.GetReserved("A", Wh)); // резерв по A НЕ создан (никаких частичных)
    }

    [Fact]
    public async Task RepeatedReserve_SameKey_NoDoubleReserve()
    {
        var c = new Mock1CConnector();
        c.SetStock("A", Wh, 10m);
        await c.UpsertOrderAsync(Order(3, ("A", 4m)), "k3");
        await c.ReserveAsync(3, Wh, "res-3");
        await c.ReserveAsync(3, Wh, "res-3"); // повторный вебхук
        Assert.Equal(4m, c.GetReserved("A", Wh));
    }

    [Fact]
    public async Task RepeatedShip_SameOrder_NoDoubleWriteOff()
    {
        var c = new Mock1CConnector();
        c.SetStock("A", Wh, 10m);
        await c.UpsertOrderAsync(Order(4, ("A", 4m)), "k4");
        await c.ReserveAsync(4, Wh, "res-4");

        var d1 = await c.ShipAsync(4, "chk-4", "ship-4");
        var d2 = await c.ShipAsync(4, "chk-4", "ship-4b"); // повтор
        Assert.Equal(d1.Number, d2.Number);

        var stocks = await c.GetStocksAsync(Wh, null, 1, 100);
        Assert.Equal(6m, stocks.Data.Single(s => s.Sku == "A").Quantity); // списано один раз
    }

    [Fact]
    public async Task Ship_ModifiedOrder_Rejected()
    {
        var c = new Mock1CConnector();
        c.SetStock("A", Wh, 10m);
        await c.UpsertOrderAsync(Order(5, ("A", 4m)), "k5");
        await c.ReserveAsync(5, Wh, "res-5");
        var ex = await Assert.ThrowsAsync<Sync1CException>(() => c.ShipAsync(5, "wrong-checksum", "ship-5"));
        Assert.Equal(Sync1CErrorCode.OrderModified, ex.Code);
    }

    [Fact]
    public async Task NonUsdCurrency_Rejected()
    {
        var c = new Mock1CConnector();
        var order = Order(6, ("A", 1m)) with { Currency = "UAH" };
        var ex = await Assert.ThrowsAsync<Sync1CException>(() => c.UpsertOrderAsync(order, "k6"));
        Assert.Equal(Sync1CErrorCode.CurrencyNotSupported, ex.Code); // DEC-008: Prom вручную
    }

    [Fact]
    public async Task Unreserve_Idempotent_And_RestoresAvailability()
    {
        var c = new Mock1CConnector();
        c.SetStock("A", Wh, 10m);
        await c.UpsertOrderAsync(Order(7, ("A", 4m)), "k7");
        await c.ReserveAsync(7, Wh, "res-7");
        Assert.True(await c.UnreserveAsync(7, "unres-7"));
        Assert.True(await c.UnreserveAsync(7, "unres-7")); // повтор — не ошибка
        Assert.Equal(0m, c.GetReserved("A", Wh));
    }

    [Fact]
    public async Task Return_RestoresStock_KeepsRealization()
    {
        var c = new Mock1CConnector();
        c.SetStock("A", Wh, 10m);
        await c.UpsertOrderAsync(Order(8, ("A", 4m)), "k8");
        await c.ReserveAsync(8, Wh, "res-8");
        await c.ShipAsync(8, "chk-8", "ship-8");

        var ret = await c.CreateReturnAsync(new ReturnRequest(8, new[] { new ReturnItem("A", 2m) }, "брак", false), "ret-8");
        Assert.True(ret.Posted);

        var state = await c.GetOrderStateAsync(8);
        Assert.NotNull(state.Realization); // реализация НЕ удалена (ТЗ п.13)
        var stocks = await c.GetStocksAsync(Wh, null, 1, 100);
        Assert.Equal(8m, stocks.Data.Single(s => s.Sku == "A").Quantity); // 10 - 4 + 2
    }

    [Fact]
    public async Task Payment_DuplicateKeycrmId_NoDuplicate()
    {
        var c = new Mock1CConnector();
        c.SetStock("A", Wh, 10m);
        await c.UpsertOrderAsync(Order(9, ("A", 1m)), "k9");
        var p1 = await c.RegisterPaymentAsync(new PaymentRequest(1442, 9, 45m, "USD", "credit_card", DateTime.UtcNow), "pay-a");
        var p2 = await c.RegisterPaymentAsync(new PaymentRequest(1442, 9, 45m, "USD", "credit_card", DateTime.UtcNow), "pay-b");
        Assert.Equal(p1.Number, p2.Number); // тот же документ
    }

    [Fact]
    public async Task TwoManagers_SameStock_OnlyOneWins()
    {
        var c = new Mock1CConnector();
        c.SetStock("A", Wh, 5m);
        await c.UpsertOrderAsync(Order(10, ("A", 4m)), "k10");
        await c.UpsertOrderAsync(Order(11, ("A", 4m)), "k11");

        await c.ReserveAsync(10, Wh, "res-10");
        var ex = await Assert.ThrowsAsync<Sync1CException>(() => c.ReserveAsync(11, Wh, "res-11"));
        Assert.Equal(Sync1CErrorCode.InsufficientStock, ex.Code); // двойного списания не будет
    }
}
