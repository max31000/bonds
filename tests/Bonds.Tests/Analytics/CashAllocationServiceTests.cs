using Bonds.Core.Analytics;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Analytics;

/// <summary>
/// Тесты жадного распределения свободных денег между текущими позициями (plan/17 §B). Проверяет
/// три обязательных краевых кейса из плана: сумма меньше самой дешёвой бумаги, лимит концентрации
/// останавливает докупку лидера и пускает деньги во вторую бумагу, флоатер без YTM использует
/// CurrentYield.
/// </summary>
public class CashAllocationServiceTests
{
    private static CashAllocationCandidate Candidate(
        ulong instrumentId,
        decimal? effectiveYield,
        decimal pricePerLotRub,
        string issuer,
        decimal currentIssuerMarketValueRub = 0m,
        decimal lotSize = 1m,
        bool lotSizeIsAssumed = true) => new()
    {
        InstrumentId = instrumentId,
        Name = $"Bond {instrumentId}",
        Issuer = issuer,
        EffectiveYield = effectiveYield,
        PricePerLotRub = pricePerLotRub,
        LotSize = lotSize,
        LotSizeIsAssumed = lotSizeIsAssumed,
        CurrentIssuerMarketValueRub = currentIssuerMarketValueRub,
    };

    [Fact]
    public void Allocate_AmountBelowCheapestCandidate_ReturnsEmptyList_AllLeftover()
    {
        var candidates = new[]
        {
            Candidate(1, 0.15m, pricePerLotRub: 1000m, issuer: "A"),
            Candidate(2, 0.12m, pricePerLotRub: 1200m, issuer: "B"),
        };

        var result = CashAllocationService.Allocate(amountRub: 500m, candidates, currentPortfolioValueRub: 0m);

        result.Allocations.Should().BeEmpty();
        result.LeftoverRub.Should().Be(500m);
    }

    [Fact]
    public void Allocate_ConcentrationLimit_StopsLeaderAndFillsSecondCandidate()
    {
        // Лидер "A" уже занимает 240 000 из портфеля 250 000 (96%) — лимит 25% не даёт добавить
        // ни одного лота (даже 1 лот раздул бы долю сильно выше лимита), деньги идут в "B".
        var candidates = new[]
        {
            Candidate(1, 0.20m, pricePerLotRub: 10_000m, issuer: "A", currentIssuerMarketValueRub: 240_000m),
            Candidate(2, 0.15m, pricePerLotRub: 10_000m, issuer: "B", currentIssuerMarketValueRub: 0m),
        };

        var result = CashAllocationService.Allocate(
            amountRub: 20_000m, candidates, currentPortfolioValueRub: 250_000m, maxIssuerSharePercent: 25m);

        result.Skipped.Should().ContainSingle(s => s.InstrumentId == 1 && s.Reason == CashAllocationSkipReason.ConcentrationLimit);
        result.Allocations.Should().ContainSingle(a => a.InstrumentId == 2);
        result.Allocations[0].EstimatedCostRub.Should().Be(20_000m);
        result.LeftoverRub.Should().Be(0m);
    }

    [Fact]
    public void Allocate_FloaterWithoutYtm_UsesEffectiveYieldPassedByCaller()
    {
        // Сервис не знает про IsFloater — вызывающий слой (эндпоинт) обязан подставить CurrentYield
        // в EffectiveYield для флоатера/индексируемой бумаги (та же логика, что и в
        // PositionComparisonService/PositionsEndpoints). Здесь проверяем, что переданная доходность
        // используется как есть и кандидат участвует в распределении наравне с обычной бумагой.
        var candidates = new[]
        {
            Candidate(1, effectiveYield: 0.09m, pricePerLotRub: 1000m, issuer: "Floater Co"), // CurrentYield подставлен вызывающим слоем
            Candidate(2, effectiveYield: 0.07m, pricePerLotRub: 1000m, issuer: "Fixed Co"),
        };

        // Портфель на 100 000 — чтобы одна докупка на 1000 не сама по себе упиралась в лимит 25%
        // (нулевой стартовый портфель означал бы 100% доли после первого же лота).
        var result = CashAllocationService.Allocate(amountRub: 1000m, candidates, currentPortfolioValueRub: 100_000m);

        result.Allocations.Should().ContainSingle();
        result.Allocations[0].InstrumentId.Should().Be(1, "выше EffectiveYield — первым получает докупку");
        result.Allocations[0].EffectiveYield.Should().Be(0.09m);
    }

