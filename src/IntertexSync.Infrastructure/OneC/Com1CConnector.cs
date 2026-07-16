using System.Runtime.InteropServices;
using System.Text.Json;
using IntertexSync.Core.Contracts;
using IntertexSync.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntertexSync.Infrastructure.OneC;

public sealed class OneCOptions
{
    /// <summary>Строка соединения COM, напр.: File="D:\ALMITS\01_INTERTEX\ITX_UTP";Usr="ИнтеграцияKeyCRM";Pwd="..."</summary>
    public string ConnectionString { get; set; } = "";
    /// <summary>ProgID коннектора платформы.</summary>
    public string ProgId { get; set; } = "V83.COMConnector";
    /// <summary>Путь к внешней обработке ИнтеграцияKeyCRM.epf с экспортными функциями контракта.</summary>
    public string ProcessorPath { get; set; } = "";
    public int OperationTimeoutSeconds { get; set; } = 120;
}

/// <summary>
/// Транспорт до 1С через COM (V83.COMConnector) — вариант B (DEC-009).
/// Файловая база: все вызовы строго последовательны (одно соединение, семафор) — R-11.
/// Обмен с обработкой — JSON-строками: Обработка.Выполнить(операция, json) → json.
/// Работает только на Windows; на других ОС бросает PlatformNotSupportedException.
/// </summary>
public sealed class Com1CConnector : I1CConnector, IDisposable
{
    private readonly OneCOptions _options;
    private readonly ILogger<Com1CConnector> _log;
    private readonly SemaphoreSlim _gate = new(1, 1); // файловая база: без параллелизма
    private object? _connection;
    private object? _processor;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Com1CConnector(IOptions<OneCOptions> options, ILogger<Com1CConnector> log)
    {
        _options = options.Value;
        _log = log;
    }

    // ---------- Публичные операции контракта ----------

    public Task<HealthInfo> HealthAsync(CancellationToken ct = default)
        => InvokeAsync<HealthInfo>("health", new { }, ct);

    public Task<IReadOnlyList<Warehouse1C>> GetWarehousesAsync(CancellationToken ct = default)
        => InvokeAsync<IReadOnlyList<Warehouse1C>>("warehouses", new { }, ct);

    public Task<Page<Product1C>> GetProductsAsync(DateTime? updatedSince, int page, int limit, CancellationToken ct = default)
        => InvokeAsync<Page<Product1C>>("products", new { updatedSince, page, limit }, ct);

    public Task<Page<StockRow1C>> GetStocksAsync(string? warehouseGuid, DateTime? updatedSince, int page, int limit, CancellationToken ct = default)
        => InvokeAsync<Page<StockRow1C>>("stocks", new { warehouseGuid, updatedSince, page, limit }, ct);

    public Task<CustomerResult> UpsertCustomerAsync(CustomerRequest request, string idempotencyKey, bool dryRun = false, CancellationToken ct = default)
        => InvokeAsync<CustomerResult>("upsert_customer", new { request, idempotencyKey, dryRun }, ct);

    public Task<DocumentRef> UpsertOrderAsync(OrderRequest request, string idempotencyKey, bool dryRun = false, CancellationToken ct = default)
        => InvokeAsync<DocumentRef>("upsert_order", new { request, idempotencyKey, dryRun }, ct);

    public Task<IReadOnlyList<StockRow1C>> ReserveAsync(long keycrmOrderId, string warehouseGuid, string idempotencyKey, bool dryRun = false, CancellationToken ct = default)
        => InvokeAsync<IReadOnlyList<StockRow1C>>("reserve", new { keycrmOrderId, warehouseGuid, idempotencyKey, dryRun }, ct);

    public Task<bool> UnreserveAsync(long keycrmOrderId, string idempotencyKey, bool dryRun = false, CancellationToken ct = default)
        => InvokeAsync<bool>("unreserve", new { keycrmOrderId, idempotencyKey, dryRun }, ct);

    public Task<DocumentRef> ShipAsync(long keycrmOrderId, string itemsChecksum, string idempotencyKey, bool dryRun = false, CancellationToken ct = default)
        => InvokeAsync<DocumentRef>("ship", new { keycrmOrderId, itemsChecksum, idempotencyKey, dryRun }, ct);

    public Task<DocumentRef> CreateReturnAsync(ReturnRequest request, string idempotencyKey, bool dryRun = false, CancellationToken ct = default)
        => InvokeAsync<DocumentRef>("create_return", new { request, idempotencyKey, dryRun }, ct);

    public Task<DocumentRef> RegisterPaymentAsync(PaymentRequest request, string idempotencyKey, bool dryRun = false, CancellationToken ct = default)
        => InvokeAsync<DocumentRef>("register_payment", new { request, idempotencyKey, dryRun }, ct);

    public Task<OrderState1C> GetOrderStateAsync(long keycrmOrderId, CancellationToken ct = default)
        => InvokeAsync<OrderState1C>("order_state", new { keycrmOrderId }, ct);

    public Task<IReadOnlyList<Transfer1C>> GetTransfersAsync(DateTime since, CancellationToken ct = default)
        => InvokeAsync<IReadOnlyList<Transfer1C>>("transfers", new { since }, ct);

    // ---------- Внутренности COM ----------

