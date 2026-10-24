using Bonds.Core.Policy;

namespace Bonds.Core.Interfaces.Repositories;

/// <summary>
/// Планы сделок (<c>trade_plans</c>/<c>trade_plan_lines</c>, задача 63). ТОЛЬКО ДАННЫЕ: репозиторий не знает о брокерах и не отправляет заявок.
/// Все методы привязаны к <c>userId</c> — чужие планы недоступны. Даты — UTC.
/// </summary>
public interface ITradePlanRepository
{
    /// <summary>Сохраняет план со строками одной транзакцией; возвращает id.</summary>
    Task<ulong> InsertAsync(TradePlan plan);

    Task<TradePlan?> GetAsync(ulong userId, ulong id);

    /// <summary>Планы пользователя (новые сверху) со строками; <paramref name="status"/> = null — любые.</summary>
    Task<IReadOnlyList<TradePlan>> ListAsync(ulong userId, string? status, int limit);

    /// <summary>
    /// Есть ли план того же правила и причины. <paramref name="openOnly"/> = true — только «открытые» (<see cref="PlanStatuses.Open"/>) и не просроченные
    /// (<c>expires_at</c> позже <paramref name="nowUtc"/>); false — в любом статусе (пополнение планируется один раз).
    /// <paramref name="createdSince"/> — если задан, учитываются только планы, созданные не раньше этого момента (пауза повтора).
    /// </summary>
    Task<bool> ExistsAsync(ulong userId, string rule, string trigger, bool openOnly, DateTime nowUtc, DateTime? createdSince = null);

    /// <summary>Есть ли у пользователя открытый (и не просроченный) план правила — для согласования P3b/P4.</summary>
    Task<bool> HasOpenRuleAsync(ulong userId, string rule, DateTime nowUtc);

    /// <summary>
    /// Σ <c>est_amount</c> покупок планов, ИСПОЛНЯЕМЫХ с <paramref name="sinceUtc"/> (по <c>decided_at</c>), в рублях — для суточного лимита. Считаются только
    /// <c>Submitting</c>, <c>PartiallyDone</c>, <c>Done</c> и (задача 65) <c>AutoApproved</c> с <c>dry_run = 0</c> — ждущий исполнителя, со строками в RUB. AutoApproved
    /// dry-run НЕ считается: он никогда не исполняется (деньги не потрачены); счётчик внутри одного цикла наращивает <c>PolicyRunner</c>. Задача 67 (#10): только
    /// <c>decided_via = auto</c> — ручные покупки авто-лимит не расходуют.
    /// <paramref name="excludePlanId"/> — исключить план (исполнитель проверяет лимит для самого плана).
    /// </summary>
    Task<decimal> SumAutoApprovedRubSinceAsync(ulong userId, DateTime sinceUtc, ulong? excludePlanId = null);

    /// <summary>
    /// Планы (со строками), входящие в суточный лимит авто-исполнения с <paramref name="sinceUtc"/> (по <c>decided_at</c>): <c>Submitting</c>, <c>PartiallyDone</c>,
    /// <c>Done</c>, <c>Failed</c> и <c>AutoApproved</c> с <c>dry_run = 0</c>; только <c>decided_via = auto</c> (задача 67, #10). Суммы считает исполнитель по ФАКТУ заявок (<c>AutoDayUsage</c>), а не по оценке.
    /// </summary>
    Task<IReadOnlyList<TradePlan>> ListAutoDayPlansAsync(ulong userId, DateTime sinceUtc);

    /// <summary>Продлевает <c>expires_at</c> подтверждённого плана (только вперёд: <c>GREATEST</c>). false — план не в <c>Approved</c>.</summary>
    Task<bool> ExtendExpiryAsync(ulong userId, ulong id, DateTime expiresAtUtc);

    /// <summary>Пользователи, у которых есть что исполнять: план в <c>Submitting</c> или <c>Approved</c>/<c>AutoApproved</c> с <c>dry_run = 0</c> (задача 65).</summary>
    Task<IReadOnlyList<ulong>> ListUserIdsWithExecutablePlansAsync();

    /// <summary>Планы пользователя для исполнителя (со строками): <c>Submitting</c> + <c>Approved</c>/<c>AutoApproved</c> с <c>dry_run = 0</c>, старые первыми.</summary>
    Task<IReadOnlyList<TradePlan>> ListExecutableAsync(ulong userId);

