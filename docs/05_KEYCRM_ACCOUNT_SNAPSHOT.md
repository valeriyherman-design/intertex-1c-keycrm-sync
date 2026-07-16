# 05. Снапшот аккаунта KeyCRM (InterTex-Fabric)

**Дата снятия:** 2026-07-16 · **Метод:** только read-only GET-запросы к
`https://openapi.keycrm.app/v1` (ничего в аккаунте не изменялось).
Все данные ниже — ✅ **факты живого API**.

> 🔐 API-ключ был передан в переписке открытым текстом. Перед боевым запуском
> **перевыпустить ключ** в KeyCRM и ввести новый только в мастере настройки.
> Ключ не хранится в репозитории.

---

## 1. Источники заказов (`GET /order/source`)

| ID | Название | Драйвер | Валюта |
|---|---|---|---|
| 1 | Instagram | other | USD |
| 2 | Viber | other | USD |
| 3 | https://intertex-fabric.com/ | **opencart** | USD |
| 4 | Лендинг | other | USD |
| 6 | Prom | promua (авто-статусы, ТТН) | UAH |
| 7 | Telegram | other | USD |

✅ Уточнение владельца (2026-07-16): вся валюта в 1С и CRM — **USD**; заказы с Prom
(UAH) сотрудники пересчитывают вручную. Интеграция работает в USD без автоконвертации
(DEC-008); заказ с валютой ≠ USD не проводится автоматически → журнал конфликтов.
Есть действующая интеграция сайта на **OpenCart** и **Prom.ua** с автопередачей
статусов/ТТН — интеграция 1С не должна с ними конфликтовать.

## 2. Статусы заказов (`GET /order/status`) — фактическая воронка

| ID | Статус (укр.) | Группа |
|---|---|---|
| 1 | Нове замовлення | 1 (новые) |
| 21 | Потреби визначено | 2 (в работе) |
| 26 | Перевірка наявності | 2 |
| 27 | Передзамовлення у контрагента | 2 |
| 4 | Виставлено рахунок | 2 |
| 3 | Рахунок оплачено | 2 |
| 24 | Передано на збірку | 4 (логистика) |
| 28 | Укомплектовано | 4 |
| 8 | Доставку узгоджено | 4 |
| 11 | Замовлення відправлено | 4 |
| 29 | Отримано | 4 |
| 12 | Виконано | 5 (успех) |
| 25 | Відгук від клієнта | 5 |
| 14 | Недодзвон / не оплатив | 6 (неуспех) |
| 15 | Немає в наявності | 6 |
| 17 | Не влаштувала доставка | 6 |
| 19 | Скасовано | 6 |
| 20 | Не забрали з пошти | 6 |

**Сопоставление с целевой цепочкой 19 статусов (`03_TARGET_WORKFLOW.md`):**
уже есть аналоги для: Новый(1), В работе(21), Проверка резерва(26 «Перевірка
наявності»), Счёт отправлен(4), Оплачен(3), На сборку(24), Собран(28 «Укомплектовано»),
Отправлен(11), Доставлен(29), Выполнен(12), Отмена(19).
**Отсутствуют и требуют создания:** «Зарезервовано», «Часткова оплата» (или через
`payment_status`), «Перевірка перед списанням», «Готово до відправки», «Повернення»,
«Помилка синхронізації». Создаются стандартными средствами KeyCRM (настройки воронки).

## 3. Методы оплаты (`GET /order/payment-method`)

| ID | Название |
|---|---|
| 1 | Оплата наличными (cash) |
| 2 | Банковская карта |
| 3 | Банковский перевод |
| 4 | PayPal |
| 5 | Прочее |
| 6 | Wise |
| 7 | Наличными |
| 8 | Оплата по реквизитам UA71…453 (ФОП) |
| 9 | Evopay |
| 10 | Оплата на счет |

→ Таблица соответствий «метод оплаты ↔ кассовый/банковский документ 1С» будет иметь
10 строк (заполняется со специалистом 1С).

## 4. Службы доставки (`GET /order/delivery-service`)

