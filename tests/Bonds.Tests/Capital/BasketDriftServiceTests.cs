using Bonds.Core.Capital;
using Bonds.Core.Models;
using BrokerIds = Bonds.Core.Models.Brokers;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Capital;

/// <summary>
/// Задача 62: отклонения корзин от целей. Все веса/цели/дрейф — ДОЛИ, деньги — в валюте портфеля. Эталоны посчитаны руками.
/// </summary>
public class BasketDriftServiceTests
{
    private static CapitalBasket Basket(
        string code, decimal? target, decimal band = 0.02m, decimal hard = 0.05m, int sort = 0, params CapitalBasketMember[] members) => new()
    {
        Id = (ulong)(code.GetHashCode() & 0xFFFF) + 1, Portfolio = CapitalPortfolios.Rub, Code = code, Title = code, TargetWeight = target,
        Band = band, HardBand = hard, SortOrder = sort, Members = [.. members],
    };

    private static CapitalBasketMember Inst(string key, decimal? sub = null) =>
        new() { Broker = BrokerIds.TInvest, InstrumentKey = key, SubWeight = sub, MatchRule = BasketMatchRules.Instrument };

    private static CapitalBasketMember Class(string cls) =>
        new() { Broker = BrokerIds.TInvest, InstrumentKey = cls, MatchRule = BasketMatchRules.AssetClass };

    private static CapitalPosition Pos(string key, string cls, decimal value) => new(BrokerIds.TInvest, key, cls, key, value);

    [Fact]
    public void Drift_ManualEtalon_LiquidityBelowTarget_DeficitInPortfolioCurrency()
    {
        // Портфель 1 000 000: ликвидность (TMON@) 120 000 при цели 0.15, полоса 0.02 / жёсткая 0.05; облигации 880 000 при цели 0.85.
        var baskets = new[]
        {
            Basket("liquidity", 0.15m, 0.02m, 0.05m, 0, Inst("TMON@")),
            Basket("bonds", 0.85m, 0.02m, 0.05m, 1, Class("bond")),
        };
        var positions = new[] { Pos("TMON@", "fund", 120_000m), Pos("RU000A", "bond", 880_000m) };

        var result = BasketDriftService.Calculate(baskets, positions, freeCash: 0m);

        result.TotalValue.Should().Be(1_000_000m);
        result.BaseValue.Should().Be(1_000_000m);
        var liquidity = result.Rows.Single(r => r.Code == "liquidity");
        liquidity.Value.Should().Be(120_000m);
        liquidity.Weight.Should().Be(0.12m);
        liquidity.Target.Should().Be(0.15m);
        liquidity.Drift.Should().Be(-0.03m);
        liquidity.State.Should().Be(BasketState.Below, "−0.03 за пределами полосы 0.02, но не дальше жёсткой 0.05");
        liquidity.DeficitToTarget.Should().Be(30_000m);

        var bonds = result.Rows.Single(r => r.Code == "bonds");
        bonds.Weight.Should().Be(0.88m);
        bonds.Drift.Should().Be(0.03m);
        bonds.State.Should().Be(BasketState.Above);
        bonds.DeficitToTarget.Should().Be(-30_000m, "избыток — отрицательный дефицит");
    }

    [Theory]
    [InlineData(0.15, BasketState.Ok)]          // drift 0
    [InlineData(0.17, BasketState.Ok)]          // +0.02 ровно на границе полосы — ещё Ok
    [InlineData(0.13, BasketState.Ok)]          // −0.02 ровно на границе
    [InlineData(0.1699, BasketState.Ok)]
    [InlineData(0.18, BasketState.Above)]       // +0.03
    [InlineData(0.20, BasketState.Above)]       // +0.05 ровно на жёсткой границе — ещё не Hard
    [InlineData(0.21, BasketState.HardAbove)]
    [InlineData(0.12, BasketState.Below)]       // −0.03
    [InlineData(0.10, BasketState.Below)]       // −0.05 ровно на жёсткой границе
    [InlineData(0.09, BasketState.HardBelow)]
    public void State_ByBandAndHardBand(double weight, BasketState expected)
    {
        var w = (decimal)weight;
        var baskets = new[] { Basket("a", 0.15m), Basket("b", 0.85m, sort: 1, members: Class("bond")) };
        var positions = new[] { Pos("X", "fund", w * 100_000m), Pos("B", "bond", (1m - w) * 100_000m) };
        baskets[0].Members.Add(Inst("X"));

        var result = BasketDriftService.Calculate(baskets, positions, 0m);

        result.Rows.Single(r => r.Code == "a").State.Should().Be(expected);
    }

