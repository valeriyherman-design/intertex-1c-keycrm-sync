using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IntertexSync.Core.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntertexSync.Infrastructure.KeyCrm;

public sealed class KeyCrmOptions
{
    public string BaseUrl { get; set; } = "https://openapi.keycrm.app/v1/";
    /// <summary>API-ключ. Задаётся через переменную окружения / защищённый файл, НЕ в appsettings.json.</summary>
    public string ApiKey { get; set; } = "";
    public int RateLimitPerMinute { get; set; } = 60;
    public int MaxRetries { get; set; } = 4;
}

public sealed class KeyCrmClient : IKeyCrmClient
{
    private readonly HttpClient _http;
    private readonly SlidingWindowRateLimiter _limiter;
    private readonly ILogger<KeyCrmClient> _log;
    private readonly KeyCrmOptions _options;

    public KeyCrmClient(HttpClient http, IOptions<KeyCrmOptions> options, ILogger<KeyCrmClient> log)
    {
        _options = options.Value;
        _http = http;
        _http.BaseAddress = new Uri(_options.BaseUrl);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.Timeout = TimeSpan.FromSeconds(60);
        _limiter = new SlidingWindowRateLimiter(_options.RateLimitPerMinute, TimeSpan.FromMinutes(1));
        _log = log;
    }

    public async Task<JsonDocument> GetAsync(string path, IReadOnlyDictionary<string, string>? query = null, CancellationToken ct = default)
    {
        var url = query is { Count: > 0 }
            ? path + "?" + string.Join("&", query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"))
            : path;
        using var resp = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
        return await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    public async Task PutStocksAsync(long warehouseId, IReadOnlyList<(string Sku, decimal Quantity)> stocks, CancellationToken ct = default)
    {
        var payload = new
        {
            warehouse_id = warehouseId,
            stocks = stocks.Select(s => new { sku = s.Sku, quantity = s.Quantity }).ToArray(),
        };
        using var resp = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Put, "offers/stocks") { Content = JsonContent.Create(payload) }, ct);
    }

    public async Task UpdateOrderAsync(long orderId, object payload, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Put, $"order/{orderId}") { Content = JsonContent.Create(payload) }, ct);
    }

    public Task<JsonDocument> GetOrderAsync(long orderId, string include, CancellationToken ct = default)
        => GetAsync($"order/{orderId}", new Dictionary<string, string> { ["include"] = include }, ct);

    /// <summary>Rate-limit + повтор на 429/5xx/сетевых с экспоненциальной задержкой.</summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> requestFactory, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            await _limiter.WaitAsync(ct);
            HttpResponseMessage? resp = null;
            try
            {
                resp = await _http.SendAsync(requestFactory(), ct);
                if (resp.IsSuccessStatusCode) return resp;

                var transient = resp.StatusCode == HttpStatusCode.TooManyRequests || (int)resp.StatusCode >= 500;
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (!transient || attempt >= _options.MaxRetries)
                    throw new HttpRequestException($"KeyCRM {(int)resp.StatusCode}: {Truncate(body)}", null, resp.StatusCode);

                var delay = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                _log.LogWarning("KeyCRM {Status}, повтор через {Delay}с (попытка {Attempt}/{Max})",
                    (int)resp.StatusCode, delay.TotalSeconds, attempt + 1, _options.MaxRetries);
                resp.Dispose();
                await Task.Delay(delay, ct);
            }
            catch (HttpRequestException) when (attempt < _options.MaxRetries)
            {
                resp?.Dispose();
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                _log.LogWarning("KeyCRM сеть недоступна, повтор через {Delay}с (попытка {Attempt}/{Max})",
                    delay.TotalSeconds, attempt + 1, _options.MaxRetries);
                await Task.Delay(delay, ct);
            }
        }
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300];
}
