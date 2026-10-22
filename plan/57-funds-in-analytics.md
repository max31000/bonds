# Задача 57 — Фонды на «Аналитике»: композиция портфеля и сценарии ставок

> **Кому адресовано.** Агент-исполнитель (`bond-implementer`). Задача самодостаточна: контекст —
> этот файл + [CLAUDE.md](../CLAUDE.md) + [docs/CODEBASE-GUIDE.md](../docs/CODEBASE-GUIDE.md)
> (контракт единиц, разделы «Фонды — отдельный домен», «Цена пая — одна точка»).
>
> **Зависимости:** 51–53 (фонды: `funds`, `fund_positions`, `market_index_history` с RGBI
> `yield_fraction`/`duration_years`, `FundOverviewBuilder`, `IncomeLedgerBuilder.ResolveFundPriceAsync`,
> `FundOutlookService` — резолв индекса фонда, `PUT /api/funds/{id}/settings`).
> **Порядок в волне:** единственная задача.
>
> **Рамки (что НЕ делать):** `PortfolioCompositionService.Calculate(holdings)` и
> `RateScenarioService.Compute(holdings, …)` для существующих потребителей НЕ менять по поведению:
> композицию используют `SignalsEngine` (лимит концентрации эмитента) и `PositionVerdictEndpoints`
> (доля эмитента) — фонды туда не подмешивать. Не трогать scatter/траекторию/XIRR-историю. Валюту в
> аналитику не добавлять.

---

## Проблема

«Аналитика» сейчас строго облигационная. Владелец держит облигационные БПИФ (TPAY — флоатеры и
короткие бумаги; TOFZ — ОФЗ, сильно чувствителен к ставке) — их нет ни в композиции портфеля, ни в
сценариях ставок, поэтому картина риска неполная: «−X ₽ при +100 б.п.» не учитывает TOFZ.

## Что сделать

### A. Дюрация фонда (оценка)

1. `funds.duration_override_years DECIMAL(9,4) NULL` — миграция `026_fund_duration_override.sql`
   (ГОДЫ, модифицированная дюрация, ручная оценка владельца); `MigrationIdempotencyTests`.
2. `Bonds.Core/Funds/FundDurationEstimator.cs` (чистая функция) → `FundDurationEstimate { ModifiedDurationYears?, Source ("manual"|"index:RGBI"|null), AsOf?, Warnings[] }`:
   - есть `duration_override_years` → он, Source `manual`;
   - иначе индекс фонда (тот же резолв, что у `FundOutlookService` для IndexYield: `benchmark_index`
     или ОФЗ-направленность → `RGBI`; вынести общий резолвер, не дублировать) с точкой не старше 10 дней
     (старше — использовать с предупреждением `IndexStale`, как в прогнозе): MOEX `DURATION` индекса —
     **Маколей** (дни → годы уже при записи); модифицированная = `D_mac / (1 + y/2)` (ОФЗ — полугодовой
     купон; `y` — `yield_fraction` той же точки). Doc-comment с формулой и почему это оценка
     (фонд активно управляется, дюрация портфеля фонда ≠ индекса);
   - иначе null (фонд флоатеров/коротких без индекса — «чувствительность неизвестна, задайте вручную»).
3. `PUT /api/funds/{id}/settings` принимает `durationOverrideYears` (null = сброс; валидация 0–30 → 422);
   `FundDetailDto.settings` и строка фонда отдают `durationEstimate`.

### B. Композиция с фондами

1. Новая чистая функция `PortfolioCompositionService.CalculateWithFunds(holdings, IReadOnlyList<FundExposure> funds)`
   (`FundExposure { FundId, Ticker, Name, MarketValueRub, ModifiedDurationYears? }`), исходный
   `Calculate` — без изменений. Фонды в разрезах: эмитент — `"Фонд: <name>"`; сектор — `"Фонды облигаций"`;
   тип купона — `"Фонд"`; корзина дюрации — `DurationBucketClassifier.Label(ModifiedDurationYears)` (null →
   «Не определено»). Новый разрез `ByAssetClass` («Облигации» / «Фонды»). Нормализация долей — та же
   (`SharePercent` — ПРОЦЕНТЫ, задокументированное исключение; новых исключений не заводить — новый разрез
   идёт тем же типом `CompositionShare`).