    [Fact]
    public void NullTargetBaskets_AreExcludedFromBase_AndDoNotDiluteTargets()
    {
        // 1 000 000 всего, из них 200 000 во «временной» корзине без цели. База = 800 000. Ликвидность 120 000 / 800 000 = 0.15 → Ok.
        var baskets = new[]
        {
            Basket("liquidity", 0.15m, 0.02m, 0.05m, 0, Inst("TMON@")),
            Basket("bonds", 0.85m, 0.02m, 0.05m, 1, Class("bond")),
            Basket("temporary", null, 0.05m, 0.10m, 2, Inst("TPAY")),
        };
        var positions = new[] { Pos("TMON@", "fund", 120_000m), Pos("RU000A", "bond", 680_000m), Pos("TPAY", "fund", 200_000m) };

        var result = BasketDriftService.Calculate(baskets, positions, 0m);

        result.TotalValue.Should().Be(1_000_000m);
        result.BaseValue.Should().Be(800_000m);
        var liquidity = result.Rows.Single(r => r.Code == "liquidity");
        liquidity.Weight.Should().Be(0.15m);
        liquidity.State.Should().Be(BasketState.Ok);
        liquidity.ShareOfTotal.Should().Be(0.12m);

        var temporary = result.Rows.Single(r => r.Code == "temporary");
        temporary.State.Should().Be(BasketState.NoTarget);
        temporary.Target.Should().BeNull();
        temporary.Drift.Should().BeNull();
        temporary.DeficitToTarget.Should().BeNull();
        temporary.ShareOfTotal.Should().Be(0.2m);
    }

    [Fact]
    public void FreeCash_IsSeparateRow_AndIncludedInBase()
    {
        var baskets = new[] { Basket("liquidity", 0.20m, 0.02m, 0.05m, 0, Inst("TMON@")) };
        var positions = new[] { Pos("TMON@", "fund", 100_000m) };

        var result = BasketDriftService.Calculate(baskets, positions, freeCash: 400_000m);

        result.TotalValue.Should().Be(500_000m);
        result.BaseValue.Should().Be(500_000m, "кэш входит в базу");
        var cash = result.Rows.Single(r => r.Code == SystemBaskets.Cash);
        cash.Value.Should().Be(400_000m);
        cash.Weight.Should().Be(0.8m);
        cash.State.Should().Be(BasketState.NoTarget);
        result.Rows.Single(r => r.Code == "liquidity").Weight.Should().Be(0.2m);
    }

    [Fact]
    public void EveryPosition_LandsInExactlyOneBasket()
    {
        var baskets = new[]
        {
            Basket("liquidity", 0.15m, 0.02m, 0.05m, 0, Inst("TMON@"), Inst("TSAV")),
            Basket("bonds", 0.85m, 0.02m, 0.05m, 1, Class("bond")),
            Basket("temporary", null, 0.05m, 0.10m, 2, Inst("TPAY")),
        };
        var positions = new[]
        {
            Pos("TMON@", "fund", 10m), Pos("TSAV", "fund", 20m), Pos("TPAY", "fund", 30m), Pos("RU000A", "bond", 40m), Pos("RU000B", "bond", 50m),
            Pos("TBRU@", "fund", 60m),   // фонд без члена и без правила класса → unassigned
            Pos("CNY", "currency", 70m), // валюта T-Invest → fx
        };

        var result = BasketDriftService.Calculate(baskets, positions, 0m);

        var allAssigned = result.Rows.Where(r => r.Code != SystemBaskets.Cash).SelectMany(r => r.Positions).ToList();
        allAssigned.Should().HaveCount(positions.Length);
        allAssigned.Select(p => p.Key).Should().OnlyHaveUniqueItems().And.BeEquivalentTo(positions.Select(p => p.Key));
        result.Rows.Single(r => r.Code == "liquidity").Positions.Select(p => p.Key).Should().BeEquivalentTo("TMON@", "TSAV");
        result.Rows.Single(r => r.Code == "bonds").Positions.Select(p => p.Key).Should().BeEquivalentTo("RU000A", "RU000B");
        result.Rows.Single(r => r.Code == SystemBaskets.Unassigned).Positions.Select(p => p.Key).Should().BeEquivalentTo("TBRU@");
        result.Rows.Single(r => r.Code == SystemBaskets.Fx).Positions.Select(p => p.Key).Should().BeEquivalentTo("CNY");
        result.Rows.Sum(r => r.Value).Should().Be(280m, "Σ корзин = стоимость портфеля");
    }

    [Fact]
    public void ExplicitMember_BeatsAssetClassRule()
    {
        // Облигация явно в «временной» корзине — правило «все облигации» её не забирает.
        var baskets = new[]
        {
            Basket("bonds", 0.85m, 0.02m, 0.05m, 0, Class("bond")),
            Basket("temporary", null, 0.05m, 0.10m, 1, Inst("RU000X")),
        };
        var positions = new[] { Pos("RU000X", "bond", 100m), Pos("RU000Y", "bond", 300m) };

        var result = BasketDriftService.Calculate(baskets, positions, 0m);

        result.Rows.Single(r => r.Code == "temporary").Positions.Select(p => p.Key).Should().BeEquivalentTo("RU000X");
        result.Rows.Single(r => r.Code == "bonds").Positions.Select(p => p.Key).Should().BeEquivalentTo("RU000Y");
    }