| ID | Название |
|---|---|
| 1 | Novaposhta |
| 3 | DHL |
| 4 | AutoRegularBus |
| 5 | CourierLudaBus |
| 6 | CourierSashaDHL |
| 7 | DPD_Euro |
| 8 | UkrPoshta_EMS |
| 9 | Самовывоз |

→ Есть международная доставка (DHL, DPD, EMS) — подтверждает потребность в
инвойсе/упаковочном листе для международных отправок (ТЗ п.11).
Novaposhta подключена нативно (в заказе — `city_ref`, `warehouse_ref` НП).

## 5. Каталог

- Вариантов (offers) всего: **~5400** (2700 страниц × 2 при limit=2).
- SKU выглядят как внутренние штрихкоды EAN-13 с префиксом 2000000…
  (`"sku": "2000000310213"`) — совпадает с утверждением ТЗ «уникальные коды и
  штрихкоды уже используются». Поле `barcode` у выборки — `null` (штрихкод живёт в SKU).
- Категории: дерево (кружева «Шантильї», фурнитура: стрічки, гудзики, нитки,
  замочки YKK, чашки, фати и т.д.) — соответствует свадебной тематике.
- Вариант (`offer`): `id`, `product_id`, `sku`, `barcode`, `price`,
  `purchased_price`, `quantity`, `in_reserve`, `properties[{name,value}]`
  (пример: «Характеристика: Молочный») — прямой аналог характеристик 1С.
- Товар (`product`): `unit_type` (сейчас `null` — единицы измерения НЕ заполнены!),
  `is_archived`, `category_id`, `custom_fields`, габариты/вес.

⚠️ **Находка:** `unit_type` пуст у выборочных товаров → метраж тканей сейчас
не формализован в KeyCRM. Для дробных количеств (12.5 м) единицы измерения
надо будет заполнить при синхронизации каталога из 1С.

## 6. Структура заказа (`GET /order?include=...`)

Ключевые поля: `id`, `status_id`, `status_group_id`, `has_reserves`,
`payment_status`, `grand_total`, `products_total`, `discount_*`, `manager`,
`buyer`, `payments[]`, `shipping{}`, `custom_fields[]`, `source_id`,
`ordered_at/updated_at/status_changed_at`, `margin_sum`, `parent_id`.

- Позиция заказа: `sku`, `name`, `price`, `price_sold`, `quantity`, `unit_type`,
  `properties`, `product_status_id`, `comment`, `picture`, `offer`.
- Оплата: `id`, `amount`, `actual_amount`, `source_currency`, `actual_currency`,
  `payment_method_id`, `transaction_uuid`, `is_expense`, `bill_id`.
- Доставка: `delivery_service_id`, `tracking_code`, `address_payload`
  (city_ref/warehouse_ref Новой Почты), `shipping_status`.
- В заказе есть `has_reserves` — флаг резервов KeyCRM.

## 7. Кастомные поля (`GET /custom-fields`) — 10 шт.

| UUID | Модель | Тип | Название |
|---|---|---|---|
| CT_1001 | client | select multi | Сегментація клієнтів по A,B,C |
| CT_1002 | client | select multi | Тип клієнта |
| CT_1003 | client | date | Дата останньої угоди |
| CT_1004 | client | date | Дата останньої успішної угоди |
| CT_1005 | client | text | Адреса сайта |
| CT_1006 | client | text | Соціальні мережі |
| CT_1007 | crm_product | text | Артикул_SKU |
| OR_1008 | order | number | Номер замовлення у WhatsApp |
| LD_1009 | lead | select multi | Краіна |
| LD_1010 | lead | select multi | Потреба |

→ Свободно место под служебные поля интеграции (модель `order`):
`1С: Номер документа`, `1С: GUID`, `1С: Статус проведення`, `1С: Причина помилки`.
Также используются **лиды** (LD_*) — в аккаунте есть воронка лидов.

## 8. Пользователи (`GET /users` — недокументированный, но рабочий эндпоинт)

Всего 12 пользователей, три роли (имена ролей API не отдаёт — сверить в кабинете):

