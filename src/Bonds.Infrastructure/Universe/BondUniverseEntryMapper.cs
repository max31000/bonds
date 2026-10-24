using Bonds.Core.Calculation;
using Bonds.Core.Models;
using Bonds.Infrastructure.Connectors.Moex;

namespace Bonds.Infrastructure.Universe;

/// <summary>
/// Конвертирует сырую строку MOEX ISS (<see cref="MoexBondMarketRow"/>,
/// единицы "как есть от биржи") в <see cref="BondUniverseEntry"/> (единицы этого сервиса).
/// <para>
/// <b>ЕДИНИЦЫ (plan/26 "ЕДИНИЦЫ" — главный класс багов в этом репо, дважды ловили):</b>
/// MOEX YIELD приходит в ПРОЦЕНТАХ (например 12.5 = 12.5%) — здесь делится на 100 → ДОЛЯ (0.125).
/// MOEX DURATION приходит в ДНЯХ — здесь делится на 365 → ГОДЫ. Цены/bid/offer остаются в %
/// от номинала как есть (суффикс _percent), НЕ переводятся в доли/рубли.
/// </para>
/// <para>
/// Задача 42 (блок <c>marketdata_yields</c>): EFFECTIVEYIELD/YIELDTOOFFER приходят в ПРОЦЕНТАХ → ÷100;
/// GSPREADBP/ZSPREADBP приходят в БАЗИСНЫХ ПУНКТАХ → ÷10000 (35 б.п. → 0.0035); ACCRUEDINT — рубли
/// на одну бумагу, без конверсии. Отсутствие строки marketdata_yields даёт null во всех этих полях.
/// </para>
/// </summary>
public static class BondUniverseEntryMapper
{
    private const decimal DaysPerYear = 365m;
    private const decimal PercentToFraction = 100m;
    private const decimal BasisPointsToFraction = 10_000m;

    /// <summary>
    /// Предел правдоподобной доли (1000 = 100 000% годовых). Колонки банка — DECIMAL(12,8), максимум 9999.99999999;
    /// MOEX для дефолтных/неликвидных выпусков отдаёт абсурдные доходности, и одна такая строка роняла весь
    /// почасовой upsert снимка (прод 2026-10-24: «Out of range value for column effective_yield_fraction»).
    /// За пределом значение бессмысленно для аналитики → null («нет данных»), а не исключение.
    /// </summary>
    internal const decimal MaxPlausibleFraction = 1000m;

    private static decimal? Plausible(decimal? fraction) =>
        fraction is { } f && Math.Abs(f) <= MaxPlausibleFraction ? f : null;

    /// <summary>MOEX проценты → доля (÷100). Null или неправдоподобное значение → null.</summary>
    private static decimal? PercentToFractionOrNull(decimal? percent) =>
        percent.HasValue ? Plausible(percent.Value / PercentToFraction) : null;

    /// <summary>MOEX базисные пункты → доля (÷10000: 35 б.п. = 0.0035). Null или неправдоподобное значение → null.</summary>
    private static decimal? BasisPointsToFractionOrNull(decimal? bp) =>
        bp.HasValue ? Plausible(bp.Value / BasisPointsToFraction) : null;

    /// <summary>
    /// Маппит одну строку + опциональную кривую (для приближённого G-спреда, часть C.3). Только
    /// рублёвые (FACEUNIT SUR/RUB) бумаги должны попадать сюда — фильтрация по валюте и STATUS
    /// выполняется ДО вызова этого метода (см. <see cref="BondUniverseRefreshService"/>), не здесь —
    /// маппер сам по себе не решает, включать ли бумагу, только переводит единицы одной строки.
    /// </summary>
    public static BondUniverseEntry Map(MoexBondMarketRow row, YieldCurveSnapshot? curve)
    {
        var yieldFraction = PercentToFractionOrNull(row.YieldPercent);
        var durationYears = row.DurationDays.HasValue ? row.DurationDays.Value / DaysPerYear : (decimal?)null;

        decimal? gspreadApprox = null;
        if (yieldFraction is { } y && durationYears is { } d && d > 0m && curve is not null)
        {
            // Приближение по данным MOEX (YIELD/DURATION биржи), НЕ точный движок GSpreadCalculator
            // считает G-спред для холдингов портфеля из BondMetricsCalculator — здесь переиспользуем
            // только интерполяцию кривой (CurveValue), входная доходность/срок — биржевые, не наши.
            gspreadApprox = GSpreadCalculator.GSpread(y, d, curve);
        }

        return new BondUniverseEntry
        {
            Secid = row.Secid,
            Isin = row.Isin,
            ShortName = row.ShortName,
            SecName = row.SecName,
            FaceValue = row.FaceValue,
            LotValue = row.LotValue,
            CouponPercent = row.CouponPercent,
            MaturityDate = row.MatDate,
            OfferDate = row.OfferDate,
            ListLevel = row.ListLevel,
            Sector = BondUniverseSectorMapper.MapSecTypeToSector(row.SecType),
            YieldFraction = yieldFraction,
            DurationYears = durationYears,
            PricePercent = row.PricePercent,
            TurnoverRub = row.TurnoverRub,
            BidPercent = row.BidPercent,
            OfferPercent = row.OfferPricePercent,
            NumTrades = row.NumTrades,
            GspreadApproxFraction = gspreadApprox,
            AccruedRub = row.AccruedRub,
            IssueSize = row.IssueSize,
            EffectiveYieldFraction = PercentToFractionOrNull(row.EffectiveYieldPercent),
            YieldToOfferFraction = PercentToFractionOrNull(row.YieldToOfferPercent),
            YieldDateType = row.YieldDateType,
            GspreadOfficialFraction = BasisPointsToFractionOrNull(row.GspreadBp),
            ZspreadOfficialFraction = BasisPointsToFractionOrNull(row.ZspreadBp),
            IsFloater = row.BondType is null ? null : row.BondType.Contains("Флоатер", StringComparison.OrdinalIgnoreCase),
            UpdatedAt = DateTime.UtcNow,
        };
    }
}
