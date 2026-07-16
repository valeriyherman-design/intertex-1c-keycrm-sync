namespace IntertexSync.Core.Models;

/// <summary>Разобранный заказ KeyCRM (то, что нужно для оркестрации в 1С).</summary>
public sealed record OrderView(
    long OrderId,
    int StatusId,
    string Currency,
    BuyerView Buyer,
    long? ManagerId,
    IReadOnlyList<OrderItem> Items,
    string ItemsChecksum);

public sealed record BuyerView(
    long Id,
    string FullName,
    IReadOnlyList<string> Phones,
    IReadOnlyList<string> Emails);

/// <summary>Результат обработки одного события заказа.</summary>
public sealed record OrderProcessResult(
    long OrderId,
    string Action,
    OrderOutcome Outcome,
    int? NewKeyCrmStatusId,
    string? OneCDocNumber,
    string? OneCDocGuid,
    string? ErrorReason,
    bool DryRun);

public enum OrderOutcome { Success, InsufficientStock, ValidationFailed, CurrencyNotSupported, OneCError, Skipped }
