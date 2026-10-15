# Задача 46 — Скоркарта надёжности: три слоя, светофор v2, разделение Risk/Value

> **Кому адресовано.** Агент-исполнитель (`bond-implementer`). Задача самодостаточна: контекст —
> этот файл + [CLAUDE.md](../CLAUDE.md) + [docs/CODEBASE-GUIDE.md](../docs/CODEBASE-GUIDE.md).
> Это центральная задача цикла (docs/2026-10-analytics-quality-upgrade.md, §7.1).
>
> **Зависимости:** 42 (EffectiveGspread, история), 43 (`security_details`, дефолт-агрегат,
> emitent_inn), 44 (IQR-порог, ликвидность), 45 (`issuer_financials`).
> **Порядок в волне:** после 45. Задача 47 потребляет DTO этой задачи.
>
> **Рамки (что НЕ делать):** UI-разбивка — задача 47 (здесь только DTO); вердикт позиции и
> события — задача 49; ранжирование кандидатов — задача 50. Существующий параметр
> `reliability=` в API и значения Green/Yellow/Red сохранить (меняется наполнение, не контракт).

---

## Проблема

Светофор T-38 (`CandidateRiskSignalService`) видит только микроструктуру (ликвидность, листинг,
спред к медиане корзины) и ничего не знает об эмитенте. Хуже: ось спреда склеивает цену с
риском — «дешёвая» бумага (спред > медианы на 20+ б.п.) автоматически Red, поэтому топ
RV-кандидатов красный, а фильтр `recommended` аллокации исключает именно дешёвые бумаги.
По литературе (Giesecke et al., NBER w15848) уровень спреда — слабый предиктор дефолта; сильные
бесплатные предикторы на рынке РФ (исследование 962 эмитентов): дефолт-история, уровень
листинга, размер выручки, наличие оферты. Все данные уже в системе после задач 42–45.

## Что сделать

### A. Скоркарта (чистый сервис в Bonds.Core)

1. `Bonds.Core/Analytics/ReliabilityScoreService.cs` — чистая функция:
   вход `ReliabilityScoreInput` (всё nullable), выход `ReliabilityScore`:
   - `Components: IReadOnlyList<ScoreComponent>` — `record ScoreComponent(string Key,
     string Title, decimal? RawValue, string? RawDisplay, int Points, int MaxPoints,
     string Source, bool Missing)`;
   - `Score 0–100` = `earned / available × 100`, где available — сумма MaxPoints компонентов с
     данными; `Coverage 0–1` = `available / totalMax`;
   - `Level` (Green/Yellow/Red) и `CriticalFlags: string[]`.
