namespace IntertexSync.Core.Models;

/// <summary>Коды бизнес-ошибок 1С — контракт 05_API_CONTRACT.md §1.2.</summary>
public enum Sync1CErrorCode
{
    AuthFailed,
    ValidationError,
    SkuNotFound,
    WarehouseNotFound,
    CustomerNotFound,
    OrderNotFound,
    InsufficientStock,
    AlreadyProcessed,
    OrderModified,
    PeriodClosed,
    PostingError,
    CurrencyNotSupported,
    Locked,
    InternalError,
}

public sealed class Sync1CException : Exception
{
    public Sync1CErrorCode Code { get; }
    /// <summary>true — операцию имеет смысл повторить позже (Locked, InternalError).</summary>
    public bool Retryable { get; }
    /// <summary>Детали для человека, напр. дефицит по позициям (sku/requested/available).</summary>
    public IReadOnlyList<string> Details { get; }

    public Sync1CException(Sync1CErrorCode code, string message, bool? retryable = null, IReadOnlyList<string>? details = null)
        : base(message)
    {
        Code = code;
        Retryable = retryable ?? code is Sync1CErrorCode.Locked or Sync1CErrorCode.InternalError;
        Details = details ?? Array.Empty<string>();
    }
}
