# Задача 59 — Коннектор Freedom (Tradernet API v3) и модель комиссий, только чтение

> **Кому адресовано.** Агент-исполнитель (`bond-implementer`). Контекст — этот файл + CLAUDE.md +
> docs/CODEBASE-GUIDE.md + дизайн [docs/2026-10-23-capital-automation-design.md](../docs/2026-10-23-capital-automation-design.md) §2.5.
> Эталонная Python-реализация, проверенная на живом счёте, лежит вне репо: `M:\Projects\trade\tn\client.py`, `tn\fees.py`.
> Ниже переписано всё нужное из неё.
>
> **Зависимости:** задача 58 (реестр провайдеров, `ICredentialValidator`, `IUserCredentialStore`).
> **Порядок в волне:** после 58.
>
> **Рамки:** только чтение. `putTradeOrder` / `delTradeOrder` **не реализовывать** — это фаза 3, отдельная
> задача с собственным слоем безопасности. Синк в БД и UI делает задача 60. Реальные данные счёта в репо не
> класть: репозиторий публичный, фикстуры в `tests/Bonds.Tests/Fixtures/Freedom/` уже синтетические.

---

## Что сделать

### A. Клиент (`Bonds.Infrastructure/Connectors/Freedom/`)

1. **`FreedomApiClient : IFreedomApiClient`** — именованный HttpClient `FreedomApiClient.HttpClientName`,
   базовый URL — `FreedomOptions.BaseUrl` (дефолт `https://tradernet.global`). Ключи приходят параметром
   `FreedomCredentials(PublicKey, PrivateKey)` в каждый вызов: клиент без состояния, мультипользовательский.
2. **Подпись (API v3).** Запрос — `POST {BaseUrl}/api/{cmd}`.
   - Тело: компактный JSON параметров (`{"a":1}`, без пробелов, порядок ключей как передан; пустые
     параметры — `{}`). Это **строка**, отправляется как есть, `Content-Type: application/json`.
   - Заголовки:
     - `X-NtApi-PublicKey: <public>`;
     - `X-NtApi-Timestamp: <unix seconds>`;
     - `X-NtApi-Sig: hex(lowercase(HMAC-SHA256(key = privateKey UTF-8, msg = body + timestamp)))`.
   - Эталонные векторы, обязательны в юнит-тесте:
     - key `test-private-key`, body `{"tickers":"VUAA.EU,SPYM.US"}`, ts `1700000000` →
       `576ce1445e7d3db2987b9b5c22eea0b339777c05bbb9baa410a8cd13e8e8e70b`;
     - key `test-private-key`, body `{}`, ts `1700000000` →
       `a292e5ed5038526bdd857ff5783fff021fc1562e48dc95a7b7a623b775889cde`.
   - Время берётся через `TimeProvider` (для тестов).
3. **Ошибки.** HTTP не-2xx или JSON с полем `errMsg` / `error` → `FreedomApiException(cmd, message)`.
   В сообщении нет ключей и подписи. Ключи и заголовки не логировать (LogDebug только cmd).
4. **Методы.** Формы ответов — по фикстурам. Модели — `sealed record` в `Connectors/Freedom/Models`.
   - `GetUserInfoAsync` → `GetAllUserTexInfo`: `data.trader_systems_id` (номер счёта), `data.tariffDetails.{id,name}`.
     Name может быть коротким (`"Standard"`) — сравнивать тариф по **id**.
   - `GetPortfolioAsync` → `getPositionJson`:
     - `result.ps.acc[]`: `curr`, `s` = остаток денег в этой валюте;
     - `result.ps.pos[]`: `i` тикер, `name`, `q` кол-во (единицы), `curr` валюта бумаги, `bal_price_a`
       средняя цена, `mkt_price` текущая (обе в валюте бумаги), `market_value` (валюта бумаги).
     - **Поле `currval` (рублей за единицу валюты) у брокера бывает устаревшим — для оценки не
       использовать**, только курсы bonds (задача 60). Отметить в doc-comment.
   - `GetQuotesAsync(tickers)` → `getStockQuotesJson`, params `{"tickers":"A,B"}` (через запятую):
     `result.q[]`: `c` тикер, `bbp` bid, `bap` ask, `ltp` last, `x_curr`, `min_step`.
   - `GetTradesAsync(from, to)` → `getTradesHistory`, params `{"beginDate":"yyyy-MM-dd","endDate":"yyyy-MM-dd","max":1000}`:
     `trades.trade[]`: `id`, `order_id`, `date`, `instr_nm`, `type` ("1" buy / "2" sell), `q`, `p`, `summ`,
     `curr_c`, `commission`, `commission_currency`. Числа приходят **строками** — парсить
     InvariantCulture. Пустая история приходит не массивом — обработать.
   - `GetCashFlowsAsync(take)` → `getUserCashFlows`, params `{"take":N}`: `cashflow[]`: `type_code`
     (`bank`, `card` — пополнения; `commission_for_trades`, `dividend`, `tax` и др.), `date`, `currency`,
     `sumRaw` (строка, знак: + приход / − расход), `comment` (у комиссии содержит `(Trade 900000123 buy VUAA.EU )` и
     строку «Рынок: europe|kazakhstan_its|usa…, тип бумаги: stocks|funds»).
   - `FindAsync(text)` → `tickerFinder` `{"text":...}`: `found[]`: `t`, `nm`, `mkt`, `codesub`, `x_curr`.
   - Тикеры `*.ITS` для этого типа счёта **только котировочные**: торговля по ним отклоняется («instrument
     is not available for trading»), торговать нужно `*.US`, а маршрут на ITS выбирает брокер. Записать в
     doc-comment `FindAsync` и в CODEBASE-GUIDE.