2. **Компоненты и баллы** (все пороги — именованные константы в `ReliabilityScoreOptions` с
   doc-comment «эвристика по литературе/статистике РФ, калибровка — BACKLOG О2»):

   Слой A — структурный (max 45: 12+15+5+3+10; при наличии ручного рейтинга +10 → 55; в первой версии плана
   было ошибочно «40/50» — сумма компонентов считается по компонентам, Score нормируется по доступным баллам):
   - `issuer_default_history` (0/12): эмитент не дефолтил ни по одному выпуску (агрегат задачи
     43). Дефолт → **критический флаг** `IssuerDefaulted`; техдефолт без дефолта → 4 балла и флаг
     `IssuerTechDefault`.
   - `listing` (0/8/15): уровень 1 → 15, 2 → 8, 3/нет → 0.
   - `no_offer` (0/5): нет неисполненной оферты → 5 (оферта как структурный фактор риска,
     дефолтность с офертой ×4.5; календарное событие оферты остаётся в SignalsEngine).
   - `not_qual_only` (0/3): не «только для квалов» → 3.
   - `broker_risk_level` (0/5/10): T-Invest risk_level Low → 10, Moderate → 5, High → 0,
     Unspecified/нет → Missing.

   Слой B — фундаментальный (max 40), по последнему году `issuer_financials`, source
   'manual' приоритетнее 'girbo':
   - `revenue_size` (0/4/8): ≥ 4 млрд ₽ → 8; 1–4 млрд → 4; < 1 млрд → 0.
   - `interest_coverage` (0/2/5/8): 2200/2330 ≥ 3 → 8; 2–3 → 5; 1–2 → 2; < 1 → 0 +
     **критический флаг** `InterestUncovered`. 2330 = 0/null при ненулевом долге → Missing.
   - `leverage` (0/2/5/8): (1410+1510−1250)/1300 < 1 → 8; 1–2 → 5; 2–4 → 2; ≥ 4 → 0.
     Капитал ≤ 0 → 0 + **критический флаг** `NegativeEquity`.
   - `current_ratio` (0/2/4): 1200/1500 ≥ 1.5 → 4; 1–1.5 → 2; < 1 → 0.
   - `trend` (0/3/6): по 3 годам — выручка и чистая прибыль растут → 6; смешанно → 3; обе
     падают или прибыль < 0 последний год → 0. Меньше 2 лет данных → Missing.
   - `altman_z_em` (0/3/6): Z″-EM = 3.25 + 6.56·X1 + 3.26·X2 + 6.72·X3 + 1.05·X4, где
     X1=(1200−1500)/1600, X2=1370/1600, X3=2200/1600, X4=1300/(1600−1300).
     Коэффициенты сверены с первоисточником (Altman, Hartzell, Peck 1995; Altman 2005 «An emerging market
     credit scoring system», Emerging Markets Review): совпадают (константа 3.25 сдвигает шкалу так, что 0 = рейтинг D).
     **Расхождение найдено в порогах:** исходные зоны Z″ 2.6 / 1.1 относятся к формуле БЕЗ константы 3.25; для Z″-EM
     (с константой) пороги сдвигаются на 3.25 → **≥ 5.85 → 6; 4.35–5.85 → 3; < 4.35 → 0** (5.85 — к тому же нижняя
     граница категории BBB в рейтинговой шкале Altman&Hotchkiss 2005). С порогами 2.6/1.1 к формуле с константой
     почти любой эмитент получал бы максимум. X3 по строке 2200 — приближение EBIT.
   - Вся отчётность старше 20 месяцев (нет подачи за прошлый год к октябрю) → все компоненты
     слоя B получают пометку `Stale` (в RawDisplay), баллы считаются, но Coverage слоя
     умножается на 0.5 (константа).
   - Слой B Missing при ЛЮБОМ статусе ГИР БО ≠ ok (cb, not_found, hidden, error, нет записи) — если нет
     ручной отчётности (source='manual' используется при любом статусе).

   Слой C — рыночный (max 20), по `bond_universe_history` (появилась в 42):
   - `spread_momentum` (0/3/8): G-спред бумаги сегодня − его медиана за 20 торговых дней:
     ≤ +20 б.п. → 8; +20…+50 → 3; > +50 б.п. → 0 + **критический флаг** `SpreadShock`.
     Меньше 10 дней истории → Missing.
   - `price_drawdown` (0/3/6): падение цены от максимума за 20 дней < 2% → 6; 2–5% → 3; > 5% →
     0 + флаг `PriceDrawdown` (не критический: для длинных бумаг это может быть ставка).
   - `liquidity` (0/3/6): ось ликвидности задачи 44: Good → 6; Neutral → 3; Caution → 0.

   **G-спред и перцентиль корзины в риск-скоринг НЕ входят** — это ось Value.
3. **Маппинг на светофор:**
   - Red: `IssuerDefaulted` **или** `InterestUncovered` **или** `NegativeEquity` **или**
     `SpreadShock` **или** (liquidity Caution **и** оборот-медиана < 300 тыс.) **или**
     Score < 35;
   - Green: Score ≥ 65 **и** Coverage ≥ 0.6 **и** листинг ∈ {1,2} **и** нет критических флагов **и** (для
     несуверенных) есть хотя бы один компонент слоя B («Green — при живом фундаментале», docs §7.1: иначе банк
     с полным набором A+C набирал бы Coverage 0.619 ≥ 0.6 без всякой отчётности);
   - иначе Yellow. Гос/муни-бумаги: слой B всегда Missing, бонус-компонент `sovereign`
     (0/15) вместо него (Гособлигации → 15, Муниципальные → 10) — чтобы ОФЗ не падали в Yellow
     из-за Coverage.
