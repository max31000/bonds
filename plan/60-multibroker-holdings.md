# Задача 60 — Счёт Freedom в сервисе: синк позиций, денег и операций, раздел «Freedom»

> **Кому адресовано.** Агент-исполнитель (`bond-implementer`). Контекст — этот файл, CLAUDE.md,
> docs/CODEBASE-GUIDE.md и дизайн docs/2026-10-23-capital-automation-design.md §2.
>
> **Зависимости:** 58 (credentials/capabilities) и 59 (`IFreedomApiClient`, модели).
> **Порядок в волне:** после 59.
>
> **Рамки:**
> - Облигационный движок, XIRR облигаций, сигналы, `positions`, `instruments` и `operations` НЕ
>   трогать. Freedom живёт в своих таблицах — так же, как фонды и валюта.
> - Существующий single-account путь T-Invest (`GetPrimaryAccountIdAsync`) не ломать. Мультиаккаунт
>   для T-Invest не делать.
> - Торговли нет.

---

## Проблема

Капитал владельца лежит у двух брокеров: T-Invest (~381 тыс. ₽) и Freedom (~$2.2 тыс., ETF и
акции в USD/EUR/GBP). Сервис видит только T-Invest. Нужен read-only раздел Freedom плюс общий итог
капитала в рублях, чтобы дальше (фаза 2) на этом строились корзины и правила.

## Что сделать

### A. Backend

1. **Миграция `030_broker_accounts.sql`.**
   - `accounts`: добавить `broker VARCHAR(16) NOT NULL DEFAULT 'tinvest'`. Уникальность
     `(user_id, broker, broker_account_id)`, если текущий индекс это допускает; проверить 003/014.
   - Существующие запросы `GetPrimaryAccountIdAsync` / `ResolveAccountIdAsync` должны брать
     **только `broker='tinvest'`**. Иначе запись Freedom с меньшим id перехватит «основной» счёт —
     поправить SQL и закрыть тестом.
   - Новые таблицы, у всех `account_id` с FK на `accounts`:

     | Таблица | Колонки | Ключ |
     |---|---|---|
     | `broker_positions` | `ticker VARCHAR(32)`, `name`, `asset_class VARCHAR(16)` (`etf\|stock\|bond\|fund\|other`; Freedom ETF определять по `name` ~ "ETF\|UCITS\|Fund", иначе stock — эвристика, задокументировать), `currency CHAR(3)`, `quantity DECIMAL(20,6)`, `avg_price DECIMAL(20,6)`, `last_price DECIMAL(20,6)`, `market_value DECIMAL(20,4)`, `synced_at` | UNIQUE (account_id, ticker) |
     | `broker_cash` | `currency CHAR(3)`, `amount DECIMAL(20,4)`, `synced_at` | UNIQUE (account_id, currency) |
     | `broker_operations` | `external_id VARCHAR(64)`, `op_date DATETIME`, `type VARCHAR(16)` (`Buy\|Sell\|Deposit\|Withdrawal\|Fee\|Dividend\|Tax\|Other`), `ticker NULL`, `quantity NULL`, `price NULL`, `amount DECIMAL(20,4)` (со знаком: + приход денег, − расход), `currency CHAR(3)`, `raw_type VARCHAR(48)`, `comment VARCHAR(512) NULL` | UNIQUE (account_id, external_id) |

     Все цены и суммы — **в валюте строки**, не в рублях. Это записывается в doc-comment и в
     CODEBASE-GUIDE.
   - Обновить `MigrationIdempotencyTests`.
2. **`FreedomSyncService`** (Infrastructure/Sync):
   - Для **каждого** пользователя с валидным credential `freedom`: `GetUserInfoAsync` → upsert
     `accounts` (broker `freedom`, `broker_account_id` = `trader_systems_id`, name «Freedom»).
   - `GetPortfolioAsync` → upsert позиций и денег. Сверку удалений делать через `SyncGuards`: пустой
     ответ брокера удаляет позиции только со 2-го раза подряд.
   - `GetTradesAsync` (вся история с 2020-01-01) и `GetCashFlowsAsync(500)` → `broker_operations`:
     - сделки: `external_id = "trade:" + id`, Buy/Sell, amount = ∓`summ`;
     - `bank`/`card` (положительные) → Deposit, отрицательные → Withdrawal, `external_id = "cf:" + hash(date|type|sum|comment)`;
     - `commission_for_trades` → Fee, тикер извлекается из comment регуляркой `\(Trade \d+ (buy|sell) ([A-Z0-9.\-]+) \)`;
     - `dividend` → Dividend, `tax` → Tax, прочее → Other с `raw_type`;
     - конвертации валют (`instr_nm` вида `EUR/USD`) → Other, raw_type `fx`.
   - Сбой одного пользователя или одного шага пишется `LogWarning` и не валит остальных.
   - Ключи берутся только через `IUserCredentialStore` и нигде не логируются.
