using Bonds.Core.Analytics;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Analytics;

/// <summary>
/// Тесты анализа замены v2 (задача 48): компаундирование на эффективном горизонте, НДФЛ на купоны (13%),
/// слиппедж обеих ног, налог продажи, усечение горизонта сроком цели, срок окупаемости, дюрации.
/// <para>
/// Эталонные числа посчитаны вручную (промежуточные значения FV обеих ног — в комментариях тестов).
/// Сокращения: MV — грязная стоимость hold; y_at = y × 0.87; FV_hold = MV·(1+y_hold_at)^h;
/// proceeds = MV − sellComm − sellSlip − saleTax; invested = proceeds − buyComm − buySlip;
/// FV_switch = invested·(1+y_target_at)^h; NetBenefit = FV_switch − FV_hold.
/// </para>
/// </summary>
public class SwitchAnalysisServiceTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private static SwitchCandidate Hold(decimal mv = 100_000m, decimal? y = 0.10m, decimal? halfSpread = null,
        decimal? invested = null, bool unknownLots = true, decimal? duration = null) => new()
    {
        PositionId = 1, MarketValueRub = mv, EffectiveYield = y, HalfSpreadFraction = halfSpread,
        InvestedRub = invested, HasUnknownLots = unknownLots, MacaulayDurationYears = duration,
    };

    private static SwitchCandidate Target(decimal? y = 0.105m, decimal? halfSpread = null, DateOnly? horizonDate = null,
        bool isOffer = false, decimal? duration = null) => new()
    {
        PositionId = 2, MarketValueRub = 1_000m, EffectiveYield = y, HalfSpreadFraction = halfSpread,
        TargetHorizonDate = horizonDate, TargetHorizonIsOffer = isOffer, MacaulayDurationYears = duration,
    };

    [Fact]
    public void Compare_LinearlyPositive_ButNegativeAfterCouponTaxAndSlippage()
    {
        // MV=100000, y_hold=10%, y_target=10.5% (спред 0.5 п.п.), h=2, комиссии 0.3%/0.3%, полуспреды hold 0.2% / цели 0.4%.
        // v1 (линейно): 99700·0.005·2 − 599.1 = +397.9 → «выгодно».
        // v2:
        //   y_hold_at = 0.087, y_target_at = 0.09135
        //   FV_hold   = 100000·1.087² = 118156.90
        //   sellComm = 300, sellSlip = 200 → proceeds = 99500 (налог продажи неизвестен → 0)
        //   buyComm = 298.5, buySlip = 398 → invested = 98803.5
        //   FV_switch = 98803.5·1.09135² = 98803.5·1.1910448225 = 117679.397
        //   NetBenefit = 117679.397 − 118156.90 = −477.503
        var result = SwitchAnalysisService.Compare(
            Hold(halfSpread: 0.002m), Target(0.105m, halfSpread: 0.004m), horizonYears: 2m,
            sellCommissionRate: 0.003m, buyCommissionRate: 0.003m, asOf: Today);

        result.HoldYieldAfterTaxFraction.Should().BeApproximately(0.087m, 1e-9m);
        result.TargetYieldAfterTaxFraction.Should().BeApproximately(0.09135m, 1e-9m);
        result.HoldFutureValueRub.Should().BeApproximately(118_156.90m, 0.01m);
        result.SellCommissionRub.Should().Be(300m);
        result.SellSlippageRub.Should().Be(200m);
        result.ProceedsRub.Should().Be(99_500m);
        result.BuyCommissionRub.Should().Be(298.5m);
        result.BuySlippageRub.Should().Be(398m);
        result.CapitalRub.Should().Be(98_803.5m);
        result.SwitchFutureValueRub.Should().BeApproximately(117_679.397m, 0.01m);
        result.NetBenefitRub.Should().BeApproximately(-477.503m, 0.01m);
        result.IsSwitchFavorable.Should().BeFalse("после налога на купоны и слиппеджа выгода отрицательна, хотя линейно была +398");
        result.SlippageUnknown.Should().BeFalse();
        result.TotalSwitchCostRub.Should().Be(598.5m, "TotalSwitchCostRub = только комиссии обеих сделок");
        // AnnualizedBenefit = (117679.397/118156.90)^(1/2) − 1 = −0.0020227
        result.AnnualizedBenefitFraction!.Value.Should().BeApproximately(-0.0020227m, 1e-6m);
    }

    [Fact]
    public void Compare_SameCandidateWithoutSlippage_IsPositive_ShowingSlippageFlipsTheSign()
    {
        // Те же входы без стакана: слиппедж = 0 (SlippageUnknown=true).
        //   proceeds = 99700, buyComm = 299.1, invested = 99400.9
        //   FV_switch = 99400.9·1.1910448225 = 118390.927 → NetBenefit = +234.027
        var result = SwitchAnalysisService.Compare(
            Hold(), Target(0.105m), horizonYears: 2m, sellCommissionRate: 0.003m, buyCommissionRate: 0.003m, asOf: Today);

        result.SlippageUnknown.Should().BeTrue("bid/offer неизвестны — слиппедж не учтён и помечен");
        result.SellSlippageRub.Should().Be(0m);
        result.BuySlippageRub.Should().Be(0m);
        result.NetBenefitRub.Should().BeApproximately(234.027m, 0.01m);
        result.IsSwitchFavorable.Should().BeTrue();
    }

    [Fact]
    public void Compare_CouponTax_AloneFlipsTheSign()
    {
        // MV=100000, y_hold=10%, y_target=10.7%, h=1, комиссии 0.3%/0.3%, слиппедж неизвестен.
        // invested = 100000·0.997·0.997 = 99400.9
        // БЕЗ НДФЛ на купоны: FV_switch = 99400.9·1.107 = 110037.80; FV_hold = 110000 → +36.80 (выгодно).
        // С НДФЛ 13%: y_hold_at = 0.087, y_target_at = 0.09309;
        //   FV_hold = 108700; FV_switch = 99400.9·1.09309 = 108654.13 → −45.87 (невыгодно).
        var result = SwitchAnalysisService.Compare(
            Hold(), Target(0.107m), horizonYears: 1m, sellCommissionRate: 0.003m, buyCommissionRate: 0.003m, asOf: Today);

        result.HoldFutureValueRub.Should().BeApproximately(108_700m, 0.01m);
        result.SwitchFutureValueRub.Should().BeApproximately(108_654.13m, 0.01m);
        result.NetBenefitRub.Should().BeApproximately(-45.87m, 0.01m);
        result.IsSwitchFavorable.Should().BeFalse();
    }

    [Fact]
    public void Compare_TargetOfferInEightTenthsOfYear_CutsHorizon_AndFlagsIt()
    {
        // h_user=2, оферта цели через 292 дня = 0.8 года → h_eff = 0.8.
        // MV=100000, y_hold=10% (at 0.087), y_target=16% (at 0.1392), комиссии 0.3%, полуспреды 0.1%/0.1%:
        //   FV_hold = 100000·1.087^0.8 = 106901.46; proceeds = 99600; invested = 99600 − 298.8 − 99.6 = 99201.6
        //   FV_switch = 99201.6·1.1392^0.8 = 110102.87 → NetBenefit = +3201.41
        // На полных 2 годах было бы +10584.62 — горизонт НЕ даёт «2 года преимущества».
        var offerDate = Today.AddDays(292);
        var result = SwitchAnalysisService.Compare(
            Hold(halfSpread: 0.001m), Target(0.16m, halfSpread: 0.001m, horizonDate: offerDate, isOffer: true),
            horizonYears: 2m, sellCommissionRate: 0.003m, buyCommissionRate: 0.003m, asOf: Today);

        result.HorizonYears.Should().Be(2m);
        result.EffectiveHorizonYears.Should().BeApproximately(0.8m, 1e-9m);
        result.HorizonLimitedByTarget.Should().BeTrue();
        result.TargetHorizonDate.Should().Be(offerDate);
        result.TargetHorizonIsOffer.Should().BeTrue();
        result.HoldFutureValueRub.Should().BeApproximately(106_901.46m, 0.05m);
        result.CapitalRub.Should().BeApproximately(99_201.6m, 0.001m);
        result.SwitchFutureValueRub.Should().BeApproximately(110_102.87m, 0.05m);
        result.NetBenefitRub.Should().BeApproximately(3_201.41m, 0.05m);
    }

    [Fact]
    public void Compare_TargetFartherThanHorizon_DoesNotCutHorizon()
    {
        var result = SwitchAnalysisService.Compare(
            Hold(), Target(0.16m, horizonDate: Today.AddDays(365 * 5)), horizonYears: 2m, asOf: Today);

        result.EffectiveHorizonYears.Should().Be(2m);
        result.HorizonLimitedByTarget.Should().BeFalse();
        result.TargetHorizonDate.Should().BeNull();
    }

    [Theory]
    [InlineData(30)]   // 30 дней = 0.082 года < 0.25
    [InlineData(0)]    // уже сегодня
    [InlineData(-10)]  // дата в прошлом (снимок устарел)
    public void Compare_TargetAlmostMatured_HorizonFloorIsQuarterOfYear(int daysToTarget)
    {
        var result = SwitchAnalysisService.Compare(
            Hold(), Target(0.16m, horizonDate: Today.AddDays(daysToTarget)), horizonYears: 2m, asOf: Today);

        result.EffectiveHorizonYears.Should().Be(0.25m);
        result.HorizonLimitedByTarget.Should().BeTrue();
    }

    [Fact]
    public void Compare_UserHorizonBelowFloor_IsNotInflated()
    {
        var result = SwitchAnalysisService.Compare(
            Hold(), Target(0.16m, horizonDate: Today.AddDays(10)), horizonYears: 0.1m, asOf: Today);

        result.EffectiveHorizonYears.Should().Be(0.1m, "пол 0.25 только для усечения сроком цели, не повышает горизонт пользователя");
        result.HorizonLimitedByTarget.Should().BeFalse();
    }

    [Fact]
    public void Compare_WithoutAsOf_DoesNotCutHorizon()
    {
        var result = SwitchAnalysisService.Compare(Hold(), Target(0.16m, horizonDate: Today.AddDays(10)), horizonYears: 2m);

        result.EffectiveHorizonYears.Should().Be(2m);
        result.HorizonLimitedByTarget.Should().BeFalse();
    }

    [Fact]
    public void Compare_SameBond_BenefitIsMinusCostsGrownByHoldYield()
    {
        // Одна и та же бумага: y=10% на обеих ногах (at 0.087), MV=200000, h=1, комиссии 0.5%/0.5%, без слиппеджа.
        //   FV_hold = 200000·1.087 = 217400
        //   proceeds = 199000, buyComm = 995 → invested = 198005
        //   FV_switch = 198005·1.087 = 215231.435 → NetBenefit = −2168.565
        // Издержки = 1000 + 995 = 1995; −2168.565 = −1995·1.087 (издержки выросли бы на доходность hold).
        var result = SwitchAnalysisService.Compare(
            Hold(200_000m), Target(0.10m), horizonYears: 1m, sellCommissionRate: 0.005m, buyCommissionRate: 0.005m, asOf: Today);

        result.TotalSwitchCostRub.Should().Be(1_995m);
        result.NetBenefitRub.Should().BeApproximately(-2_168.565m, 0.001m);
        result.NetBenefitRub.Should().BeApproximately(-result.TotalSwitchCostRub * 1.087m, 0.001m);
        result.IsSwitchFavorable.Should().BeFalse();
        result.BreakEvenYears.Should().BeNull("доходности равны — издержки не окупаются никогда");
    }

    [Fact]
    public void Compare_BreakEven_IsAnalyticAndFinite()
    {
        // MV=100000, y_hold=10% (0.087), y_target=15% (0.1305), комиссии 0.3%/0.3%, без слиппеджа → invested = 99400.9.
        // BE = ln(100000/99400.9) / ln(1.1305/1.087) = 0.0060090 / 0.0392374 = 0.153141 года.
        var result = SwitchAnalysisService.Compare(
            Hold(), Target(0.15m), horizonYears: 3m, sellCommissionRate: 0.003m, buyCommissionRate: 0.003m, asOf: Today);

        result.BreakEvenYears.Should().NotBeNull();
        result.BreakEvenYears!.Value.Should().BeApproximately(0.153141m, 1e-5m);
        // Проверка определения: на горизонте = BE выгода ≈ 0.
        var atBreakEven = SwitchAnalysisService.Compare(
            Hold(), Target(0.15m), horizonYears: result.BreakEvenYears.Value, sellCommissionRate: 0.003m, buyCommissionRate: 0.003m, asOf: Today);
        atBreakEven.NetBenefitRub.Should().BeApproximately(0m, 0.01m);
        // Предусловие: на старом линейном приближении было бы 599.1/(99700·0.05) = 0.1202 — компаундирование и НДФЛ меняют срок.
    }

    [Fact]
    public void Compare_LowerYieldTarget_BreakEvenNull_AndNeverFavorable()
    {
        var result = SwitchAnalysisService.Compare(Hold(50_000m, 0.15m), Target(0.10m), horizonYears: 2m, asOf: Today);

        result.IsSwitchFavorable.Should().BeFalse();
        result.NetBenefitRub.Should().BeLessThan(0m);
        result.BreakEvenYears.Should().BeNull("отрицательный спред доходности никогда не окупается");
    }

    [Fact]
    public void Compare_TinySpread_BreakEvenBeyondThirtyYears_IsNull()
    {
        // Спред 0.02 п.п.: после НДФЛ 0.0174 п.п.; ln(1/0.9940009)/ln(1.087174/1.087) ≈ 37.4 года > 30 → null.
        var result = SwitchAnalysisService.Compare(Hold(), Target(0.1002m), horizonYears: 2m, asOf: Today);

        result.BreakEvenYears.Should().BeNull();
        result.IsSwitchFavorable.Should().BeFalse();
    }

    [Fact]
    public void Compare_SaleTaxKnown_ReducesProceedsAndBenefit()
    {
        // MV=100000, вложено 80000 (журнал полный), y_hold=10%, y_target=12% (at 0.1044), h=2, комиссии 0.3%/0.3%,
        // полуспреды 0.2% / 0.4%.
        //   sellComm=300, sellSlip=200 → выручка до налога 99500; прибыль 19500; НДФЛ = 2535
        //   proceeds = 96965; buyComm = 290.895; buySlip = 387.86; invested = 96286.245
        //   FV_hold = 118156.90; FV_switch = 96286.245·1.1044² = 117440.27 → NetBenefitAfterTax = −716.63
        //   Без налога продажи (поле NetBenefitRub): invested 98803.5·1.1044² = 120510.57 − 118156.90 = +2353.67
        var result = SwitchAnalysisService.Compare(
            Hold(halfSpread: 0.002m, invested: 80_000m, unknownLots: false), Target(0.12m, halfSpread: 0.004m),
            horizonYears: 2m, sellCommissionRate: 0.003m, buyCommissionRate: 0.003m, asOf: Today);

        result.SellTaxEstimateRub.Should().BeApproximately(2_535m, 0.001m);
        result.ProceedsRub.Should().BeApproximately(96_965m, 0.001m);
        result.CapitalRub.Should().BeApproximately(96_286.245m, 0.001m);
        result.SwitchFutureValueRub.Should().BeApproximately(117_440.27m, 0.05m);
        result.NetBenefitAfterTaxRub.Should().NotBeNull();
        result.NetBenefitAfterTaxRub!.Value.Should().BeApproximately(-716.63m, 0.05m);
        result.NetBenefitRub.Should().BeApproximately(2_353.67m, 0.05m, "до налога продажи замена выглядела бы выгодной");
        result.IsSwitchFavorable.Should().BeFalse("флаг выгоды учитывает налог продажи, если он оценён");
    }

    [Fact]
    public void Compare_SaleTaxUnknown_TaxExcluded_AndAfterTaxIsNull()
    {
        var result = SwitchAnalysisService.Compare(Hold(), Target(0.12m), horizonYears: 2m, asOf: Today);

        result.SellTaxEstimateRub.Should().BeNull("журнал операций неполон — налог не оценён, не «ноль»");
        result.NetBenefitAfterTaxRub.Should().BeNull();
        result.NetBenefitRub.Should().BeGreaterThan(0m);
    }

    [Fact]
    public void Compare_SaleAtLoss_HasZeroTax()
    {
        var result = SwitchAnalysisService.Compare(
            Hold(invested: 150_000m, unknownLots: false), Target(0.12m), horizonYears: 2m, asOf: Today);

        result.SellTaxEstimateRub.Should().Be(0m);
        result.NetBenefitAfterTaxRub.Should().Be(result.NetBenefitRub);
    }

    [Fact]
    public void Compare_InvertedBook_NegativeHalfSpread_IsTreatedAsZero()
    {
        var result = SwitchAnalysisService.Compare(
            Hold(halfSpread: -0.01m), Target(0.12m, halfSpread: -0.02m), horizonYears: 1m, asOf: Today);

        result.SellSlippageRub.Should().Be(0m);
        result.BuySlippageRub.Should().Be(0m);
        result.SlippageUnknown.Should().BeFalse("спред известен, просто неконсистентен");
    }

    [Fact]
    public void Compare_DurationDifferenceAboveThreshold_Warns()
    {
        var result = SwitchAnalysisService.Compare(Hold(duration: 1.2m), Target(duration: 3.0m), horizonYears: 2m, asOf: Today);

        result.DurationMismatch.Should().BeTrue();
        result.DurationMismatchWarning.Should().Be("Процентный риск изменится: дюрация 1.2 → 3.0 лет");
        result.HoldMacaulayDurationYears.Should().Be(1.2m);
        result.TargetMacaulayDurationYears.Should().Be(3.0m);
    }

    [Theory]
    [InlineData(1.0, 2.5)]   // ровно 1.5 — не больше порога
    [InlineData(4.0, 3.0)]
    public void Compare_DurationDifferenceWithinThreshold_NoWarning(double hold, double target)
    {
        var result = SwitchAnalysisService.Compare(
            Hold(duration: (decimal)hold), Target(duration: (decimal)target), horizonYears: 2m, asOf: Today);

        result.DurationMismatch.Should().BeFalse();
        result.DurationMismatchWarning.Should().BeNull();
    }

    [Fact]
    public void Compare_UnknownDuration_NoWarning()
    {
        var result = SwitchAnalysisService.Compare(Hold(duration: 1m), Target(duration: null), horizonYears: 2m, asOf: Today);

        result.DurationMismatch.Should().BeFalse();
    }

    [Fact]
    public void Compare_MissingYieldOnEitherSide_MarksResultIncomplete()
    {
        var result = SwitchAnalysisService.Compare(Hold(10_000m, y: null), Target(0.10m), horizonYears: 1m, asOf: Today);

        result.YieldDataIncomplete.Should().BeTrue();
        result.IsSwitchFavorable.Should().BeFalse("недостоверный результат не может считаться выгодным");
        result.SpreadFraction.Should().BeNull();
        result.GrossGainRub.Should().BeNull();
        result.AnnualizedBenefitFraction.Should().BeNull();
    }

    [Fact]
    public void Compare_BreakdownFields_AreConsistent()
    {
        // GrossGain = MV·((1+y_t_at)^h − (1+y_h_at)^h) = 100000·(1.09135² − 1.087²) = 100000·(1.1910448 − 1.181569) = 947.58
        var result = SwitchAnalysisService.Compare(Hold(), Target(0.105m), horizonYears: 2m, asOf: Today);

        result.SpreadFraction.Should().BeApproximately(0.005m, 1e-9m);
        result.GrossGainRub!.Value.Should().BeApproximately(947.58m, 0.01m);
    }

    [Fact]
    public void Compare_NegativeYield_IsNotReducedByCouponTax()
    {
        SwitchAnalysisService.AfterTax(-0.02m).Should().Be(-0.02m);
        SwitchAnalysisService.AfterTax(0.10m).Should().BeApproximately(0.087m, 1e-12m);
        SwitchAnalysisService.CouponTaxRate.Should().Be(0.13m);
    }

    [Fact]
    public void Compare_AlwaysIncludesDisclaimer_AboutScopeTaxAndCompounding()
    {
        var result = SwitchAnalysisService.Compare(Hold(1000m, 0.1m), Target(0.1m), horizonYears: 1m, asOf: Today);

        result.Disclaimer.Should().NotBeNullOrWhiteSpace();
        result.Disclaimer.Should().Contain("НДФЛ");
        result.Disclaimer.Should().Contain("сложным начислением");
        result.Disclaimer.Should().Contain("слиппедж");
        result.Disclaimer.Should().NotContain("линейно", "v2 — не линейная оценка");
    }

    [Fact]
    public void Compare_InvalidHorizon_Throws()
    {
        var act = () => SwitchAnalysisService.Compare(Hold(1000m, 0.1m), Target(0.1m), horizonYears: 0m);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