    private async Task<T> InvokeAsync<T>(string operation, object args, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Com1CConnector работает только на Windows (V83.COMConnector). Для разработки используйте Mock1CConnector (OneC:Driver=mock).");

        await _gate.WaitAsync(ct);
        try
        {
            // COM-вызовы синхронные; выносим в пул с тайм-аутом операции.
            // Платформа гарантирована проверкой IsWindows выше.
#pragma warning disable CA1416
            var task = Task.Run(() => InvokeCore<T>(operation, args), ct);
#pragma warning restore CA1416
            var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(_options.OperationTimeoutSeconds), ct));
            if (completed != task)
                throw new Sync1CException(Sync1CErrorCode.InternalError, $"1С: тайм-аут операции '{operation}' ({_options.OperationTimeoutSeconds}с)", retryable: true);
            return await task;
        }
        finally
        {
            _gate.Release();
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private T InvokeCore<T>(string operation, object args)
    {
        EnsureConnected();
        var argsJson = JsonSerializer.Serialize(args, Json);
        string resultJson;
        try
        {
            // Обработка.ВыполнитьОперацию(Операция, АргументыJSON) → СтрокаJSON
            // ("Выполнить" — зарезервированное слово встроенного языка 1С)
            resultJson = (string)_processor!.GetType().InvokeMember(
                "ВыполнитьОперацию",
                System.Reflection.BindingFlags.InvokeMethod,
                null, _processor, new object[] { operation, argsJson })!;
        }
        catch (TargetInvocationWrapper ex)
        {
            ResetConnection();
            throw new Sync1CException(Sync1CErrorCode.InternalError, $"1С COM: {ex.Message}", retryable: true);
        }
        catch (COMException ex)
        {
            ResetConnection();
            throw new Sync1CException(Sync1CErrorCode.InternalError, $"1С COM: {ex.Message}", retryable: true);
        }

        using var doc = JsonDocument.Parse(resultJson);
        var root = doc.RootElement;
        if (root.TryGetProperty("success", out var ok) && !ok.GetBoolean())
        {
            var err = root.GetProperty("error");
            var code = ParseCode(err.GetProperty("code").GetString() ?? "INTERNAL_ERROR");
            var message = err.GetProperty("message").GetString() ?? "";
            var retryable = err.TryGetProperty("retryable", out var r) && r.GetBoolean();
            var details = err.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.Array
                ? d.EnumerateArray().Select(x => x.ToString()).ToArray()
                : Array.Empty<string>();
            throw new Sync1CException(code, message, retryable, details);
        }

        var data = root.TryGetProperty("data", out var dataEl) ? dataEl : root;
        return data.Deserialize<T>(Json)
            ?? throw new Sync1CException(Sync1CErrorCode.InternalError, $"1С: пустой ответ операции '{operation}'");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void EnsureConnected()
    {
        if (_processor is not null) return;

        var progType = Type.GetTypeFromProgID(_options.ProgId)
            ?? throw new Sync1CException(Sync1CErrorCode.InternalError,
                $"ProgID '{_options.ProgId}' не зарегистрирован. Установите платформу 1С и зарегистрируйте comcntr.dll.", retryable: false);

        _log.LogInformation("Подключение к 1С через {ProgId}…", _options.ProgId);
        var connector = Activator.CreateInstance(progType)!;
        _connection = connector.GetType().InvokeMember(
            "Connect", System.Reflection.BindingFlags.InvokeMethod,
            null, connector, new object[] { _options.ConnectionString })!;

        // ВнешниеОбработки.Создать(Путь) → объект обработки с экспортной функцией Выполнить().
        var external = _connection.GetType().InvokeMember(
            "ВнешниеОбработки", System.Reflection.BindingFlags.GetProperty, null, _connection, null)!;
        _processor = external.GetType().InvokeMember(
            "Создать", System.Reflection.BindingFlags.InvokeMethod,
            null, external, new object[] { _options.ProcessorPath })!;
        _log.LogInformation("1С: соединение установлено, обработка загружена ({Path})", _options.ProcessorPath);
    }

    private void ResetConnection()
    {
        if (OperatingSystem.IsWindows())
        {
            if (_processor is not null) { try { Marshal.FinalReleaseComObject(_processor); } catch { /* ignore */ } }
            if (_connection is not null) { try { Marshal.FinalReleaseComObject(_connection); } catch { /* ignore */ } }
        }
        _processor = null;
        _connection = null;
    }

    private static Sync1CErrorCode ParseCode(string code) => code switch
    {
        "AUTH_FAILED" => Sync1CErrorCode.AuthFailed,
        "VALIDATION_ERROR" => Sync1CErrorCode.ValidationError,
        "SKU_NOT_FOUND" => Sync1CErrorCode.SkuNotFound,
        "WAREHOUSE_NOT_FOUND" => Sync1CErrorCode.WarehouseNotFound,
        "CUSTOMER_NOT_FOUND" => Sync1CErrorCode.CustomerNotFound,
        "ORDER_NOT_FOUND" => Sync1CErrorCode.OrderNotFound,
        "INSUFFICIENT_STOCK" => Sync1CErrorCode.InsufficientStock,
        "ALREADY_PROCESSED" => Sync1CErrorCode.AlreadyProcessed,
        "ORDER_MODIFIED" => Sync1CErrorCode.OrderModified,
        "PERIOD_CLOSED" => Sync1CErrorCode.PeriodClosed,
        "POSTING_ERROR" => Sync1CErrorCode.PostingError,
        "CURRENCY_NOT_SUPPORTED" => Sync1CErrorCode.CurrencyNotSupported,
        "LOCKED" => Sync1CErrorCode.Locked,
        _ => Sync1CErrorCode.InternalError,
    };

    public void Dispose()
    {
        ResetConnection();
        _gate.Dispose();
    }

    /// <summary>Обёртка для унификации catch (реальный тип зависит от рефлексии).</summary>
    private sealed class TargetInvocationWrapper : Exception
    {
        public TargetInvocationWrapper(string message) : base(message) { }
    }
}
