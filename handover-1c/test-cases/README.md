# Тестовые сценарии обработки 1С

Готовые запросы к обработке `ИнтеграцияKeyCRM.epf` (вызов через COM:
`Обработка.ВыполнитьОперацию(Операция, АргументыJSON)`). Прогонять на **тестовой базе**.
Полный контракт и коды ошибок — `../05_API_CONTRACT.md`.

## Как прогонять (PowerShell пример)
```powershell
$conn = (New-Object -ComObject "V83.COMConnector").Connect('File="…\ITX_UTP_ТЕСТ";Usr="ИнтеграцияKeyCRM";Pwd="***"')
$proc = $conn.ВнешниеОбработки.Создать("D:\Integration\ИнтеграцияKeyCRM.epf")
# пример: резерв
$proc.ВыполнитьОперацию("reserve", '{"keycrmOrderId":900001,"warehouseGuid":"<GUID склада>","idempotencyKey":"t-res-1","dryRun":true}')
```
Все запросы ниже — с `"dryRun":true` (репетиция без проведения). Для боевой проверки
проводок — повторить с `"dryRun":false` на тестовой базе.

## Матрица сценариев (`cases.json` — машиночитаемо)

| # | Операция | Что проверяет | Ожидаемо |
|---|---|---|---|
| 1 | health | связь, версия конфигурации | `success:true`, config |
| 2 | warehouses | 6 складов с GUID | 6 элементов |
| 3 | products (page 1) | каталог + характеристики + штрихкоды + цены | массив, meta.total |
| 4 | stocks | остатки/резервы по складу | sku/quantity/reserved/available |
| 5 | upsert_customer (новый) | создание контрагента | `created:true`, guid |
| 6 | upsert_customer (существующий телефон) | анти-дубли | `created:false`, `matchedBy:"phone"` |
| 7 | upsert_order (шт) | заказ поштучно | guid/number |
| 8 | upsert_order (ткань 12.5 пог.м.) | дробное количество | принимается 12.5 |
| 9 | upsert_order (с характеристикой) | цвет | характеристика в ТЧ |
| 10 | reserve | резерв достаточного остатка | остатки по позициям, reserved |
| 11 | reserve (нехватка) | недостаточный остаток | `INSUFFICIENT_STOCK` + details по позициям |
| 12 | reserve (повтор) | идемпотентность | без второго резерва |
| 13 | unreserve | снятие резерва | `success:true` |
| 14 | ship | реализация + проведение | number, `posted:true` |
| 15 | ship (изменён состав) | сверка чексуммы | `ORDER_MODIFIED` |
| 16 | ship (повтор) | без двойного списания | тот же документ |
| 17 | create_return (частичный) | возврат, реализация не удалена | number, posted |
| 18 | register_payment | ПКО по заказу | guid; повтор → тот же |
| 19 | order_state | состояние документов заказа | order/reserve/realization |
| 20 | reserve (закрытый период) | отказ проведения | `PERIOD_CLOSED` |

Файл `cases.json` содержит конкретные `{name, operation, args, expected}` для
автоматического/ручного прогона. GUID складов и реальные SKU подставить из вашей базы
(плейсхолдеры `<WH_GUID>`, `<SKU_шт>`, `<SKU_ткань>`).
