namespace IntertexSync.Infrastructure.KeyCrm;

/// <summary>
/// Скользящее окно: не более maxRequests за period (LIM-02: KeyCRM 60 req/min).
/// Потокобезопасен; при исчерпании квоты ожидает освобождения окна.
/// </summary>
public sealed class SlidingWindowRateLimiter
{
    private readonly int _maxRequests;
    private readonly TimeSpan _period;
    private readonly Queue<DateTime> _timestamps = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<DateTime> _clock;

    public SlidingWindowRateLimiter(int maxRequests, TimeSpan period, Func<DateTime>? clock = null)
    {
        _maxRequests = maxRequests;
        _period = period;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public async Task WaitAsync(CancellationToken ct = default)
    {
        while (true)
        {
            TimeSpan wait;
            await _gate.WaitAsync(ct);
            try
            {
                var now = _clock();
                while (_timestamps.Count > 0 && now - _timestamps.Peek() >= _period)
                    _timestamps.Dequeue();

                if (_timestamps.Count < _maxRequests)
                {
                    _timestamps.Enqueue(now);
                    return;
                }
                wait = _period - (now - _timestamps.Peek());
            }
            finally
            {
                _gate.Release();
            }
            await Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(50), ct);
        }
    }

    /// <summary>Текущее число запросов в окне (для health/метрик).</summary>
    public int CurrentCount
    {
        get
        {
            _gate.Wait();
            try
            {
                var now = _clock();
                while (_timestamps.Count > 0 && now - _timestamps.Peek() >= _period)
                    _timestamps.Dequeue();
                return _timestamps.Count;
            }
            finally { _gate.Release(); }
        }
    }
}