    /// <summary>
    /// Атомарный переход статуса исполнения (<c>decided_at</c>/<c>decided_via</c> НЕ трогаются — кто решил, остаётся видно); <paramref name="summaryJson"/> — если задан.
    /// false — план в другом статусе.
    /// </summary>
    Task<bool> SetExecutionStatusAsync(ulong userId, ulong id, IReadOnlyCollection<string> fromStatuses, string toStatus, string? summaryJson);

    /// <summary>Просьба снять висящие заявки плана (<c>cancel_requested = 1</c>) — только для плана в исполнении/ожидании исполнения. false — нечего отменять.</summary>
    Task<bool> RequestCancelAsync(ulong userId, ulong id);

    /// <summary>
    /// Σ <c>TradePlanSummary.ReservedBuy</c> (покупки + комиссия в валюте ПОРТФЕЛЯ) открытых, не просроченных планов, РЕЗЕРВИРУЮЩИХ деньги
    /// (<see cref="PlanStatuses.ReservingCash"/>), с покупками у брокера <paramref name="broker"/> и валютой портфеля <paramref name="currency"/>.
    /// плюс живые <c>Approved</c> (<c>dry_run = 0</c>, задача 67). Деньги Approved dry-run не резервируются.
    /// Задача 67 (#3): плюс действующие удержания под вывод (<see cref="GetCashHoldAsync"/>, <paramref name="withdrawHoldTtl"/> — рабочие дни
    /// <see cref="PolicyOptions.WithdrawHoldTtl"/>).
    /// </summary>
    Task<decimal> SumReservedBuysAsync(ulong userId, string broker, string currency, DateTime nowUtc, int withdrawHoldTtl = 3);

    /// <summary>
    /// Действующие удержания денег под вывод (задача 67, #3): Σ <see cref="TradePlanSummary.CashHold"/> планов P5/выхода из корзины брокера <paramref name="broker"/>
    /// с валютой портфеля <paramref name="currency"/>, по которым <see cref="WithdrawHold.IsActive"/>.
    /// </summary>
    Task<CashHoldState> GetCashHoldAsync(ulong userId, string broker, string currency, DateTime nowUtc, int withdrawHoldTtl);

    /// <summary>
    /// Задача 67 (#4): атомарная смена <c>bot_nonce</c> ждущего решения плана (<c>PendingApproval</c>) с <paramref name="expectedNonce"/> на <paramref name="newNonce"/> —
    /// кнопки Telegram одноразовые. false — nonce уже другой (кнопку нажали раньше/параллельно) или план не ждёт решения.
    /// </summary>
    Task<bool> RotateNonceAsync(ulong userId, ulong id, string expectedNonce, string newNonce);

    /// <summary>
    /// «Деньги выведены» (<c>/withdrawn</c>, кнопка в вебе): снимает удержание у ИСПОЛНЕННЫХ планов вывода/выхода пользователя — <c>Done</c>/<c>PartiallyDone</c>
    /// (в т.ч. план «только удержание», который при подтверждении сразу Done). Неисполненные (Pending/Approved/Submitting…) держат деньги дальше: продажи ещё не
    /// прошли, вывести эти деньги пользователь не мог (T-67E, minor 5). Возвращает число планов.
    /// </summary>
    Task<int> ReleaseCashHoldsAsync(ulong userId, DateTime nowUtc);

    /// <summary>
    /// Атомарно переводит план из одного из <paramref name="fromStatuses"/> в <paramref name="toStatus"/> (с <c>decided_at</c>, <c>decided_via</c> и, если задан, новым
    /// <c>summary_json</c>). false — план не найден или находится в другом статусе (гонка двух решений). <paramref name="notExpiredAt"/> (задача 67, NIT) —
    /// переход только у плана с <c>expires_at</c> позже этого момента (проверка в том же UPDATE: срок мог истечь между чтением и решением). <paramref name="dryRun"/>
    /// (T-67E, M3) — если задан, <c>dry_run</c> пишется в том же UPDATE, что и статус (исполнитель не увидит Approved со старым флагом).
    /// </summary>
    Task<bool> TransitionAsync(
        ulong userId, ulong id, IReadOnlyCollection<string> fromStatuses, string toStatus, string? decidedVia, DateTime decidedAtUtc, string? summaryJson,
        DateTime? notExpiredAt = null, bool? dryRun = null);

    /// <summary>
    /// Переводит просроченные <c>PendingApproval</c> и <c>AutoApproved</c> (по <c>expires_at</c>) в <c>Expired</c>; возвращает число планов. У AutoApproved в сводку
    /// дописывается «не исполнен» (в dry-run он не исполнялся).
    /// </summary>
    Task<int> ExpirePendingAsync(DateTime nowUtc);
}
