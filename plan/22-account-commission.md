# Задача 22 — Комиссия аккаунта: авто-определение из журнала + тариф из API + override

> **Кому адресовано.** Агент-исполнитель. Задача самодостаточна. Конвенции — [CLAUDE.md](../CLAUDE.md).
> Доходности/ставки на бэкенде — ДОЛИ (0.003 = 0.3%). Рубли — decimal. Перед пушем — `pre-push-check`
> (пуш делает оркестратор, не ты).
>
> **Зависимости:** нет. Задачи 23 и 25 будут потреблять твой `ICommissionRateProvider` — контракт
> ниже обязателен.

---

## Проблема

Ставка комиссии захардкожена: `SwitchAnalysisService.DefaultCommissionRate = 0.003` (0.3%,
тариф «Инвестор») и используется в замене, аллокации и «если продать сейчас»
([SwitchAnalysisService.cs](../src/Bonds.Core/Analytics/SwitchAnalysisService.cs),
[CashAllocationService.cs](../src/Bonds.Core/Analytics/CashAllocationService.cs),
[IfSoldNowService.cs](../src/Bonds.Core/Analytics/IfSoldNowService.cs)). У владельца тариф со
сниженной ставкой (~0.04–0.05%) — мы завышаем издержки перекладки в ~7–12 раз, из-за чего
выгодные замены отфильтровываются как убыточные. Числовой ставки в T-Invest API нет, но есть:
(а) фактические `Fee`-операции в журнале (уже синкаются), (б) имя тарифа в `UsersService.GetInfo`.

## Что сделать

### A. Оценка фактической ставки из журнала (Core, чистый сервис)

`Bonds.Core/Analytics/CommissionRateEstimator.cs` (статический, без I/O):
- Вход: журнал `Operation` счёта + `asOf`. Выход: `CommissionEstimate?` record:
  `Rate` (доля), `FeeTotalRub`, `TurnoverRub`, `TradeCount`, `WindowMonths`.
- Алгоритм: окно — последние **6 месяцев** от `asOf`; ставка = Σ|AmountRub| операций `Fee` /
  Σ|AmountRub| операций `Buy`+`Sell` за окно. Если сделок в окне < 5 — расширить окно на всю
  историю (и отразить это в `WindowMonths`). Если сделок нет вообще или оборот 0 — вернуть `null`.
- **ВАЖНО, проверь маппер**: [TInvestOperationMapper.cs](../src/Bonds.Infrastructure/Connectors/TInvest/TInvestOperationMapper.cs) —
  какие сырые типы T-Invest мапятся в `OperationType.Fee`. Если туда попадают НЕ только
  брокерские комиссии сделок (например, сервисные/депозитарные сборы) — оценка завысится.
  Разберись по факту: если в `Operation` сохраняется сырой тип/название — фильтруй по нему;
  если нет и в Fee попадает лишнее — добавь минимальное поле/различение (миграция) либо
  задокументируй погрешность в doc-comment и в UI-подписи («по всем комиссиям журнала»).
  Не выдумывай — реши по фактическому коду маппера и доступным полям.
- Юнит-тесты: обычный случай (2 сделки + 2 комиссии → точная ставка), мало сделок → окно вся
  история, нет сделок → null, Fee без сделок → null (оборот 0), знак AmountRub не важен (модуль).

### B. Тариф из T-Invest API (Infrastructure)

- `ITInvestPortfolioClient` + [TInvestPortfolioClient.cs](../src/Bonds.Infrastructure/Connectors/TInvest/TInvestPortfolioClient.cs):
  новый метод `GetUserTariffAsync(ct)` → `string?` через `Users.GetInfoAsync` (поле `tariff`;
  если SDK отдаёт и `prem_status` — включи в результат record'ом). Ошибка/нет токена → `null`,
  не исключение наружу.
- Числовой ставки в API НЕТ — тариф только отображается в настройках для контекста, в расчёт
  не входит. Зафиксируй это doc-comment'ом.

### C. Резолвер эффективной ставки (Infrastructure) — контракт для задач 23/25

`Bonds.Infrastructure/Analytics/CommissionRateProvider.cs` + интерфейс
`Bonds.Core/Interfaces/ICommissionRateProvider.cs`:
```csharp
Task<ResolvedCommissionRate> GetAsync(ulong accountId, CancellationToken ct = default);
// record ResolvedCommissionRate(decimal Rate, CommissionRateSource Source, CommissionEstimate? Estimate);
// enum CommissionRateSource { UserOverride, EstimatedFromTrades, Default }
```
Приоритет: override из настроек → оценка из журнала (часть A) → `DefaultCommissionRate`.
Зарегистрировать в DI. Юнит-тесты на все три ветки приоритета.

### D. Настройки: хранение override + отдача контекста

- Миграция (следующий номер, смотри `ls src/Bonds.Infrastructure/Migrations/`; ПОМНИ: сплиттер
  миграций теперь режет `--`-комментарии, но `;` в комментариях всё равно не пиши):
  `user_settings.commission_rate_override DECIMAL(8,6) NULL` (доля).
- `UserSettings` модель + репозиторий + `GET/PUT /api/settings`
  ([SettingsEndpoints.cs](../src/Bonds.Api/Endpoints/SettingsEndpoints.cs)): PUT принимает
  `commissionRateOverride` (валидация: null или 0 < x < 0.05, иначе 422 — ставка больше 5%
  явно опечатка); GET дополнительно возвращает read-only контекст: `commissionAutoEstimate`
  (ставка+обороты+число сделок или null), `tInvestTariff` (строка или null), `commissionEffective`
  (что реально применится и источник).
- Frontend [Settings.tsx](../bonds-web/src/pages/Settings.tsx) + store: блок «Комиссия брокера»:
  «Тариф T-Invest: {tariff}» · «Фактическая по сделкам: ≈0.046% (23 сделки за 6 мес)» ·
  NumberInput «Переопределить, %» (вводится в ПРОЦЕНТАХ, конвертация в долю на границе API —
  задокументируй) · подпись «Применяется: 0.046% (из ваших сделок)».

### E. Прокинуть ставку в потребители + показать в UI

- [AnalyticsEndpoints.cs](../src/Bonds.Api/Endpoints/AnalyticsEndpoints.cs): `PostReplacement` —
  когда `request.SellCommissionRate/BuyCommissionRate` null, брать из `ICommissionRateProvider`
  (не константу); `GetAllocation` — аналогично; [PositionsEndpoints.cs](../src/Bonds.Api/Endpoints/PositionsEndpoints.cs)
  `ifSoldNow` — аналогично. В ответы добавить `commissionRateUsed` (доля) и `commissionRateSource`
  (строка enum) там, где их ещё нет.
- Frontend: на карточках замен, в результатах аллокации и в «если продать сейчас» — подпись
  мелким: «комиссия 0.046% — из ваших сделок» (маппинг source → человеческий текст).
- Интеграционные тесты: замена/аллокация с засеянными Fee-операциями применяет оценённую ставку;
  override в настройках побеждает оценку.

## Критерии приёмки

- [ ] Оценка ставки из журнала с тестами; тариф из GetInfo; резолвер с приоритетом override → оценка → дефолт.
- [ ] Все три потребителя (replacement/allocation/ifSoldNow) используют резолвер; ответы несут ставку+источник.
- [ ] Настройки показывают тариф, авто-оценку и позволяют override (в %, валидация).
- [ ] UI-карточки показывают применённую ставку и источник.
- [ ] `dotnet build/test` (юнит+интеграционные), `yarn typecheck/test:run/build/lint` — зелёные.
