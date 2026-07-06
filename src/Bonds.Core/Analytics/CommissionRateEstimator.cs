using Bonds.Core.Models;

namespace Bonds.Core.Analytics;

/// <summary>
/// Оценка фактической ставки комиссии брокера из журнала операций счёта (plan/22 часть A).
/// Числовой ставки в T-Invest API нет — но журнал уже несёт фактические <see cref="OperationType.Fee"/>
/// операции (комиссии сделок) и объём торгов (<see cref="OperationType.Buy"/>/<see cref="OperationType.Sell"/>),
/// поэтому ставку можно восстановить как отношение суммы комиссий к обороту сделок за окно.
/// <para>
/// <b>Погрешность (ВАЖНО, см. doc-comment <see cref="Bonds.Infrastructure.Connectors.TInvest.TInvestOperationMapper"/>):</b>
/// <see cref="OperationType.Fee"/> в этой модели агрегирует ВСЕ типы комиссий T-Invest, попадающие
/// под fallback-маппинг (<c>BrokerFee</c>, <c>ServiceFee</c>, <c>MarginFee</c>, <c>CashFee</c>,
/// <c>OutFee</c>, <c>OtherFee</c>, <c>AdviceFee</c>, <c>SuccessFee</c>, <c>TrackMfee</c>, <c>TrackPfee</c>,
/// <c>OutStampDuty</c>) — НЕ только брокерскую комиссию за сделки. <see cref="Operation"/> не хранит
/// сырой тип T-Invest, поэтому здесь нет возможности отфильтровать по нему без изменения схемы
/// (различение оставлено как TODO — см. финальный отчёт задачи 22 и doc-comment
/// <see cref="Bonds.Core.Interfaces.ICommissionRateProvider"/>). Если у счёта есть сервисные/депозитарные
/// сборы, оценка будет слегка завышена относительно чистой комиссии за сделку — это единственная
/// доступная по факту оценка «по всем комиссиям журнала», доносится до пользователя текстом в UI
/// (см. Settings.tsx).
/// </para>
/// Чистый статический сервис, без I/O — тестируется без БД/gRPC.
/// </summary>
public static class CommissionRateEstimator
{
    /// <summary>Ширина окна оценки по умолчанию — последние 6 месяцев от <c>asOf</c>.</summary>
    public const int DefaultWindowMonths = 6;

    /// <summary>Минимум сделок в окне, ниже которого окно расширяется на всю историю журнала.</summary>
    public const int MinTradesInWindow = 5;

    /// <summary>
    /// Оценивает ставку комиссии по журналу <paramref name="operations"/> на момент <paramref name="asOf"/>.
    /// Алгоритм (plan/22 §A): берём окно [asOf − 6 мес, asOf]; если сделок (Buy+Sell) в окне меньше
    /// <see cref="MinTradesInWindow"/> — расширяем окно на всю историю журнала (WindowMonths отражает
    /// фактическую ширину использованного окна). Ставка = Σ|AmountRub| операций Fee / Σ|AmountRub|
    /// операций Buy+Sell за окно. Нет сделок в окне вообще, либо оборот равен нулю (например, только
    /// Fee без сделок) — возвращает null: делить не на что, оценка недостоверна.
    /// </summary>
    public static CommissionEstimate? Estimate(IReadOnlyCollection<Operation> operations, DateTime asOf)
    {
        if (operations.Count == 0) return null;

        var defaultWindowStart = asOf.AddMonths(-DefaultWindowMonths);
        var tradesInDefaultWindow = operations.Count(op => IsTrade(op.Type) && op.Date >= defaultWindowStart && op.Date <= asOf);

        DateTime windowStart;
        int windowMonths;
        if (tradesInDefaultWindow >= MinTradesInWindow)
        {
            windowStart = defaultWindowStart;
            windowMonths = DefaultWindowMonths;
        }
        else
        {
            // Расширяем на всю историю журнала — от самой ранней операции.
            windowStart = operations.Min(op => op.Date);
            windowMonths = Math.Max(DefaultWindowMonths, MonthsBetween(windowStart, asOf));
        }

        var windowOps = operations.Where(op => op.Date >= windowStart && op.Date <= asOf).ToList();

        var turnoverRub = windowOps.Where(op => IsTrade(op.Type)).Sum(op => Math.Abs(op.AmountRub));
        if (turnoverRub <= 0m) return null;

        var feeTotalRub = windowOps.Where(op => op.Type == OperationType.Fee).Sum(op => Math.Abs(op.AmountRub));
        var tradeCount = windowOps.Count(op => IsTrade(op.Type));

        return new CommissionEstimate
        {
            Rate = feeTotalRub / turnoverRub,
            FeeTotalRub = feeTotalRub,
            TurnoverRub = turnoverRub,
            TradeCount = tradeCount,
            WindowMonths = windowMonths,
        };
    }

    private static bool IsTrade(OperationType type) => type is OperationType.Buy or OperationType.Sell;

    /// <summary>Приблизительная ширина окна в месяцах (для отображения в UI — не используется в расчёте).</summary>
    private static int MonthsBetween(DateTime from, DateTime to)
    {
        var months = ((to.Year - from.Year) * 12) + to.Month - from.Month;
        return Math.Max(months, 1);
    }
}

/// <summary>
/// Результат оценки ставки комиссии по журналу операций (plan/22 часть A). Null у вызывающего
/// кода — журнал не позволяет оценить ставку (нет сделок/оборот 0), см. doc-comment <see cref="CommissionRateEstimator"/>.
/// </summary>
public sealed record CommissionEstimate
{
    /// <summary>Оценённая ставка комиссии — доля (0.00046 = 0.046%), не процент.</summary>
    public required decimal Rate { get; init; }

    /// <summary>Сумма всех Fee-операций за использованное окно, ₽ (модуль).</summary>
    public required decimal FeeTotalRub { get; init; }

    /// <summary>Сумма оборота Buy+Sell за использованное окно, ₽ (модуль).</summary>
    public required decimal TurnoverRub { get; init; }

    /// <summary>Число сделок (Buy+Sell) в использованном окне.</summary>
    public required int TradeCount { get; init; }

    /// <summary>Фактическая ширина использованного окна в месяцах (6 — стандартное, больше — если было расширено на всю историю).</summary>
    public required int WindowMonths { get; init; }
}