    [Fact]
    public void KeyMatching_IgnoresCaseAndTrailingAt()
    {
        var baskets = new[] { Basket("liquidity", 0.15m, 0.02m, 0.05m, 0, Inst("tmon@")) };
        var positions = new[] { Pos("TMON", "fund", 100m) }; // брокер отдал тикер без «@»

        BasketDriftService.Calculate(baskets, positions, 0m).Rows.Single(r => r.Code == "liquidity").Value.Should().Be(100m);
    }

    [Fact]
    public void FreedomMember_WithoutExchangeSuffix_MatchesBaseTicker_ButSuffixedMemberIsExact()
    {
        var baskets = new[]
        {
            Basket("core", 0.6m, 0.05m, 0.10m, 0, new CapitalBasketMember { Broker = BrokerIds.Freedom, InstrumentKey = "VUAA.EU" }),
            Basket("sat", null, 0.05m, 0.10m, 1, new CapitalBasketMember { Broker = BrokerIds.Freedom, InstrumentKey = "LUV" }),
        };
        var positions = new[]
        {
            new CapitalPosition(BrokerIds.Freedom, "VUAA.EU", "etf", "a", 10m),
            new CapitalPosition(BrokerIds.Freedom, "VUAA.UK", "etf", "b", 20m),   // другая площадка — НЕ член «VUAA.EU»
            new CapitalPosition(BrokerIds.Freedom, "LUV.US", "stock", "c", 30m),  // «LUV» → LUV.US
            new CapitalPosition(BrokerIds.Freedom, "LUVX.US", "stock", "d", 40m), // «LUV» ≠ префикс другого тикера
        };

        var result = BasketDriftService.Calculate(baskets, positions, 0m);

        result.Rows.Single(r => r.Code == "core").Positions.Select(p => p.Key).Should().BeEquivalentTo("VUAA.EU");
        result.Rows.Single(r => r.Code == "sat").Positions.Select(p => p.Key).Should().BeEquivalentTo("LUV.US");
        result.Rows.Single(r => r.Code == SystemBaskets.Unassigned).Positions.Select(p => p.Key).Should().BeEquivalentTo("VUAA.UK", "LUVX.US");
    }

    [Fact]
    public void BrokerMustMatch_ForMember()
    {
        var baskets = new[] { Basket("liquidity", 0.15m, 0.02m, 0.05m, 0, Inst("TMON@")) };
        var positions = new[] { new CapitalPosition(BrokerIds.Freedom, "TMON@", "etf", "x", 100m) };

        var result = BasketDriftService.Calculate(baskets, positions, 0m);

        result.Rows.Single(r => r.Code == "liquidity").Value.Should().Be(0m);
        result.Rows.Single(r => r.Code == SystemBaskets.Unassigned).Value.Should().Be(100m);
    }

    [Fact]
    public void EmptyPortfolio_NoDivisionByZero()
    {
        var baskets = new[] { Basket("liquidity", 0.15m) };

        var result = BasketDriftService.Calculate(baskets, [], 0m);

        result.TotalValue.Should().Be(0m);
        var row = result.Rows.Single(r => r.Code == "liquidity");
        row.Weight.Should().Be(0m);
        row.State.Should().Be(BasketState.NoData, "база доли 0 — отклонение не считается");
        row.Drift.Should().BeNull();
        row.DeficitToTarget.Should().BeNull();
    }

    [Fact]
    public void SystemRows_AreOnlyEmittedWhenNonEmpty_ExceptCash()
    {
        var result = BasketDriftService.Calculate([Basket("a", 1m, members: Inst("X"))], [Pos("X", "fund", 5m)], 0m);

        result.Rows.Select(r => r.Code).Should().BeEquivalentTo("a", SystemBaskets.Cash);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void NonPositiveBase_TargetBasketsAreNoData_NoDriftNoDeficit(int baseDelta)
    {
        // Вся стоимость — в корзине без цели: база доли = 0 (или меньше при отрицательной стоимости позиции).
        var baskets = new[] { Basket("liquidity", 0.15m, 0.02m, 0.05m, 0, Inst("TMON@")), Basket("temporary", null, 0.02m, 0.05m, 1, Inst("TPAY")) };
        var positions = new[] { Pos("TPAY", "fund", 100m), Pos("TMON@", "fund", baseDelta) };

        var result = BasketDriftService.Calculate(baskets, positions, 0m);

        var liquidity = result.Rows.Single(r => r.Code == "liquidity");
        liquidity.State.Should().Be(BasketState.NoData);
        liquidity.Target.Should().Be(0.15m);
        liquidity.Drift.Should().BeNull();
        liquidity.DeficitToTarget.Should().BeNull();
        result.Rows.Single(r => r.Code == "temporary").State.Should().Be(BasketState.NoTarget);
    }

    [Fact]
    public void EmptyPortfolio_TargetBasketsAreNoData()
    {
        var result = BasketDriftService.Calculate([Basket("liquidity", 1m, 0.02m, 0.05m, 0, Inst("TMON@"))], [], 0m);

        result.Rows.Single(r => r.Code == "liquidity").State.Should().Be(BasketState.NoData);
    }
}