| ID | Имя | Email | role_id | Статус |
|---|---|---|---|---|
| 2 | Valeriy Gherman | voloca2012@gmail.com | 1 | active |
| 3 | Vleriya Valeriy | valeria.intertex@gmail.com | 2 | active |
| 4 | Integrator Zambit | ceo@zambit.biz.ua | 1 | active |
| 5 | Marcel Bohitsoy | marchel.bohitsoy@gmail.com | 1 | active |
| 6 | Вадим Урсуляк | intertex.mag2@icloud.com | **5** | active |
| 7 | Еленa Герман | elena.gherman.intertex@gmail.com | 2 | pending |
| 9 | Yuliia Kuchuk | intertex.j@gmail.com | 1 | active |
| 10 | Marina Shtefanets | marinaintertex@gmail.com | 2 | blocked |
| 11 | Daniel Gherman | gdanik175@gmail.com | 2 | active |
| 12 | Daniela Bobu | danyelaintertex@gmail.com | 2 | blocked |
| 13 | Serhiy Abv | kot1715@gmail.com | 1 | active |
| 14 | Juliana Inter Tex | bunzakuliana@gmail.com | 2 | active |

Предположительно: role_id=1 — админ, role_id=2 — менеджер, role_id=5 — отдельная
роль (Вадим, intertex.mag2 → магазин №2?). ❓ уточнить названия ролей в кабинете.
→ Таблица соответствий «менеджер KeyCRM ↔ пользователь 1С» будет на 12 строк.

## 8а. Допустимые include (сняты с живого API, полные списки)

- **/order:** attachments(.file), tags, buyer, products, products.offer, manager,
  status, payments, expenses, marketing, shipping(.lastHistory, .deliveryService),
  customFields, assigned (+ *Count/*Exists варианты).
- **/buyer:** manager, shipping, company, loyalty, customFields.
- **/offers:** product. · **/products:** customFields.
- **/offers/stocks** и **/users:** include не поддерживают (параметр игнорируется).
- Заказов products.warehouse **нет** → склад позиции заказа через API не виден
  (подтверждает LIM-03/LIM-04).
- Точное число вариантов: **5399** (`last_page` при limit=1).

## 9. Остатки и склады — критические факты API

- `GET /offers/stocks` возвращает: `id` (offer), `sku`, `price`,
  `purchased_price`, `quantity`, `reserve` — **без разбивки по складам**;
  `filter[warehouse_id]` игнорируется (проверено сравнением ответов).
- `PUT /offers/stocks` — **принимает `warehouse_id` (обязателен)** + массив
  `stocks[{offer_id|sku, quantity}]` → **запись остатков по-складская**.
- Эндпоинта списка складов в Open API **нет** (`/warehouse`, `/warehouses`,
  `/storages` → 404). ID складов берутся из кабинета KeyCRM (настройки складов)
  или из `products.warehouse` в заказе.
- В выборке остатков `quantity=0` при `price>0` — остатки в KeyCRM сейчас,
  судя по всему, не ведутся системно → внедрение синхронизации из 1С не будет
  конфликтовать с накопленными данными (уточнить у владельца).

→ Ограничения LIM-03/LIM-04 зафиксированы в `KEYCRM_LIMITATIONS.md`.

## 10. Что осталось снять (проверено 2026-07-16: через Open API НЕ доступно)

- [ ] **Список складов KeyCRM и их ID** — только из кабинета (Настройки → Склады).
      Проверены и отвергнуты: /warehouse(s), /storages, /offers/warehouses,
      /stocks/warehouses, /warehouse-list, /dictionaries/warehouses,
      /order/warehouse, include=products.warehouse — всё 404/not allowed.
- [x] ~~Список пользователей~~ — снят через недокументированный `GET /users` (§8).
- [ ] Названия ролей (role_id 1/2/5) — из кабинета (роль id=5 особенно — сборщик?).
- [ ] Перечень доступных событий вебхуков — из кабинета (Настройки → Вебхуки).
- [ ] Тариф и фактический лимит API — из кабинета.
- [ ] Поддержка дробного количества (12.5) в позиции заказа — проверить безопасным
      тестом на тестовом заказе (Этап 2, с разрешения владельца).
