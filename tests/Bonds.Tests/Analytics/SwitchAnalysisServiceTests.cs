using Bonds.Core.Analytics;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Analytics;

/// <summary>
/// Тесты анализа замены между текущими позициями (plan/06 B4, spec §3/§9). Проверяет учёт
/// комиссии обеих сделок, дисклеймер, и что сервис принципиально работает только с переданными
/// кандидатами (текущими позициями) — не имеет доступа к "вселенной" бумаг.
/// </summary>
public class SwitchAnalysisServiceTests
{
    [Fact]
    public void Compare_HigherYieldTarget_ShowsPositiveNetBenefit_AfterBothCommissions()
    {
        var hold = new SwitchCandidate { PositionId = 1, MarketValueRub = 100000m, EffectiveYield = 0.10m };
        var target = new SwitchCandidate { PositionId = 2, MarketValueRub = 100000m, EffectiveYield = 0.16m };

        var result = SwitchAnalysisService.Compare(hold, target, horizonYears: 3m,
            sellCommissionRate: 0.003m, buyCommissionRate: 0.003m);

        result.SellCommissionRub.Should().Be(300m, "0.3% от 100000");
        result.BuyCommissionRub.Should().BeApproximately(299.1m, 0.5m, "0.3% от суммы после продажи минус комиссия продажи");
        result.TotalSwitchCostRub.Should().BeApproximately(599.1m, 1m);
        result.IsSwitchFavorable.Should().BeTrue("спред доходности 6% годовых за 3 года многократно перекрывает ~0.6% комиссий");
        result.NetBenefitRub.Should().BeGreaterThan(0m);
        result.BreakEvenYears.Should().NotBeNull();
        result.BreakEvenYears!.Value.Should().BeLessThan(1m, "при спреде 6% и комиссии <1% окупаемость быстрая");
    }

    [Fact]
    public void Compare_LowerYieldTarget_NeverFavorable()
    {
        var hold = new SwitchCandidate { PositionId = 1, MarketValueRub = 50000m, EffectiveYield = 0.15m };
        var target = new SwitchCandidate { PositionId = 2, MarketValueRub = 50000m, EffectiveYield = 0.10m };

        var result = SwitchAnalysisService.Compare(hold, target, horizonYears: 2m);

        result.IsSwitchFavorable.Should().BeFalse();
        result.NetBenefitRub.Should().BeLessThan(0m);
        result.BreakEvenYears.Should().BeNull("отрицательный спред доходности никогда не окупается");
    }

    [Fact]
    public void Compare_CommissionsScaleWithBothTrades_NotJustOne()
    {
        var hold = new SwitchCandidate { PositionId = 1, MarketValueRub = 200000m, EffectiveYield = 0.10m };
        var target = new SwitchCandidate { PositionId = 2, MarketValueRub = 200000m, EffectiveYield = 0.10m };

        // Одинаковая доходность ⇒ выгода от спреда равна нулю; весь NetBenefit — это минус сумма
        // обеих комиссий (учёт ОБЕИХ сделок — критическое требование spec §3/§9).
        var result = SwitchAnalysisService.Compare(hold, target, horizonYears: 1m,
            sellCommissionRate: 0.005m, buyCommissionRate: 0.005m);

        result.NetBenefitRub.Should().BeApproximately(-result.TotalSwitchCostRub, 0.01m);
        result.TotalSwitchCostRub.Should().BeGreaterThan(1000m, "0.5%+0.5% от 200000 это около 2000 за обе сделки");
        result.SellCommissionRub.Should().BeGreaterThan(0m);
        result.BuyCommissionRub.Should().BeGreaterThan(0m);
    }

    [Fact]
    public void Compare_MissingYieldOnEitherSide_MarksResultIncomplete()
    {
        var hold = new SwitchCandidate { PositionId = 1, MarketValueRub = 10000m, EffectiveYield = null };
        var target = new SwitchCandidate { PositionId = 2, MarketValueRub = 10000m, EffectiveYield = 0.10m };

        var result = SwitchAnalysisService.Compare(hold, target, horizonYears: 1m);

        result.YieldDataIncomplete.Should().BeTrue();
        result.IsSwitchFavorable.Should().BeFalse("недостоверный результат не может считаться выгодным");
    }

    [Fact]
    public void Compare_AlwaysIncludesDisclaimer_AboutScopeAndTax()
    {
        var hold = new SwitchCandidate { PositionId = 1, MarketValueRub = 1000m, EffectiveYield = 0.1m };
        var target = new SwitchCandidate { PositionId = 2, MarketValueRub = 1000m, EffectiveYield = 0.1m };

        var result = SwitchAnalysisService.Compare(hold, target, horizonYears: 1m);

        result.Disclaimer.Should().NotBeNullOrWhiteSpace();
        result.Disclaimer.Should().Contain("текущие позиции");
        result.Disclaimer.Should().Contain("Налог");
    }

    [Fact]
    public void Compare_SpreadGain_ComputedOnCapitalAfterSaleCommission()
    {
        // T-10/L-5: в target идёт меньший капитал, чем полная стоимость hold (после комиссии продажи).
        // Выгода спреда должна считаться на капитале ПОСЛЕ комиссии продажи, а не на полной стоимости.
        var hold = new SwitchCandidate { PositionId = 1, MarketValueRub = 100_000m, EffectiveYield = 0.10m };
        var target = new SwitchCandidate { PositionId = 2, MarketValueRub = 100_000m, EffectiveYield = 0.20m };

        var result = SwitchAnalysisService.Compare(hold, target, horizonYears: 1m,
            sellCommissionRate: 0.01m, buyCommissionRate: 0m);

        // netProceedsAfterSale = 99000; spreadGain = 99000·0.10·1 = 9900; netBenefit = 9900 − 1000 = 8900.
        // На полной базе (старое поведение) было бы 10000 − 1000 = 9000.
        result.NetBenefitRub.Should().BeApproximately(8900m, 0.01m,
            "спред считается на капитале после комиссии продажи, не на полной стоимости hold");
    }

    [Fact]
    public void Compare_Disclaimer_NotesLinearNonCompoundingEstimate()
    {
        var hold = new SwitchCandidate { PositionId = 1, MarketValueRub = 1000m, EffectiveYield = 0.1m };
        var target = new SwitchCandidate { PositionId = 2, MarketValueRub = 1000m, EffectiveYield = 0.1m };

        var result = SwitchAnalysisService.Compare(hold, target, horizonYears: 1m);

        result.Disclaimer.Should().Contain("линейн",
            "дисклеймер должен явно оговаривать линейность оценки без компаундирования (T-10/L-5)");
    }

    [Fact]
    public void Compare_InvalidHorizon_Throws()
    {
        var hold = new SwitchCandidate { PositionId = 1, MarketValueRub = 1000m, EffectiveYield = 0.1m };
        var target = new SwitchCandidate { PositionId = 2, MarketValueRub = 1000m, EffectiveYield = 0.1m };

        var act = () => SwitchAnalysisService.Compare(hold, target, horizonYears: 0m);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
