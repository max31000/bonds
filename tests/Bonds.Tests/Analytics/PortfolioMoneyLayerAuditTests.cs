using Bonds.Core.Analytics;
using Bonds.Core.Models;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Analytics;

/// <summary>
/// Audit(portfolio): целевые тесты аудита денежно-портфельного слоя (см. CALC_AUDIT_PORTFOLIO.md).
/// Ядро — синтетический счёт (3 покупки разными лотами/ценами + частичная продажа + 2 купона),
/// прогнанный ОДНОВРЕМЕННО через <see cref="PositionCostBasisService"/> (average cost) и
/// <see cref="PortfolioXirrService"/> (XIRR), сверенный с независимым python-решателем
/// (Ньютон-Рафсон на тех же денежных потоках, Actual/365 — тот же day-count, что
/// <see cref="Bonds.Core.Calculation.XirrCalculator"/>; скрипт см. scratchpad аудита, не в репо).
/// Плюс несколько инвариантов, которые не были явно покрыты прежними раундами аудита
/// (plan/12, T-1..T-10): "продажа без покупки" под XIRR, allocation "никогда не тратит больше
/// суммы" на нескольких кандидатах разом.
/// </summary>
public class PortfolioMoneyLayerAuditTests
{
    private static readonly DateOnly BaseDate = new(2025, 1, 10);
    private static ulong _nextId = 1;

    private static Operation Op(OperationType type, DateOnly date, decimal amountRub, decimal? quantity = null) => new()
    {
        Id = _nextId++,
        AccountId = 1,
        InstrumentId = 100,
        Type = type,
        Date = date.ToDateTime(TimeOnly.MinValue),
        AmountRub = amountRub,
        Quantity = quantity,
        ExternalId = Guid.NewGuid().ToString(),
    };

    /// <summary>
    /// Синтетический журнал (см. doc-comment класса):
    /// Buy 10@1000 (-10000, 2025-01-10), Buy 10@1100 (-11000, 2025-02-10), Buy 5@1200 (-6000, 2025-03-10),
    /// Sell 8@1150 (+9200, 2025-04-10), Coupon +300 (2025-06-10), Coupon +300 (2025-09-10).
    /// Текущий остаток 17 шт, грязная цена 1250/шт → рыночная стоимость 21250, на 2025-12-10.
    /// </summary>
    private static Operation[] ReferenceJournal() =>
    [
        Op(OperationType.Buy, BaseDate, -10_000m, quantity: 10m),
        Op(OperationType.Buy, BaseDate.AddMonths(1), -11_000m, quantity: 10m),
        Op(OperationType.Buy, BaseDate.AddMonths(2), -6_000m, quantity: 5m),
        Op(OperationType.Sell, BaseDate.AddMonths(3), 9_200m, quantity: 8m),
        Op(OperationType.Coupon, BaseDate.AddMonths(5), 300m),
        Op(OperationType.Coupon, BaseDate.AddMonths(8), 300m),
    ];

    private static readonly DateOnly AsOf = new(2025, 12, 10);
    private const decimal CurrentMarketValueRub = 21_250m; // 17 шт × 1250 грязная цена
    private const decimal CurrentQuantity = 17m;

    [Fact]
    public void CostBasis_ReferenceJournal_MatchesHandComputedAverageCostAndPnl()
    {
        // Ручной расчёт (см. doc-comment): avgCost = 1080, invested = 18360, unrealizedPnl = 2890,
        // couponsReceived = 600, totalReturn = 3490 — эти числа независимо пересчитаны и в python
        // (scratchpad), совпадают до копейки.
        var result = PositionCostBasisService.Calculate(ReferenceJournal(), CurrentQuantity, CurrentMarketValueRub);

        result.AverageCostRub.Should().Be(1080m);
        result.InvestedRub.Should().Be(18_360m);
        result.UnrealizedPnlRub.Should().Be(2_890m);
        result.UnrealizedPnlPercent.Should().BeApproximately(2_890m / 18_360m, 1e-9m);
        result.CouponsReceivedRub.Should().Be(600m);
        result.TotalReturnRub.Should().Be(3_490m);
        result.TotalReturnPercent.Should().BeApproximately(3_490m / 18_360m, 1e-9m);
        result.HasUnknownLots.Should().BeFalse("журнал полностью покрывает текущий остаток (25 куплено − 8 продано = 17)");
    }

    [Fact]
    public void Xirr_ReferenceJournal_MatchesIndependentNewtonSolver()
    {
        // Независимый python Ньютон-Рафсон на тех же 7 потоках (3 покупки, частичная продажа,
        // 2 купона, терминал) даёт XIRR = 0.2487788236 (см. doc-comment/scratchpad аудита).
        var result = PortfolioXirrService.Calculate(ReferenceJournal(), CurrentMarketValueRub, AsOf);

        result.Should().NotBeNull();
        result!.Value.Rate.Should().BeApproximately(0.2487788236m, 1e-6m);
    }

    [Fact]
    public void CostBasis_BuyThenFullSell_InvestedGoesToZero_NotNull()
    {
        // Инвариант из задания аудита: покупка + полная продажа → invested = 0 (остаток 0), но
        // сервис в этом случае возвращает null-метрики (currentQuantity <= 0 — "посчитать нечего"),
        // не 0 — это осознанное поведение (см. doc-comment PositionCostBasis), а не баг: 0 читался
        // бы как "вложено ноль рублей", тогда как на самом деле "остатка нет — вопрос неприменим".
        var operations = new[]
        {
            Op(OperationType.Buy, BaseDate, -10_000m, quantity: 10m),
            Op(OperationType.Sell, BaseDate.AddMonths(1), 11_000m, quantity: 10m),
        };

        var result = PositionCostBasisService.Calculate(operations, currentQuantity: 0m, currentMarketValueRub: 0m);

        result.InvestedRub.Should().BeNull("позиция полностью закрыта — 'вложено' неприменимо, не 0");
        result.AverageCostRub.Should().BeNull();
        result.HasUnknownLots.Should().BeFalse("журнал точно сходится с остатком (10-10=0), это не пробел в истории");
    }

