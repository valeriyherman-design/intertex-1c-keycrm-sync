using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using IntertexSync.Core.Models;

namespace IntertexSync.Infrastructure.Orders;

/// <summary>
/// Разбор JSON заказа KeyCRM (GET /order/{id}?include=buyer,products.offer,manager,custom_fields)
/// в доменную модель OrderView + детерминированная контрольная сумма состава.
/// Структура полей — по живому снапшоту (docs/05_KEYCRM_ACCOUNT_SNAPSHOT.md §6).
/// </summary>
public static class KeyCrmOrderMapper
{
    public static OrderView Map(JsonDocument doc)
    {
        // GET /order/{id} отдаёт объект; иные ответы могут завернуть в data.
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object)
            root = data;

        var orderId = GetLong(root, "id") ?? throw new FormatException("order.id отсутствует");
        var statusId = (int)(GetLong(root, "status_id") ?? 0);
        var sourceId = (int)(GetLong(root, "source_id") ?? 0);
        var currency = ResolveCurrency(root);

        var buyer = MapBuyer(root);
        long? managerId = null;
        if (root.TryGetProperty("manager", out var mgr) && mgr.ValueKind == JsonValueKind.Object)
            managerId = GetLong(mgr, "id");
        else
            managerId = GetLong(root, "manager_id");

        var items = MapItems(root);
        var checksum = ComputeChecksum(items);

        return new OrderView(orderId, statusId, sourceId, currency, buyer, managerId, items, checksum);
    }

    private static BuyerView MapBuyer(JsonElement root)
    {
        // buyer может прийти вложенным (include=buyer) или только client_id.
        if (root.TryGetProperty("buyer", out var b) && b.ValueKind == JsonValueKind.Object)
        {
            return new BuyerView(
                GetLong(b, "id") ?? GetLong(root, "client_id") ?? 0,
                GetString(b, "full_name") ?? "",
                GetStringArray(b, "phone"),
                GetStringArray(b, "email"));
        }
        return new BuyerView(GetLong(root, "client_id") ?? 0, "", Array.Empty<string>(), Array.Empty<string>());
    }

    private static List<OrderItem> MapItems(JsonElement root)
    {
        var items = new List<OrderItem>();
        if (!root.TryGetProperty("products", out var products) || products.ValueKind != JsonValueKind.Array)
            return items;

        foreach (var p in products.EnumerateArray())
        {
            var sku = GetString(p, "sku") ?? "";
            var qty = GetDecimal(p, "quantity") ?? 0m;
            var price = GetDecimal(p, "price_sold") ?? GetDecimal(p, "price") ?? 0m;
            var discPct = GetDecimal(p, "discount_percent") ?? 0m;
            var sum = GetDecimal(p, "total_price") ?? Math.Round(price * qty, 2);
            items.Add(new OrderItem(sku, qty, price, discPct, sum));
        }
        return items;
    }

    /// <summary>SHA-256 по нормализованному отсортированному составу. Стабильна к порядку строк
    /// И к формату чисел: G29 канонизирует decimal (10 == 10.00, 2 == 2.0) — иначе разный формат
    /// в двух выборках дал бы ложный OrderModified при списании (ТЗ п.10).</summary>
    public static string ComputeChecksum(IReadOnlyList<OrderItem> items)
    {
        static string Num(decimal d) => d.ToString("G29", CultureInfo.InvariantCulture);
        var normalized = items
            .Select(i => $"{i.Sku}|{Num(i.Quantity)}|{Num(i.Price)}")
            .OrderBy(x => x, StringComparer.Ordinal);
        var joined = string.Join(";", normalized);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)));
    }

    private static string ResolveCurrency(JsonElement root)
    {
        if (GetString(root, "currency_code") is { Length: > 0 } c) return c;
        // из первой оплаты, если на заказе валюта не указана
        if (root.TryGetProperty("payments", out var pays) && pays.ValueKind == JsonValueKind.Array)
            foreach (var pay in pays.EnumerateArray())
                if (GetString(pay, "actual_currency") is { Length: > 0 } pc) return pc;
        return "USD"; // по умолчанию (DEC-008); несоответствие проверит процессор
    }

    // ---- helpers ----
    private static long? GetLong(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64()
        : e.TryGetProperty(name, out var s) && s.ValueKind == JsonValueKind.String && long.TryParse(s.GetString(), out var l) ? l
        : null;

    private static decimal? GetDecimal(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal()
        : e.TryGetProperty(name, out var s) && s.ValueKind == JsonValueKind.String && decimal.TryParse(s.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d
        : null;

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static IReadOnlyList<string> GetStringArray(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return Array.Empty<string>();
        if (v.ValueKind == JsonValueKind.String) return string.IsNullOrWhiteSpace(v.GetString()) ? Array.Empty<string>() : new[] { v.GetString()! };
        if (v.ValueKind == JsonValueKind.Array)
            return v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        return Array.Empty<string>();
    }
}
