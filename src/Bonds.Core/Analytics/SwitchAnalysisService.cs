namespace Bonds.Core.Analytics;

/// <summary>
/// Анализ замены (plan/06 B4, spec §3/§9) — сравнение «удерживать позицию A vs продать A и
/// переложиться в позицию B», ТОЛЬКО между текущими позициями портфеля (не скринер по всей
/// вселенной бумаг — явно вне MVP, spec §3 «Вне скоупа»). Учитывает комиссию ОБЕИХ сделок
/// (продажа A + покупка B). Налог при продаже — вне MVP (spec §3: «плоский НДФЛ только на
/// купоны», полное налоговое моделирование — точка расширения, см. <c>ITaxModel</c> в Часть C).
/// Чистый сервис, без I/O.
/// </summary>
public static class SwitchAnalysisService
{
    public const string Disclaimer =
        "Анализ замены сравнивает только текущие позиции портфеля (не скринер по всей вселенной бумаг). " +
        "Налог при продаже (НДФЛ на разницу цены покупки/продажи, сальдирование убытков) не учтён — " +
        "это плоская оценка, не финансовая рекомендация. Перед сделкой проверьте фактические условия у брокера.";

    /// <summary>
    /// Сравнивает удержание позиции <paramref name="holdPosition"/> с переходом в
    /// <paramref name="targetPosition"/> на горизонте <paramref name="horizonYears"/> лет.
    /// Комиссия каждой сделки (<paramref name="sellCommissionRate"/>/<paramref name="buyCommissionRate"/>)
    /// — ставка (доля от суммы сделки), передаётся вызывающим слоем (настройки тарифа брокера),
    /// а не выводится из исторического журнала операций: единой "ставки комиссии инструмента"
    /// в модели нет (<see cref="Bonds.Core.Models.Operation"/> несёт только фактические суммы
    /// прошлых Fee-операций, не тариф на будущую сделку) — ТРЕБУЕТ СОГЛАСОВАНИЯ С ВЛАДЕЛЬЦЕМ,
    /// см. финальный отчёт этапа: на MVP ставка передаётся параметром с разумным дефолтом 0.3%
    /// (типичная брокерская комиссия), который вызывающий слой может переопределить из настроек.
    /// </summary>
    public static SwitchAnalysisResult Compare(
        SwitchCandidate holdPosition,
        SwitchCandidate targetPosition,
        decimal horizonYears,
        decimal sellCommissionRate = DefaultCommissionRate,
        decimal buyCommissionRate = DefaultCommissionRate)
    {
        if (horizonYears <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(horizonYears), "Horizon must be positive.");
        }

        // Стоимость перехода: продать holdPosition (комиссия от суммы продажи) + купить
        // targetPosition на сумму, оставшуюся после продажи (комиссия от суммы покупки) —
        // это весь капитал, который физически "проходит" через обе сделки.
        var sellCommissionRub = holdPosition.MarketValueRub * sellCommissionRate;
        var netProceedsAfterSale = holdPosition.MarketValueRub - sellCommissionRub;
        var buyCommissionRub = netProceedsAfterSale * buyCommissionRate;
        var totalSwitchCostRub = sellCommissionRub + buyCommissionRub;

        // Выгода/проигрыш от самой замены считается на ОДНОЙ и той же базе (MarketValueRub
        // holdPosition) для обеих доходностей — иначе комиссии оказались бы учтены дважды:
        // один раз явно в totalSwitchCostRub, второй раз скрыто через уменьшение базы для
        // switchYieldGainRub (баг, исправленный при ревью этого сервиса). Комиссии вычитаются
        // из чистого выигрыша один раз, отдельным членом ниже.
        var yieldSpread = (targetPosition.EffectiveYield ?? 0m) - (holdPosition.EffectiveYield ?? 0m);
        var spreadGainRub = holdPosition.MarketValueRub * yieldSpread * horizonYears;

        var netBenefitRub = spreadGainRub - totalSwitchCostRub;

        // Сколько лет нужно держать targetPosition, чтобы выгода от разницы доходностей окупила
        // комиссии перехода (простая линейная оценка — без сложного начисления, т.к. горизонт
        // сравнения и так оценочный; достаточно для ответа "когда окупится переход").
        decimal? breakEvenYears = null;
        if (yieldSpread > 0m && holdPosition.MarketValueRub > 0m)
        {
            breakEvenYears = totalSwitchCostRub / (holdPosition.MarketValueRub * yieldSpread);
        }

        var hasUsableYields = holdPosition.EffectiveYield.HasValue && targetPosition.EffectiveYield.HasValue;

        return new SwitchAnalysisResult
        {
            HoldPositionId = holdPosition.PositionId,
            TargetPositionId = targetPosition.PositionId,
            HorizonYears = horizonYears,
            SellCommissionRub = sellCommissionRub,
            BuyCommissionRub = buyCommissionRub,
            TotalSwitchCostRub = totalSwitchCostRub,
            NetBenefitRub = netBenefitRub,
            IsSwitchFavorable = hasUsableYields && netBenefitRub > 0m,
            BreakEvenYears = breakEvenYears,
            YieldDataIncomplete = !hasUsableYields,
            Disclaimer = Disclaimer,
        };
    }

    /// <summary>Дефолтная ставка комиссии за сделку (0.3% — типичный тариф брокера на MVP; настраивается вызывающим слоем).</summary>
    public const decimal DefaultCommissionRate = 0.003m;
}

/// <summary>Один из кандидатов сравнения «удерживать vs переложиться» — только текущая позиция портфеля (spec §3/§9).</summary>
public sealed record SwitchCandidate
{
    public required ulong PositionId { get; init; }
    public required decimal MarketValueRub { get; init; }

    /// <summary>YTM либо CurrentYield (для флоатера) — см. <see cref="PositionComparisonService"/>. Null — доходность не определена.</summary>
    public decimal? EffectiveYield { get; init; }
}

/// <summary>Итог анализа замены: оценка выгоды/проигрыша перехода с обязательным дисклеймером (spec §3/§9).</summary>
public sealed record SwitchAnalysisResult
{
    public required ulong HoldPositionId { get; init; }
    public required ulong TargetPositionId { get; init; }
    public required decimal HorizonYears { get; init; }

    public required decimal SellCommissionRub { get; init; }
    public required decimal BuyCommissionRub { get; init; }
    public required decimal TotalSwitchCostRub { get; init; }

    /// <summary>Чистая выгода (+) или проигрыш (-) перехода на горизонте, в рублях, после вычета комиссий обеих сделок.</summary>
    public required decimal NetBenefitRub { get; init; }
    public required bool IsSwitchFavorable { get; init; }

    /// <summary>Через сколько лет переход окупает свои комиссии за счёт разницы доходностей (null — не окупается/спред неположителен).</summary>
    public decimal? BreakEvenYears { get; init; }

    /// <summary>true — у одной из позиций нет применимой доходности (флоатер без CurrentYield/несошедшийся YTM); результат недостоверен.</summary>
    public required bool YieldDataIncomplete { get; init; }
    public required string Disclaimer { get; init; }
}