5. **Валидатор** `freedom` (регистрируется в реестре задачи 58: поля `publicKey` (маска), `privateKey`):
   вызывает `GetUserInfoAsync`. Ok → meta `{ accountNumber, tariffId, tariffName }`. Ошибка →
   invalid с безопасным сообщением.

### B. Модель комиссий (`Bonds.Core/Fees/FreedomFeeModel.cs`, чистая)

Формула сверена по выписке до цента:
`fee = max(pct × amountUsd + perOrder + perUnit × qty, minTrade)`. `perOrder` берётся **один раз на заявку**:
у заявки, исполненной двумя сделками, $1.2 списан только в первой.

| Тариф (id) | Рынок | pct | perOrder $ | perUnit $ | minTrade $ |
|---|---|---|---|---|---|
| Investor (1430) | usa, europe | 0.005 | 1.2 | 0.012 | 0 |
| Investor | kazakhstan_its (акции) | 0.005 | 0 | 0.012 | 0.01 |
| Investor | kazakhstan, kazakhstan_aix | 0.00085 | 0 | 0 | 0 |
| Standard (1431) | usa, europe | 0.0012 | 1.2 | 0 | 0 |
| Standard | kazakhstan_its акции | 0.0012 | 0 | 0 | 0.01 |
| Standard | kazakhstan_its **funds (ETF)** | 0.005 | 0 | 0 | 0 |
| любой | otc | 0.02 | 0 | 0 | 25 |

Все ставки — **ДОЛИ**, как требует контракт единиц.

- **Маршрут.** `.EU` → europe. `.US` → kazakhstan_its, если у бумаги есть двойник на ITS (параметр
  `itsTwin: bool`), иначе usa. ETF — класс funds.
- **Эталоны** (юнит, из реальной выписки; суммы в USD, EUR/GBP пересчитаны курсом из строки выписки):
  - Investor: COPX.EU 2×70.12 → 1.92; NDIA.EU 6×8.803 → 1.53; NUKL.EU 2×(49.95×1.1607) → 1.80;
    SJPA.EU 2×(60.51×1.3441) → 2.03; LUV.US via ITS 2×42.90 → 0.45; NVDA.US via ITS 1×191.03 → 0.97;
    OTGLY.US usa 9×17.54 → 2.10.
  - Standard: VUAA.EU 3×147.70 → 1.73; SPYM.US via ITS funds 1×89.41 → 0.45.
  - Допуск ±0.01: брокер округляет итог.
- **Правило доливки ETF.** `FreedomFeeModel.CheaperRoute(amountUsd)`: ITS-funds 0.5% против Europe
  0.12% + $1.2. Точка безубыточности ≈ $315.79. Тест: `CheaperRoute(300)` → ITS, `CheaperRoute(400)` → Europe.

### C. Тесты

- Подпись — векторы выше.
- Парсинг каждой фикстуры (`Fixtures/Freedom/*.json`, уже лежат в репо): числа-строки, пустая история,
  тело с `errMsg` → исключение.
- `FakeHandler` проверяет заголовки и тело запроса: точное тело, метод POST, путь `/api/{cmd}`.
- Модель комиссий — эталоны.
- CODEBASE-GUIDE: строки таблицы единиц для Freedom (цены и market_value — в валюте бумаги; `currval`
  не использовать; суммы cashflow — в своей валюте со знаком) + абзац «Freedom-коннектор».

## Критерии приёмки

- [ ] Подпись совпадает с эталонными векторами.
- [ ] Все фикстуры парсятся; ошибки брокера дают `FreedomApiException` без секретов.
- [ ] Модель комиссий воспроизводит все эталоны с точностью ±0.01.
- [ ] Валидатор `freedom` подключён к «Подключениям» задачи 58: карточка Freedom появляется в UI
  (проверить vitest-ом по MSW-ответу или скриншотом в отчёте).
- [ ] `scripts/pre-push-check.sh --all` — зелёный.
