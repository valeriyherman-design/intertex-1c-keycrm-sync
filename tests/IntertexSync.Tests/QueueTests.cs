using IntertexSync.Core.Contracts;
using IntertexSync.Infrastructure.Storage;
using Xunit;

namespace IntertexSync.Tests;

public sealed class QueueTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"itx-test-{Guid.NewGuid():N}.db");
    private readonly SqliteStore _store;

    public QueueTests() => _store = new SqliteStore(_dbPath);

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static SyncEvent Evt(string dedup, string? orderKey = null, string type = "webhook.order_status") => new()
    {
        Type = type, PayloadJson = "{}", DedupKey = dedup, OrderKey = orderKey,
    };

    [Fact]
    public async Task Enqueue_DuplicateDedupKey_IsRejected()
    {
        Assert.True(await _store.EnqueueAsync(Evt("k1")));
        Assert.False(await _store.EnqueueAsync(Evt("k1"))); // повторный вебхук
        var stats = await _store.GetStatsAsync();
        Assert.Equal(1, stats.Pending);
    }

    [Fact]
    public async Task Dequeue_SameOrder_NotParallel()
    {
        await _store.EnqueueAsync(Evt("a", "order:1"));
        await _store.EnqueueAsync(Evt("b", "order:1"));
        await _store.EnqueueAsync(Evt("c", "order:2"));

        var e1 = await _store.DequeueAsync();
        Assert.NotNull(e1);
        Assert.Equal("order:1", e1!.OrderKey);

        // Пока событие заказа 1 в обработке — следующим выдаётся заказ 2, а не второе событие заказа 1.
        var e2 = await _store.DequeueAsync();
        Assert.NotNull(e2);
        Assert.Equal("order:2", e2!.OrderKey);

        var e3 = await _store.DequeueAsync();
        Assert.Null(e3); // больше нечего выдавать без нарушения последовательности

        await _store.MarkDoneAsync(e1.Id);
        var e4 = await _store.DequeueAsync();
        Assert.NotNull(e4);
        Assert.Equal("b", e4!.DedupKey);
    }

    [Fact]
    public async Task MarkFailed_Retryable_SchedulesBackoff()
    {
        await _store.EnqueueAsync(Evt("r1", "order:5"));
        var e = await _store.DequeueAsync();
        await _store.MarkFailedAsync(e!.Id, "1C timeout", retryable: true);

        // Событие вернулось в Pending, но с отложенным next_attempt_at — сразу не выдаётся.
        var again = await _store.DequeueAsync();
        Assert.Null(again);
        var stats = await _store.GetStatsAsync();
        Assert.Equal(1, stats.Pending);
    }

    [Fact]
    public async Task MarkFailed_NonRetryable_GoesDead()
    {
        await _store.EnqueueAsync(Evt("d1"));
        var e = await _store.DequeueAsync();
        await _store.MarkFailedAsync(e!.Id, "INSUFFICIENT_STOCK", retryable: false);
        var stats = await _store.GetStatsAsync();
        Assert.Equal(1, stats.Dead);

        // Ручной повтор из админки возвращает в очередь.
        Assert.True(await _store.RetryAsync(e.Id));
        var again = await _store.DequeueAsync();
        Assert.NotNull(again);
    }

    [Fact]
    public async Task Idempotency_SecondSave_KeepsFirstResult()
    {
        await _store.SaveResultAsync("op:1", "{\"n\":1}");
        await _store.SaveResultAsync("op:1", "{\"n\":2}");
        Assert.Equal("{\"n\":1}", await _store.TryGetResultAsync("op:1"));
    }

    [Fact]
    public async Task Mappings_SetGet_Roundtrip()
    {
        await _store.SetAsync(MappingKinds.Warehouse, "3", "wh-shop4-guid", "Магазин №4");
        Assert.Equal("wh-shop4-guid", await _store.GetAsync(MappingKinds.Warehouse, "3"));
        var all = await _store.GetAllAsync(MappingKinds.Warehouse);
        Assert.Single(all);
    }
}