2. `GET /api/analytics/composition?includeFunds=true|false` (по умолчанию **true**): `includeFunds=false` —
   ответ байт-в-байт прежний (+ `byAssetClass: []`, `fundsIncluded: false`). Стоимость фондов — через
   единую цену пая (`FundOverviewBuilder`/`ResolveFundPriceAsync`), позиции qty > 0.

### C. Сценарии ставок с фондами

1. Новая перегрузка `RateScenarioService.Compute(holdings, funds, shifts)`: база (`currentValue`) =
   облигации + фонды; Δ фонда = `−ModDur × Δy × MV` (выпуклость не учитываем — подписать); фонды без
   дюрации входят в базу, но не в Δ. `RateSensitiveValue` — аналогичная перегрузка. Инвариант
   `NewValue == CurrentValue + Δ` — тестом.
2. `GET /api/analytics/rate-scenario?includeFunds=true|false` (default true); ответ дополняется
   `fundsIncluded`, `funds: [{ fundId, ticker, name, marketValueRub, modifiedDurationYears?, durationSource, deltaAtPlus100bpRub? }]`.
   `includeFunds=false` — прежние числа.

### D. Frontend

1. `api/types.ts`/`api/analytics.ts`/`useAnalyticsStore`: параметр `includeFunds`, новые поля.
2. «Аналитика» (`pages/Analytics.tsx`): у блоков «Композиция портфеля» и «Сценарии ставок» —
   переключатель «С фондами / Только облигации» (`SegmentedControl` или `Switch`, состояние в URL
   `?funds=0`), по умолчанию с фондами; показывать только если у пользователя есть фонды. В композиции —
   новый разрез «Класс активов» рядом с существующими. В сценариях — под графиком список фондов: дюрация
   (с источником: «оценка по индексу RGBI» / «задано вручную» / «неизвестна — задайте в карточке фонда»
   со ссылкой `/funds/:id`) и вклад при +100 б.п. Объяснения «?» в `components/charts/explanations.ts`
   дополнить. Dashboard MiniComposition — с фондами (дефолт эндпоинта: это картина всего портфеля); зафиксировать в тесте.
3. Карточка фонда (`pages/FundDetail.tsx`): в блоке настроек — поле «Дюрация (лет), оценка» с
   плейсхолдером из авто-оценки и источником; сохранить/сбросить через PUT settings.
4. Vitest: переключатель уходит в запрос; разрез «Класс активов»; список фондов в сценариях с подписью
   источника дюрации; ручная дюрация в FundDetail отправляет PUT.

### E. Тесты backend

- Юнит `FundDurationEstimator`: RGBI `D_mac` = 1657/365 ≈ 4.5397 г., y = 0.1603 → ModDur = 4.5397/1.08015 ≈ 4.2028;
  ручная перекрывает индекс; устаревший индекс → предупреждение; без индекса и ручной → null.
- Юнит `CalculateWithFunds`: суммы долей = 100 в каждом разрезе, `ByAssetClass`; `Calculate` без фондов
  не изменился (существующие тесты зелёные без правок).
- Юнит сценария: облигации 100 000 (ModDur 2) + фонд 50 000 (ModDur 4) при +100 б.п. → Δ = −2 000 − 2 000 = −4 000,
  база 150 000, DeltaPercent −2.6667 (ПРОЦЕНТЫ, как и было).
- Интеграционные: `/composition` и `/rate-scenario` с `includeFunds` true/false (false = прежний ответ),
  PUT settings c `durationOverrideYears` + 422; миграция 026.

## Критерии приёмки

- [ ] На «Аналитике» композиция и сценарии ставок по умолчанию учитывают фонды; переключатель
      возвращает облигационную картину.
- [ ] TOFZ даёт вклад в сценарий через оценку дюрации по RGBI (подписано как оценка), TPAY — через ручную
      дюрацию или помечен «чувствительность неизвестна».
- [ ] Сигналы концентрации и вердикты не изменились.
- [ ] Существующие тесты не ослаблены; `scripts/pre-push-check.sh --all` — зелёный.
