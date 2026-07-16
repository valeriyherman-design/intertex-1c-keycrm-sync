using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IntertexSync.Core.Contracts;
using IntertexSync.Infrastructure.KeyCrm;
using IntertexSync.Infrastructure.OneC;
using IntertexSync.Infrastructure.Storage;
using IntertexSync.Service;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// --- Windows Service (боевой режим) ---
builder.Host.UseWindowsService(o => o.ServiceName = "IntertexSync");

// --- Логи: структурированные, консоль + файл с ротацией ---
var logDir = builder.Configuration["Logging:Dir"] ?? Path.Combine(AppContext.BaseDirectory, "logs");
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.File(Path.Combine(logDir, "intertexsync-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30)
    .CreateLogger();
builder.Host.UseSerilog();

// --- Конфигурация ---
// Секреты (KeyCrm:ApiKey, OneC:ConnectionString, Webhook:Secret) — из переменных
// окружения или защищённого файла secrets.json рядом с сервисом, НЕ из appsettings.json.
var secretsPath = Path.Combine(AppContext.BaseDirectory, "secrets.json");
if (File.Exists(secretsPath)) builder.Configuration.AddJsonFile(secretsPath, optional: true);
builder.Configuration.AddEnvironmentVariables("INTERTEX_");

builder.Services.Configure<KeyCrmOptions>(builder.Configuration.GetSection("KeyCrm"));
builder.Services.Configure<OneCOptions>(builder.Configuration.GetSection("OneC"));

// --- Хранилище (SQLite WAL) ---
// Пустая строка в конфиге = не задано (используем каталог сервиса).
static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
var dbPath = NullIfEmpty(builder.Configuration["Storage:DbPath"])
    ?? Path.Combine(AppContext.BaseDirectory, "data", "intertexsync.db");
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
var store = new SqliteStore(dbPath);
builder.Services.AddSingleton(store);
builder.Services.AddSingleton<IEventQueue>(store);
builder.Services.AddSingleton<IIdempotencyStore>(store);
builder.Services.AddSingleton<IMappingStore>(store);
builder.Services.AddSingleton<IConflictLog>(store);

// --- KeyCRM клиент (rate limit 60 rpm + retry) ---
builder.Services.AddHttpClient<IKeyCrmClient, KeyCrmClient>();

// --- Коннектор 1С: com (Windows, боевой) | mock (разработка/тесты) ---
var oneCDriver = NullIfEmpty(builder.Configuration["OneC:Driver"]) ?? (OperatingSystem.IsWindows() ? "com" : "mock");
if (oneCDriver.Equals("com", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddSingleton<I1CConnector, Com1CConnector>();
else
    builder.Services.AddSingleton<I1CConnector, Mock1CConnector>();

// --- Фоновый воркер очереди ---
builder.Services.AddHostedService<QueueWorker>();

var app = builder.Build();

// ---------- Endpoints ----------

// Health: сервис + очередь + 1С (KeyCRM не пингуем на каждый health — бережём лимит).
app.MapGet("/health", async (IEventQueue queue, I1CConnector oneC) =>
{
    var stats = await queue.GetStatsAsync();
    object oneCState;
    try
    {
        var h = await oneC.HealthAsync();
        oneCState = new { status = h.Status, config = h.Config };
    }
    catch (Exception ex)
    {
        oneCState = new { status = "error", error = ex.Message };
    }
    return Results.Ok(new
    {
        status = "ok",
        driver_1c = oneCDriver,
        queue = new { stats.Pending, stats.Processing, stats.Failed, stats.Dead, lastSuccess = stats.LastSuccessUtc },
        oneC = oneCState,
        time = DateTime.UtcNow,
    });
});

// Вебхук KeyCRM: проверка секрета в URL → сохранить в очередь → немедленно 200 (ТЗ п.4.2).
app.MapPost("/webhook/{secret}", async (string secret, HttpRequest request, IEventQueue queue, IConfiguration cfg, ILogger<Program> log) =>
{
    var expected = cfg["Webhook:Secret"];
    if (string.IsNullOrEmpty(expected) || !CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(expected)))
        return Results.NotFound(); // не раскрываем существование эндпоинта

    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();
    if (string.IsNullOrWhiteSpace(body)) return Results.BadRequest();

    // Ключ идемпотентности события — хэш содержимого (повторный вебхук не дублируется).
    var dedup = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

    string type = "webhook.unknown";
    string? orderKey = null;
    try
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("event", out var ev)) type = $"webhook.{ev.GetString()}";
        if (doc.RootElement.TryGetProperty("context", out var ctx) && ctx.TryGetProperty("id", out var id))
            orderKey = type.Contains("order", StringComparison.OrdinalIgnoreCase) ? $"order:{id.GetRawText()}" : null;
    }
    catch (JsonException)
    {
        log.LogWarning("Webhook: невалидный JSON, событие сохранено как raw");
    }

    var enqueued = await queue.EnqueueAsync(new SyncEvent
    {
        Type = type,
        PayloadJson = body,
        DedupKey = dedup,
        OrderKey = orderKey,
    });
    log.LogInformation("Webhook {Type} принят (enqueued={Enqueued})", type, enqueued);
    return Results.Ok(new { accepted = true, duplicate = !enqueued });
});

app.Run();

public partial class Program { } // для интеграционных тестов
