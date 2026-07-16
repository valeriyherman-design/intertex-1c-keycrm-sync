namespace IntertexSync.Infrastructure.Orders;

/// <summary>
/// Настройки оркестрации заказа. Маппинг статусов KeyCRM (реальные id из снапшота §2)
/// на действия и целевые статусы. Для боевого режима значения задаются в конфиге.
/// </summary>
public sealed class OrderSyncOptions
{
    /// <summary>ГЕЙТ: true — X-Dry-Run в 1С + без записи в живой KeyCRM (безопасная репетиция).</summary>
    public bool DryRun { get; set; } = true;

    /// <summary>Склад отгрузки по умолчанию (GUID 1С). DEC-010: Магазин №4 (Чагор). Для live обязателен.</summary>
    public string DefaultWarehouseGuid { get; set; } = "";

    // --- Входящие статусы-триггеры (id KeyCRM, снапшот §2) ---
    public int[] ReserveTriggerStatuses { get; set; } = { 26 }; // «Перевірка наявності»
    public int[] ShipTriggerStatuses { get; set; } = { 28 };    // «Укомплектовано»
    public int[] CancelTriggerStatuses { get; set; } = { 19 };  // «Скасовано»

    // --- Целевые статусы, которые ставит сервис (создаются в KeyCRM) ---
    public int StatusReserved { get; set; }      // «Зарезервовано»
    public int StatusReadyToShip { get; set; }   // «Готово до відправки»
    public int StatusSyncError { get; set; }     // «Помилка синхронізації»

    // --- UUID кастомных полей заказа для служебных данных 1С (создаются в KeyCRM) ---
    public string CfOneCDocNumber { get; set; } = ""; // «1С: Номер документа»
    public string CfSyncError { get; set; } = "";     // «1С: Причина помилки»
}