4. Юнит-тесты сервиса — **эталонные числа руками** на 6–8 профилях: здоровый корпорат L1;
   ВДО с покрытием 1.5 и выручкой 0.8 млрд; эмитент с техдефолтом; банк (слой B Missing);
   ОФЗ; бумага со спред-шоком; скрытая отчётность + ручной ввод.

### B. Сборка входа и интеграция

1. `AnalyticsSupport.AssessEntryRiskSignals` → собирает `ReliabilityScoreInput` из:
   `bond_universe` (+42: effective gspread, 43: emitent), `security_details` + дефолт-агрегат
   (43), `issuer_financials` (45), `instrument_risk_levels` (см. C), истории (42). Батчево, без
   N+1 (образец — существующие батчевые чтения). Старый `CandidateRiskSignalService.Aggregate`
   заменить вызовом скоркарты; сами `CandidateRiskSignals` (Liquidity/Spread) оставить как
   value/ликвидность-сигналы DTO.
2. DTO (additive): к `reliability` добавить `score`, `coverage`, `components[]`,
   `criticalFlags[]`, `valueVerdict` (RV Cheap/Fair/Rich/InsufficientData — отдельным полем).
   Контракт `reliability=GREEN|YELLOW|RED` фильтров не меняется.
3. `recommended`-аллокация: фильтр «не Red» по новому светофору; **перестать исключать
   Spread=Caution** (дешевизна — не риск). Liquidity Caution остаётся исключающим.

### C. risk_level из T-Invest

1. `ITInvestPortfolioClient` (или соседний клиент инструментов): метод bulk `Bonds()`
   (`INSTRUMENT_STATUS_BASE`) → пары (ISIN, risk_level). Поле `Bond.risk_level` подтверждено
   официальным proto (instruments.proto:502, enum 0–3).
2. Миграция `020_instrument_risk_levels.sql` (+ `MigrationIdempotencyTests`): таблица
   `instrument_risk_levels` (`isin VARCHAR(12) PK`, `risk_level TINYINT NOT NULL`,
   `fetched_at DATETIME NOT NULL`) — отдельно от instruments, чтобы покрывать весь банк.
   Обновление — раз в сутки в sync-цикле (один bulk-вызов), без Docker-зависимых тестов на сеть.

### D. Ручной ввод (данные; формы — задача 47)

1. В ту же миграцию 020: таблица `issuer_ratings` (`inn VARCHAR(12)`, `agency VARCHAR(32)`,
   `rating VARCHAR(16)`, `rating_date DATE`, `note VARCHAR(512) NULL`,
   `UNIQUE KEY uq_issuer_ratings (inn, agency)`).
2. Эндпоинты CRUD `/api/issuers/{inn}/ratings` и ручной ввод строк отчётности
   `POST /api/issuers/{inn}/financials` (source='manual'; валидация 422 по гайду).
3. Компонент скоркарты `manual_rating` (0–10, слой A, max A растёт до 50 при наличии): шкала
   AAA/AA → 10, A → 8, BBB → 6, BB → 4, B → 2, ниже/отозван → 0 + флаг `JunkRating`
   (не критический). Рейтинг старше 18 месяцев → Missing («протух»).

## Критерии приёмки

- [ ] У каждой бумаги банка/портфеля — score, coverage, компоненты с подписями и источниками;
      «нет данных» — явный Missing, не ноль молча.
- [ ] «Дешёвая» здоровая бумага больше не Red; value-вердикт отдаётся отдельным полем.
- [ ] ОФЗ — Green; эмитент с HASDEFAULT — Red; банк без отчётности — Yellow с Coverage < 0.6
      (интеграционные тесты).
- [ ] Ручной рейтинг и ручная отчётность попадают в скоркарту приоритетно (тест).
- [ ] Миграция 020 + `MigrationIdempotencyTests`; `scripts/pre-push-check.sh --all` — зелёный.
