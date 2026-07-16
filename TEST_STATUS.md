# TEST_STATUS — Состояние тестирования

Обновлено: 2026-07-16 · Команда: `dotnet test` из корня.

## Автоматические тесты: 35/35 ✅ (0 предупреждений сборки)

### Нормализация названий (NameNormalizerTests, 17 — DEC-013)
| Проверка | Статус |
|---|---|
| Двойная точка `..`→`.`, двойные пробелы, trim, пробел перед знаком, скобки | ✅ |
| Чистые названия (артикулы с точками, `_(1)`, коды-заглушки) не меняются | ✅ |
| Идемпотентность, пустые/`null` | ✅ |
| **Все 24 реальных «грязных» названия каталога → без аномалий** | ✅ |



### Очередь и надёжность (QueueTests, 6)
| Тест | Сценарий ТЗ | Статус |
|---|---|---|
| Enqueue_DuplicateDedupKey_IsRejected | п.18.9 повторный вебхук | ✅ |
| Dequeue_SameOrder_NotParallel | п.15 блокировка параллельной обработки заказа | ✅ |
| MarkFailed_Retryable_SchedulesBackoff | п.15 экспоненциальная задержка | ✅ |
| MarkFailed_NonRetryable_GoesDead + ручной Retry | п.15 ручной повтор | ✅ |
| Idempotency_SecondSave_KeepsFirstResult | п.15 идемпотентность | ✅ |
| Mappings_SetGet_Roundtrip | таблицы соответствий | ✅ |

### Лимит KeyCRM (RateLimiterTests, 2)
| Тест | Сценарий | Статус |
|---|---|---|
| AllowsUpToLimit_ThenWaits | LIM-02: 60 rpm скользящее окно | ✅ |
| ParallelCallers_NeverExceedLimit | конкурентный доступ | ✅ |

### Учётные инварианты 1С на моке (Mock1CTests, 10)
| Тест | Сценарий ТЗ п.18 | Статус |
|---|---|---|
| Fabric_FractionalQuantity_12_5m | №5 ткань 12.5 м | ✅ |
| InsufficientStock_NoPartialReserve | №8 нехватка, без частичного резерва | ✅ |
| RepeatedReserve_SameKey_NoDoubleReserve | №9 повторный вебхук резерва | ✅ |
| RepeatedShip_NoDoubleWriteOff | №9/№12 повторное списание | ✅ |
| Ship_ModifiedOrder_Rejected | №13 изменение заказа после резерва | ✅ |
| NonUsdCurrency_Rejected | DEC-008 Prom вручную | ✅ |
| Unreserve_Idempotent | №16 отмена/снятие резерва | ✅ |
| Return_RestoresStock_KeepsRealization | №18 возврат без удаления реализации | ✅ |
| Payment_Duplicate_NoDuplicate | п.12 дубль оплаты | ✅ |
| TwoManagers_SameStock_OnlyOneWins | №19 два менеджера, один товар | ✅ |

## Ручной дымовой тест сервиса (2026-07-16) ✅

- `GET /health` — очередь + состояние 1С (mock) ✅
- Вебхук с неверным секретом → 404 ✅
- Вебхук валидный → `{accepted:true, duplicate:false}`, мгновенный 200 ✅
- Тот же вебхук повторно → `duplicate:true`, в очередь не попал ✅
- QueueWorker обработал событие, `lastSuccess` обновился ✅
- Штатная остановка/перезапуск — событий не потеряно (Processing переподбирается) ✅

## Не покрыто (ожидает следующих этапов / доступов)

- Интеграционные тесты с реальной 1С (нужна тестовая база — BLK-003).
- COM-драйвер на Windows (нужна Windows-машина с платформой 1С).
- Живые вебхуки KeyCRM (нужен публичный HTTPS + настройка в кабинете).
- Нагрузочный тест 10 000+ товаров (Этап 8).
- 30 приёмочных сценариев ТЗ п.18 — по мере реализации Этапов 3–6.
