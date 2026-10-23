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
    /// </summary>
    Task<bool> ExistsAsync(ulong userId, string rule, string trigger, bool openOnly, DateTime nowUtc);

    /// <summary>Есть ли у пользователя открытый (и не просроченный) план правила — для согласования P3b/P4.</summary>
    Task<bool> HasOpenRuleAsync(ulong userId, string rule, DateTime nowUtc);

    /// <summary>
    /// Σ <c>est_amount</c> покупок планов, уже авто-подтверждённых с <paramref name="sinceUtc"/> (по <c>decided_at</c>), в рублях — для суточного лимита.
    /// Считаются <c>AutoApproved</c>, <c>Submitting</c>, <c>Done</c> со строками в RUB.
    /// </summary>
    Task<decimal> SumAutoApprovedRubSinceAsync(ulong userId, DateTime sinceUtc);

    /// <summary>
    /// Σ (<c>est_amount + est_fee</c>) покупок открытых, не просроченных планов, РЕЗЕРВИРУЮЩИХ деньги (<see cref="PlanStatuses.ReservingCash"/>), по брокеру
    /// и валюте строк. Деньги Approved dry-run не резервируются.
    /// </summary>
    Task<decimal> SumReservedBuysAsync(ulong userId, string broker, string currency, DateTime nowUtc);

    /// <summary>
    /// Атомарно переводит план из одного из <paramref name="fromStatuses"/> в <paramref name="toStatus"/> (с <c>decided_at</c>, <c>decided_via</c> и, если задан, новым
    /// <c>summary_json</c>). false — план не найден или находится в другом статусе (гонка двух решений).
    /// </summary>
    Task<bool> TransitionAsync(
        ulong userId, ulong id, IReadOnlyCollection<string> fromStatuses, string toStatus, string? decidedVia, DateTime decidedAtUtc, string? summaryJson);

    /// <summary>Переводит просроченные <c>PendingApproval</c> (по <c>expires_at</c>) в <c>Expired</c>; возвращает число планов.</summary>
    Task<int> ExpirePendingAsync(DateTime nowUtc);
}
