using Bonds.Core.Analytics;

namespace Bonds.Core.Interfaces;

/// <summary>
/// Резолвер эффективной ставки комиссии брокера для счёта (plan/22 часть C). Контракт этого
/// интерфейса потребляется задачами 23/25 — менять сигнатуру без согласования нельзя.
/// <para>
/// Приоритет источника ставки (первый применимый побеждает):
/// 1. <see cref="CommissionRateSource.UserOverride"/> — ручной override в настройках пользователя
///    (<c>UserSettings.CommissionRateOverride</c>, plan/22 часть D) — пользователь явно указал
///    свою реальную ставку, доверяем ей безусловно.
/// 2. <see cref="CommissionRateSource.EstimatedFromTrades"/> — автоматическая оценка из журнала
///    операций счёта (<see cref="CommissionRateEstimator"/>, plan/22 часть A) — используется, если
///    override не задан, но журнал позволяет оценить ставку (см. doc-comment estimator'а про
///    условия null).
/// 3. <see cref="CommissionRateSource.Default"/> — захардкоженный дефолт
///    (<c>SwitchAnalysisService.DefaultCommissionRate</c>, 0.3%) — если ни override, ни оценка
///    недоступны (например, счёт только что синхронизирован, сделок в журнале ещё нет).
/// </para>
/// Явные ставки, переданные вызывающим слоем напрямую в запросе (например,
/// <c>PostReplacement.SellCommissionRate/BuyCommissionRate</c>), по-прежнему побеждают резолвер —
/// он вызывается только когда явная ставка не передана (см. AnalyticsEndpoints/PositionsEndpoints).
/// </summary>
public interface ICommissionRateProvider
{
    Task<ResolvedCommissionRate> GetAsync(ulong accountId, CancellationToken ct = default);
}

/// <summary>Эффективная ставка комиссии для счёта — значение + откуда оно взято + сырая оценка (если была).</summary>
public sealed record ResolvedCommissionRate(decimal Rate, CommissionRateSource Source, CommissionEstimate? Estimate);

/// <summary>Источник эффективной ставки комиссии — см. приоритет в doc-comment <see cref="ICommissionRateProvider"/>.</summary>
public enum CommissionRateSource
{
    UserOverride,
    EstimatedFromTrades,
    Default,
}