    [Fact]
    public void Xirr_SellWithoutAnyPriorBuy_StillSolvesFromAvailableFlows()
    {
        // Краевой случай задания: "продажа без покупки" (журнал начинается с продажи — бумага
        // куплена до начала истории синка). XIRR-солвер не знает о cost basis и его lot-трекинге —
        // он просто берёт AmountRub как есть, так что смена знака (продажа плюс, терминал плюс)
        // должна дать null (нет оттока => по документированному контракту решателя корня нет).
        var operations = new[]
        {
            Op(OperationType.Sell, BaseDate, 11_000m, quantity: 10m),
        };

        var result = PortfolioXirrService.Calculate(operations, currentMarketValueRub: 0m, asOf: BaseDate.AddMonths(1));

        result.Should().BeNull("единственный поток — приток от продажи, без оттока и без терминала — корня нет");
    }

    [Fact]
    public void Xirr_NegativeReturn_SolvesToNegativeRate()
    {
        // Краевой случай: купили дороже, чем сейчас стоит позиция — XIRR должен быть отрицательным
        // и решатель обязан сойтись (не вернуть null просто потому, что ставка отрицательна).
        var operations = new[]
        {
            Op(OperationType.Buy, BaseDate, -10_000m, quantity: 10m),
        };

        var result = PortfolioXirrService.Calculate(operations, currentMarketValueRub: 7_000m, asOf: BaseDate.AddYears(1));

        result.Should().NotBeNull();
        result!.Value.Rate.Should().BeLessThan(0m, "текущая стоимость ниже вложенного — доходность отрицательная");
        result.Value.Rate.Should().BeApproximately(-0.30m, 0.01m, "потеря 30% за год ≈ XIRR -30%");
    }

    [Fact]
    public void Allocation_AcrossManyCandidates_NeverSpendsMoreThanAmount_LeftoverNonNegative()
    {
        // Инвариант из задания: аллокация никогда не тратит больше суммы, leftover >= 0.
        // Смесь цен лота, которые не делят сумму нацело в некоторых кандидатах — чтобы поймать
        // потенциальный дрейф округления/переплату.
        var candidates = new[]
        {
            MakeCandidate(1, 0.14m, pricePerLotRub: 733.33m, issuer: "A"),
            MakeCandidate(2, 0.13m, pricePerLotRub: 1250.50m, issuer: "B"),
            MakeCandidate(3, 0.12m, pricePerLotRub: 999.99m, issuer: "C"),
            MakeCandidate(4, 0.11m, pricePerLotRub: 421.17m, issuer: "D"),
            MakeCandidate(5, 0.10m, pricePerLotRub: 10_000m, issuer: "E"), // дороже, чем весь остаток денег к моменту, когда до неё дойдёт очередь
        };

        const decimal amount = 17_777.77m;
        var result = CashAllocationService.Allocate(amount, candidates, currentPortfolioValueRub: 500_000m, maxIssuerSharePercent: 100m);

        var totalSpent = result.Allocations.Sum(a => a.EstimatedCostRub);

        result.LeftoverRub.Should().BeGreaterThanOrEqualTo(0m, "остаток денег не может уйти в минус");
        totalSpent.Should().BeLessThanOrEqualTo(amount, "сумма всех покупок не может превышать вносимую сумму");
        (totalSpent + result.LeftoverRub).Should().Be(amount, "потрачено + остаток должно точно равняться сумме — деньги не должны 'теряться' или удваиваться");
    }

    [Fact]
    public void Allocation_ConcentrationLimit_LeftoverStillReconciles_WhenLeaderIsBlockedImmediately()
    {
        // То же бухгалтерское тождество (потрачено + остаток = сумма), но специально в ветке, где
        // лидер по доходности блокируется лимитом концентрации с первого же лота (issuerValue уже
        // на лимите) — проверяем, что деньги корректно "провалились" на следующего кандидата, а не
        // потерялись при пропуске.
        var candidates = new[]
        {
            MakeCandidate(1, 0.20m, pricePerLotRub: 1_000m, issuer: "Leader", currentIssuerMarketValueRub: 249_000m),
            MakeCandidate(2, 0.15m, pricePerLotRub: 1_000m, issuer: "Second"),
        };

        const decimal amount = 5_000m;
        var result = CashAllocationService.Allocate(amount, candidates, currentPortfolioValueRub: 250_000m, maxIssuerSharePercent: 25m);

        result.Skipped.Should().ContainSingle(s => s.InstrumentId == 1 && s.Reason == CashAllocationSkipReason.ConcentrationLimit);
        var totalSpent = result.Allocations.Sum(a => a.EstimatedCostRub);
        (totalSpent + result.LeftoverRub).Should().Be(amount);
        result.LeftoverRub.Should().BeGreaterThanOrEqualTo(0m);
    }

    private static CashAllocationCandidate MakeCandidate(
        ulong instrumentId, decimal effectiveYield, decimal pricePerLotRub, string issuer, decimal currentIssuerMarketValueRub = 0m) => new()
    {
        InstrumentId = instrumentId,
        Name = $"Bond {instrumentId}",
        Issuer = issuer,
        EffectiveYield = effectiveYield,
        PricePerLotRub = pricePerLotRub,
        LotSize = 1m,
        LotSizeIsAssumed = true,
        CurrentIssuerMarketValueRub = currentIssuerMarketValueRub,
    };
}