3. **Планировщик.** Вызвать синк Freedom из `SyncCycleService` отдельным шагом **после** T-Invest
   (сбой Freedom не помечает цикл T-Invest неуспешным). Плюс ручной запуск через существующий
   «Обновить данные» (`/api/sync`), если тот вызывает цикл.
4. **Курсы.** Оценке в рублях нужны USD, EUR и GBP, даже если у пользователя этой валюты нет в
   T-Invest.
   - `CurrencyRateRefresher` должен **всегда** вести историю этих трёх: опция
     `CurrencyRateOptions.AlwaysTrackedCurrencies = ["USD","EUR","GBP"]`, идентификаторы CETS
     `USD000UTSTOM`, `EUR_RUB__TOM`, `GBPRUB_TOM` — сверить с MOEX ISS.
   - Курс в рубли — только через `ICurrencyRateLookup`; нет курса → стоимость null + `dataIncomplete`.
5. **API** (`BrokerHoldingsEndpoints`).
   - `GET /api/brokers/freedom/holdings` (404, если у пользователя нет счёта Freedom):

     | Поле | Состав |
     |---|---|
     | `positions[]` | ticker, name, assetClass, currency, quantity, avgPrice, lastPrice, marketValue, unrealizedPnl, unrealizedPnlFraction (ДОЛЯ), marketValueRub |
     | `cash[]` | currency, amount, amountRub |
     | итоги | totalValueRub, totalValueUsd, syncedAt, dataIncomplete |
     | `fees` | feesPaidUsd — Σ Fee за всё время в USD |

   - `GET /api/brokers/freedom/operations?from&to` — журнал.
   - `/api/portfolio/summary`: добавить поле `otherBrokersValueRub` (сумма по всем не-tinvest счетам,
     0 при их отсутствии). **`totalRub` не менять** — его смысл (T-Invest) используют сверка и XIRR.
     Новое поле `capitalTotalRub = totalRub + otherBrokersValueRub`.
6. **Тесты.**
   - Юнит: маппинг операций — каждый `type_code` из фикстуры, извлечение тикера из comment, знаки,
     идемпотентность external_id.
   - Интеграционные: синк с подменённым `IFreedomApiClient` на фикстурах кладёт строки; повторный синк
     не дублирует; основной счёт T-Invest остаётся тем же при существующем счёте Freedom;
     holdings-эндпоинт считает рубли по засеянному курсу; 404 без счёта; изоляция пользователей.

### B. Frontend

1. Если `capabilities.freedom` — включено:
   - на «Позициях» появляется вкладка **«Freedom»** (`?tab=freedom`). Таблица: тикер, название,
     кол-во, средняя, текущая, стоимость (в валюте), P&L %, стоимость ₽. Внизу — деньги по валютам.
     На мобильной ширине — карточки.
   - на «Обзоре» добавляются KPI **«Freedom»** (₽ и $) и **«Капитал всего»** (`capitalTotalRub`).
2. Если `capabilities.freedom` выключено — вкладки и KPI нет, а в «Подключениях» есть подсказка.
3. Форматтеры: валюта позиции — по `currency`; суммы в валюте выводить с кодом валюты, а не со знаком ₽.
4. Vitest (MSW):
   - вкладка видна только при capability;
   - таблица рендерит позиции в USD, EUR и GBP;
   - при `dataIncomplete` есть пометка;
   - KPI «Капитал всего».

## Критерии приёмки

- [ ] При ключах Freedom после синка видны все позиции и деньги счёта, а стоимость в ₽ совпадает с
  ручным пересчётом по курсам MOEX.
- [ ] Основной счёт T-Invest и все существующие страницы работают как прежде (регрессия: полный прогон
  тестов).
- [ ] Повторный синк идемпотентен; сбой Freedom не ломает синк T-Invest.
- [ ] Ключи нигде не логируются и не уходят на фронт.
- [ ] `scripts/pre-push-check.sh --all` — зелёный.
