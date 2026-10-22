namespace Bonds.Core.Income;

/// <summary>Состояние позиции в журнале доходов.</summary>
public enum PositionIncomeStatus
{
    /// <summary>Бумага/паи на счёте сейчас.</summary>
    Open,

    /// <summary>Продана/выведена целиком.</summary>
    Closed,

    /// <summary>Погашена (в журнале есть <see cref="LedgerEntryKind.Redemption"/>).</summary>
    Redeemed,
}

/// <summary>
/// Доход по одной позиции (облигация или фонд), включая закрытые. Деньги — РУБЛИ, доли — ДОЛИ (0.12 = 12%).
/// Метод — average cost (см. <see cref="PositionIncomeCalculator"/>). Инвариант (закреплён тестами):
/// <c>TotalResultRub = Σ AmountRub(операции актива) − UnknownCostProceedsRub + MarketValueCleanRub + AccruedNowRub − UnknownCostHoldingsRub</c> и
/// <c>TotalResultRub = RealizedPnlRub + (UnrealizedPnlRub ?? 0) + NetCouponIncomeRub + TaxesRub + FeesRub</c>.
/// </summary>
public sealed record PositionIncome
{
    public required AssetKind AssetKind { get; init; }
    public required ulong AssetId { get; init; }
    public required PositionIncomeStatus Status { get; init; }

    /// <summary>Дата первой покупки/ввода (если их нет — первой записи журнала).</summary>
    public required DateOnly FirstDate { get; init; }

    /// <summary>Дата последней записи журнала.</summary>
    public required DateOnly LastDate { get; init; }

    /// <summary>Дней владения: открытая — от <see cref="FirstDate"/> до «сегодня», закрытая — до <see cref="LastDate"/>.</summary>
    public required int HoldingDays { get; init; }

    public required decimal QuantityNow { get; init; }

    /// <summary>Σ |покупок| с НКД, рубли.</summary>
    public required decimal InvestedTotalRub { get; init; }

    /// <summary>Чистая себестоимость остатка (average cost, без НКД), рубли; у закрытой — 0.</summary>
    public required decimal CostRemainingRub { get; init; }

    /// <summary>Рыночная стоимость остатка по ЧИСТОЙ цене (без НКД), рубли; у закрытой — 0.</summary>
    public required decimal MarketValueCleanRub { get; init; }

    /// <summary>НКД остатка сейчас, рубли (0 у фонда и закрытой).</summary>
    public required decimal AccruedNowRub { get; init; }

    /// <summary>MarketValueClean − CostRemaining; null у закрытой/погашенной.</summary>
    public decimal? UnrealizedPnlRub { get; init; }

    public required decimal RealizedPnlRub { get; init; }

    /// <summary>Купоны/выплаты фонда БРУТТО (до налога), рубли.</summary>
    public required decimal IncomeGrossRub { get; init; }

    /// <summary>НКД, уплаченный продавцу при покупках (величина ≥ 0, только известный).</summary>
    public required decimal AccruedPaidRub { get; init; }

    /// <summary>НКД, полученный при продажах (величина ≥ 0, только известный).</summary>
    public required decimal AccruedReceivedRub { get; init; }

    /// <summary>«Честный купонный доход» = IncomeGross − AccruedPaid + AccruedReceived + AccruedNow.</summary>
    public required decimal NetCouponIncomeRub { get; init; }

    /// <summary>Налоги со знаком брокера (удержание −, возврат +).</summary>
    public required decimal TaxesRub { get; init; }

    /// <summary>Комиссии со знаком брокера (обычно −).</summary>
    public required decimal FeesRub { get; init; }

    /// <summary>Возвращённое тело (амортизация + погашение), рубли.</summary>
    public required decimal PrincipalReturnedRub { get; init; }

    public required decimal TotalResultRub { get; init; }

    /// <summary>TotalResult / InvestedTotal — ДОЛЯ; null, если вложенного нет.</summary>
    public decimal? TotalReturnFraction { get; init; }

    /// <summary>XIRR позиции — ДОЛЯ годовых; null при владении &lt; 30 дней или если не сошёлся.</summary>
    public decimal? XirrFraction { get; init; }

    /// <summary>Журнал не покрывает весь остаток (или есть ввод/вывод бумаг без цены, продажа больше известного).</summary>
    public required bool HasUnknownLots { get; init; }

    /// <summary>У облигации хотя бы у одной покупки/продажи НКД неизвестен — цена входа включает НКД.</summary>
    public required bool AccruedUnknown { get; init; }

    /// <summary>
    /// Выручка от продажи единиц без известной себестоимости (куплены до начала истории / пришли не покупкой), РУБЛИ. Результата не
    /// даёт и в итог не входит — считается внесённой «по рынку» в день продажи.
    /// </summary>
    public decimal UnknownCostProceedsRub { get; init; }

    /// <summary>Стоимость (с НКД) той части остатка, у которой себестоимость неизвестна, РУБЛИ. В бумажную прибыль и итог не входит.</summary>
    public decimal UnknownCostHoldingsRub { get; init; }

    /// <summary>Нулевая заглушка для актива, расчёт которого упал.</summary>
    public static PositionIncome Empty(AssetKind kind, ulong assetId, DateOnly asOf) => new()
    {
        AssetKind = kind, AssetId = assetId, Status = PositionIncomeStatus.Closed,
        FirstDate = asOf, LastDate = asOf, HoldingDays = 0, QuantityNow = 0,
        InvestedTotalRub = 0, CostRemainingRub = 0, MarketValueCleanRub = 0, AccruedNowRub = 0,
        RealizedPnlRub = 0, IncomeGrossRub = 0, AccruedPaidRub = 0, AccruedReceivedRub = 0,
        NetCouponIncomeRub = 0, TaxesRub = 0, FeesRub = 0, PrincipalReturnedRub = 0, TotalResultRub = 0,
        HasUnknownLots = true, AccruedUnknown = false,
    };
}

/// <summary>
/// Строка пошагового журнала актива (для раскрытия строки на фронте): состояние ПОСЛЕ операции.
/// <see cref="AvgCostAfterRub"/> — средняя чистая цена остатка за штуку (0 при нулевом остатке);
/// <see cref="RealizedPnlRub"/> — реализованный результат, признанный на этой операции;
/// <see cref="AccruedUsedRub"/> — НКД, реально учтённый калькулятором (0, если неизвестен).
/// </summary>
public sealed record LedgerRow(
    LedgerEntry Entry,
    decimal QuantityAfter,
    decimal AvgCostAfterRub,
    decimal RealizedPnlRub,
    decimal AccruedUsedRub)
{
    /// <summary>Выручка этой операции, пришедшаяся на единицы без известной себестоимости (не результат), РУБЛИ.</summary>
    public decimal UnknownCostProceedsRub { get; init; }
}

/// <summary>Результат расчёта актива: сводка + пошаговые строки.</summary>
public sealed record AssetIncome(PositionIncome Income, IReadOnlyList<LedgerRow> Rows);
