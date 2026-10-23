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
    /// <c>Submitting</c> и <c>Done</c> со строками в RUB. <c>AutoApproved</c> НЕ считается: в dry-run он никогда не исполняется (деньги не потрачены); счётчик внутри
    /// одного цикла наращивает <c>PolicyRunner</c>. TODO фаза 3: когда появится исполнитель, учитывать AutoApproved, ожидающие исполнения.
    /// </summary>
    Task<decimal> SumAutoApprovedRubSinceAsync(ulong userId, DateTime sinceUtc);

    /// <summary>
    /// Σ <c>TradePlanSummary.ReservedBuy</c> (покупки + комиссия в валюте ПОРТФЕЛЯ) открытых, не просроченных планов, РЕЗЕРВИРУЮЩИХ деньги
    /// (<see cref="PlanStatuses.ReservingCash"/>), с покупками у брокера <paramref name="broker"/> и валютой портфеля <paramref name="currency"/>.
    /// Деньги Approved dry-run не резервируются.
    /// </summary>
    Task<decimal> SumReservedBuysAsync(ulong userId, string broker, string currency, DateTime nowUtc);

    /// <summary>
    /// Атомарно переводит план из одного из <paramref name="fromStatuses"/> в <paramref name="toStatus"/> (с <c>decided_at</c>, <c>decided_via</c> и, если задан, новым
    /// <c>summary_json</c>). false — план не найден или находится в другом статусе (гонка двух решений).
    /// </summary>
    Task<bool> TransitionAsync(
        ulong userId, ulong id, IReadOnlyCollection<string> fromStatuses, string toStatus, string? decidedVia, DateTime decidedAtUtc, string? summaryJson);

    /// <summary>
    /// Переводит просроченные <c>PendingApproval</c> и <c>AutoApproved</c> (по <c>expires_at</c>) в <c>Expired</c>; возвращает число планов. У AutoApproved в сводку
    /// дописывается «не исполнен» (в dry-run он не исполнялся).
    /// </summary>
    Task<int> ExpirePendingAsync(DateTime nowUtc);
}