    [Fact]
    public void Allocate_CandidateWithoutYield_IsSkipped_NotRankedAsZero()
    {
        var candidates = new[]
        {
            Candidate(1, effectiveYield: null, pricePerLotRub: 1000m, issuer: "Unknown Co"),
            Candidate(2, effectiveYield: 0.10m, pricePerLotRub: 1000m, issuer: "Known Co"),
        };

        var result = CashAllocationService.Allocate(amountRub: 1000m, candidates, currentPortfolioValueRub: 100_000m);

        result.Skipped.Should().ContainSingle(s => s.InstrumentId == 1 && s.Reason == CashAllocationSkipReason.NoYield);
        result.Allocations.Should().ContainSingle(a => a.InstrumentId == 2);
    }

    [Fact]
    public void Allocate_SortsDescendingByYield_AndSpendsAcrossMultipleCandidates()
    {
        // Портфель маленький (4000) и "B" уже держит 900 (22.5%) — лимит 25% от растущей базы
        // пускает в "B" ровно один дополнительный лот (10 000 руб. не влезет, но 100-рублёвый лот
        // — да), после чего дальнейшие лоты "B" превышают лимит и остаток уходит в "C".
        var candidates = new[]
        {
            Candidate(1, 0.10m, pricePerLotRub: 1000m, issuer: "A"),
            Candidate(2, 0.18m, pricePerLotRub: 100m, issuer: "B", currentIssuerMarketValueRub: 900m),
            Candidate(3, 0.14m, pricePerLotRub: 1000m, issuer: "C"),
        };

        var result = CashAllocationService.Allocate(amountRub: 2000m, candidates, currentPortfolioValueRub: 4000m, maxIssuerSharePercent: 25m);

        // B получает ровно 1 лот (100 руб) — второй лот поднял бы её долю выше 25% — остаток уходит
        // в C (следующая по доходности после B); "A" в списке докупок не оказывается вовсе, т.к. на
        // неё не хватает денег после B и C (1000-рублёвый лот, а остаётся 900).
        result.Allocations.Select(a => a.InstrumentId).Should().ContainInOrder(2, 3);
        result.Allocations.Should().NotContain(a => a.InstrumentId == 1);
        result.Allocations.First(a => a.InstrumentId == 2).Quantity.Should().Be(1m);
        result.Allocations.First(a => a.InstrumentId == 3).EstimatedCostRub.Should().Be(1000m);
        result.LeftoverRub.Should().Be(900m);
    }

    [Fact]
    public void Allocate_NonPositiveAmount_Throws()
    {
        var act = () => CashAllocationService.Allocate(0m, [], currentPortfolioValueRub: 0m);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Allocate_ZeroPrice_IsSkipped_NotDivideByZero()
    {
        var candidates = new[]
        {
            Candidate(1, 0.10m, pricePerLotRub: 0m, issuer: "NoQuote"),
            Candidate(2, 0.08m, pricePerLotRub: 1000m, issuer: "HasQuote"),
        };

        var result = CashAllocationService.Allocate(amountRub: 1000m, candidates, currentPortfolioValueRub: 100_000m);

        result.Skipped.Should().ContainSingle(s => s.InstrumentId == 1 && s.Reason == CashAllocationSkipReason.NoPrice);
        result.Allocations.Should().ContainSingle(a => a.InstrumentId == 2);
    }

    [Fact]
    public void Allocate_ReturnsDisclaimer()
    {
        var result = CashAllocationService.Allocate(1000m, [], currentPortfolioValueRub: 0m);

        result.Disclaimer.Should().NotBeNullOrWhiteSpace();
        result.Disclaimer.Should().Contain("не является");
    }
}
