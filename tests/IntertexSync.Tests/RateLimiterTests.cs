using IntertexSync.Infrastructure.KeyCrm;
using Xunit;

namespace IntertexSync.Tests;

public sealed class RateLimiterTests
{
    [Fact]
    public async Task AllowsUpToLimit_ThenWaits()
    {
        var now = DateTime.UtcNow;
        var limiter = new SlidingWindowRateLimiter(3, TimeSpan.FromMinutes(1), () => now);

        await limiter.WaitAsync();
        await limiter.WaitAsync();
        await limiter.WaitAsync();
        Assert.Equal(3, limiter.CurrentCount);

        // Четвёртый запрос в том же окне должен ждать; сдвигаем часы — окно освобождается.
        now = now.AddSeconds(61);
        await limiter.WaitAsync();
        Assert.Equal(1, limiter.CurrentCount);
    }

    [Fact]
    public async Task ParallelCallers_NeverExceedLimit()
    {
        var limiter = new SlidingWindowRateLimiter(10, TimeSpan.FromSeconds(30));
        var tasks = Enumerable.Range(0, 10).Select(_ => limiter.WaitAsync()).ToArray();
        await Task.WhenAll(tasks);
        Assert.Equal(10, limiter.CurrentCount); // все прошли, лимит не превышен
    }
}
