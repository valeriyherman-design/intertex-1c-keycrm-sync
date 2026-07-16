namespace IntertexSync.Core.Models;

public sealed record HealthInfo(string Status, string Config, string Version, DateTime Time);

public sealed record Page<T>(IReadOnlyList<T> Data, int PageNumber, int Limit, int Total);

public sealed record Warehouse1C(string Guid, string Code, string Name, bool IsActive);

public sealed record ProductVariant1C(
    string? CharacteristicGuid,
    string? Characteristic,
    string Sku,
    string? Barcode,
    decimal PurchasedPrice,
    IReadOnlyList<PriceRow1C> Prices);

public sealed record PriceRow1C(string PriceType, decimal Price, string Currency);

public sealed record Product1C(
    string Guid,
    string Code,
    string Name,
    string? Category,
    string Unit,
    bool UnitAllowsFraction,
    bool IsArchived,
    IReadOnlyList<ProductVariant1C> Variants);

public sealed record StockRow1C(string Sku, string WarehouseGuid, decimal Quantity, decimal Reserved, decimal Available);

public sealed record CustomerRequest(
    long KeycrmBuyerId,
    string FullName,
    IReadOnlyList<string> Phones,
    IReadOnlyList<string> Emails,
    string? Comment);

public sealed record CustomerResult(string Guid, bool Created, string MatchedBy);

public sealed record OrderItem(
    string Sku,
    decimal Quantity,          // дробное для метража: 12.5
    decimal Price,
    decimal DiscountPercent,
    decimal Sum);

public sealed record OrderRequest(
    long KeycrmOrderId,
    string CustomerGuid,
    string WarehouseGuid,
    long? ManagerKeycrmId,
    string Currency,           // всегда "USD" (DEC-008), иное — CURRENCY_NOT_SUPPORTED
    string? Comment,
    IReadOnlyList<OrderItem> Items,
    string ItemsChecksum);

public sealed record DocumentRef(string Guid, string Number, DateTime Date, bool Posted);

public sealed record ReturnItem(string Sku, decimal Quantity);

public sealed record ReturnRequest(
    long KeycrmOrderId,
    IReadOnlyList<ReturnItem> Items,
    string? Reason,
    bool FullReturn);

public sealed record PaymentRequest(
    long KeycrmPaymentId,
    long KeycrmOrderId,
    decimal Amount,
    string Currency,
    string Method,
    DateTime Date);

public sealed record ReserveState(bool Active, decimal QuantityTotal);

public sealed record OrderState1C(
    DocumentRef? Order,
    ReserveState? Reserve,
    DocumentRef? Realization,
    IReadOnlyList<DocumentRef> Returns,
    IReadOnlyList<(string Guid, decimal Amount)> Payments);

public sealed record TransferItem1C(string Sku, decimal Quantity);

public sealed record Transfer1C(
    string Guid,
    string Number,
    DateTime Date,
    string WarehouseFromGuid,
    string WarehouseToGuid,
    IReadOnlyList<TransferItem1C> Items);
