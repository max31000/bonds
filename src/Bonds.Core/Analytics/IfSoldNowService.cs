namespace Bonds.Core.Analytics;

/// <summary>
/// «Если продать сейчас» (plan/19 §A.4) — чистая прикидка выручки от продажи всего текущего
/// остатка позиции ПРЯМО СЕЙЧАС по последней известной котировке: рыночная стоимость (уже включает
/// НКД — см. doc-comment <see cref="PortfolioHoldingsBuilder"/> про DirtyPrice × Quantity) минус
/// комиссия брокера (<see cref="Bonds.Core.Analytics.SwitchAnalysisService.DefaultCommissionRate"/>
/// по умолчанию, ставка передаётся вызывающим слоем). Если для позиции известна цена входа
/// (<see cref="Bonds.Core.Analytics.PositionCostBasis"/>, plan/14) — дополнительно считает итоговый
/// P&amp;L при продаже: (чистая выручка − вложено) + уже полученные купоны.
/// <para>
/// Это НЕ анализ замены (<see cref="SwitchAnalysisService"/> — сравнение с альтернативной бумагой)
/// и не налоговый расчёт (НДФЛ на разницу цены покупки/продажи вне MVP, см. spec §3) — просто
/// снимок "что я получу на руки, если закрою позицию сегодня".
/// </para>
/// </summary>
public static class IfSoldNowService
{
    public const string Disclaimer =
        "Оценочный расчёт: рыночная стоимость по последней известной котировке минус комиссия " +
        "брокера. Налог при продаже (НДФЛ на разницу цены покупки/продажи) не учтён. Фактическая " +
        "цена исполнения на бирже может отличаться от последней котировки.";

    /// <summary>
    /// Считает оценку продажи текущего остатка. <paramref name="marketValueRub"/> — грязная
    /// рыночная стоимость всей позиции (Quantity × DirtyPrice, как в <c>PortfolioHolding</c>).
    /// <paramref name="costBasis"/> — null, если по журналу операций цену входа не восстановить
    /// (см. doc-comment <see cref="PositionCostBasis"/>) — тогда P&amp;L-поля результата тоже null.
    /// <paramref name="accruedTotalRub"/> — задача 24: НКД на всю позицию (уже включён в
    /// <paramref name="marketValueRub"/>, см. doc-comment <see cref="PortfolioHoldingsBuilder"/>) —
    /// передаётся только для разложения в UI («выручка = чистая стоимость + НКД − комиссия»), НЕ
    /// пересчитывает саму выручку. Дефолт 0m — вызывающий код без данных об НКД не ломается,
    /// просто CleanValueRub совпадёт с MarketValueRub.
    /// </summary>
    public static IfSoldNowResult Calculate(
        decimal marketValueRub,
        PositionCostBasis? costBasis,
        decimal commissionRate = SwitchAnalysisService.DefaultCommissionRate,
        decimal accruedTotalRub = 0m)
    {
        var commissionRub = marketValueRub * commissionRate;
        var netProceedsRub = marketValueRub - commissionRub;

        decimal? realizedPnlRub = null;
        decimal? realizedPnlPercent = null;
        decimal? totalReturnWithCouponsRub = null;

        if (costBasis?.InvestedRub is decimal investedRub && investedRub != 0m)
        {
            realizedPnlRub = netProceedsRub - investedRub;
            realizedPnlPercent = realizedPnlRub / investedRub;
            totalReturnWithCouponsRub = realizedPnlRub + costBasis.CouponsReceivedRub;
        }

        return new IfSoldNowResult
        {
            MarketValueRub = marketValueRub,
            CleanValueRub = marketValueRub - accruedTotalRub,
            AccruedTotalRub = accruedTotalRub,
            CommissionRub = commissionRub,
            NetProceedsRub = netProceedsRub,
            CommissionRate = commissionRate,
            RealizedPnlRub = realizedPnlRub,
            RealizedPnlPercent = realizedPnlPercent,
            CouponsReceivedRub = costBasis?.CouponsReceivedRub,
            TotalReturnWithCouponsRub = totalReturnWithCouponsRub,
            PnlAvailable = costBasis?.InvestedRub is not null,
            Disclaimer = Disclaimer,
        };
    }
}

/// <summary>Результат «если продать сейчас» — см. doc-comment <see cref="IfSoldNowService"/>.</summary>
public sealed record IfSoldNowResult
{
    /// <summary>Рыночная стоимость всего остатка (грязная цена × количество) на момент расчёта.</summary>
    public required decimal MarketValueRub { get; init; }

    /// <summary>Задача 24: MarketValueRub − AccruedTotalRub — «чистая» (без НКД) стоимость остатка.</summary>
    public decimal CleanValueRub { get; init; }

    /// <summary>Задача 24: НКД на всю позицию, уже включённый в MarketValueRub (разложение, не добавка).</summary>
    public decimal AccruedTotalRub { get; init; }

    public required decimal CommissionRub { get; init; }
    public required decimal CommissionRate { get; init; }

    /// <summary>Выручка на руки = MarketValueRub − CommissionRub.</summary>
    public required decimal NetProceedsRub { get; init; }

    /// <summary>NetProceedsRub − вложено (по средней цене входа). Null — cost basis недоступен (plan/14).</summary>
    public decimal? RealizedPnlRub { get; init; }

    /// <summary>RealizedPnlRub / InvestedRub — доля.</summary>
    public decimal? RealizedPnlPercent { get; init; }

    /// <summary>Сумма купонов, полученных по бумаге за всё время (см. PositionCostBasis).</summary>
    public decimal? CouponsReceivedRub { get; init; }

    /// <summary>RealizedPnlRub + CouponsReceivedRub — итоговый результат владения бумагой при продаже сейчас.</summary>
    public decimal? TotalReturnWithCouponsRub { get; init; }

    /// <summary>True — P&amp;L-поля посчитаны (cost basis известен, plan/14); false — доступна только выручка/комиссия.</summary>
    public required bool PnlAvailable { get; init; }

    public required string Disclaimer { get; init; }
}
