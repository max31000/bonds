using Bonds.Core.Analytics;
using Bonds.Core.Models;
using Bonds.Core.Universe;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Analytics;

/// <summary>
/// Задача 46 часть A: эталонные числа скоркарты, посчитанные РУКАМИ (см. комментарии у профилей).
/// Даты — относительно «сегодня» (вход <see cref="ReliabilityScoreInput.Today"/>), абсолютных дат нет.
/// Максимумы: слой A = 12+15+5+3+10 = 45 (+10 manual_rating), слой B = 8+8+8+4+6+6 = 40, слой C = 8+6+6 = 20.
/// </summary>
public class ReliabilityScoreServiceTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);
    private static int LastYear => Today.Year - 1;

    // ─── Фабрики входа ────────────────────────────────────────────────────────────────────

    /// <summary>Слой C «спокойный»: спред и цена без движения 12 дней, ликвидность Good → 8+6+6 = 20/20.</summary>
    private static ReliabilityScoreInput CalmMarket(ReliabilityScoreInput i) => i with
    {
        GSpreadFraction = 0.0300m,
        GSpreadHistory = Enumerable.Repeat(0.0300m, 12).ToList(),
        PricePercent = 100m,
        PriceHistory = Enumerable.Repeat(100m, 12).ToList(),
        Liquidity = SignalLevel.Good,
        LiquidityScoreRaw = LiquidityScore.High,
        TurnoverMedianRub = 10_000_000m,
    };

    /// <summary>Слой A «чистый»: дефолтов нет (12), листинг 1 (15), оферты нет (5), не только для квалов (3),
    /// брокер Low (10) → 45/45.</summary>
    private static ReliabilityScoreInput CleanStructure(ReliabilityScoreInput i) => i with
    {
        IssuerHasDefault = false,
        IssuerHasTechnicalDefault = false,
        DefaultHistoryIsIssuerWide = true,
        ListLevel = 1,
        OfferDate = null,
        IsQualifiedInvestors = false,
        BrokerRiskLevel = BrokerRiskLevel.Low,
    };

    private static ReliabilityScoreInput Base() => new() { Today = Today, Sector = "Корпоративные" };

    private static ScoreComponent Component(ReliabilityScore s, string key) => s.Components.Single(c => c.Key == key);

    /// <summary>
    /// Здоровый корпорат: выручка 6 млрд, 2200=1.2 млрд, 2330=0.3 млрд, чистая 0.6 млрд, капитал 3 млрд, баланс 9 млрд,
    /// 1200=4, 1500=2, деньги 0.5, 1410=2, 1510=0.5, 1370=2 (млрд). Тренд: 6/0.6 против 4/0.3 два года назад.
    /// </summary>
    private static List<IssuerFinancials> HealthyFinancials(string source = IssuerFinancialsSources.GirBo) =>
    [
        new IssuerFinancials
        {
            Inn = "7700000001", Period = LastYear, Source = source,
            Revenue = 6e9m, OperatingProfit = 1.2e9m, InterestPaid = 0.3e9m, NetProfit = 0.6e9m, Equity = 3e9m, TotalAssets = 9e9m,
            CurrentAssets = 4e9m, CurrentLiabilities = 2e9m, Cash = 0.5e9m, LongDebt = 2e9m, ShortDebt = 0.5e9m, RetainedEarnings = 2e9m,
        },
        new IssuerFinancials { Inn = "7700000001", Period = LastYear - 1, Source = source, Revenue = 5e9m, NetProfit = 0.4e9m },
        new IssuerFinancials { Inn = "7700000001", Period = LastYear - 2, Source = source, Revenue = 4e9m, NetProfit = 0.3e9m },
    ];

    // ─── Профиль 1: здоровый корпорат, листинг 1 → Score 100, Coverage 1.0, Green ──────────

    [Fact]
    public void HealthyCorporate_L1_IsGreen_WithPerfectScoreAndFullCoverage()
    {
        var input = CalmMarket(CleanStructure(Base())) with
        {
            Financials = HealthyFinancials(),
            FinancialsStatus = IssuerFinancialsStatuses.Ok,
        };

        var s = ReliabilityScoreService.Evaluate(input);

        // A 45/45 + B 40/40 + C 20/20 = 105/105.
        s.Score.Should().Be(100m);
        s.Coverage.Should().Be(1.000m);
        s.Level.Should().Be(ReliabilityLight.Green);
        s.CriticalFlags.Should().BeEmpty();
        s.WarningFlags.Should().BeEmpty();
        s.Components.Should().HaveCount(14); // 5 (A, без manual_rating) + 6 (B) + 3 (C)
        s.Components.Where(c => c.Layer == "B").Sum(c => c.Points).Should().Be(40);
        s.Components.Should().OnlyContain(c => !c.Missing && c.Points == c.MaxPoints);
    }

    [Fact]
    public void AltmanZEm_HealthyProfile_MatchesHandCalculation()
    {
        // X1=(4-2)/9=0.2222, X2=2/9=0.2222, X3=1.2/9=0.1333, X4=3/(9-3)=0.5.
        // Z = 3.25 + 6.56*0.2222 + 3.26*0.2222 + 6.72*0.1333 + 1.05*0.5 = 3.25+1.4578+0.7244+0.896+0.525 = 6.8532.
        var z = ReliabilityScoreService.ComputeAltmanZEm(HealthyFinancials()[0]);

        z.Should().BeApproximately(6.8532m, 0.0005m);
    }

    [Fact]
    public void AltmanZEm_TotalLiabilitiesNotPositive_ReturnsNull()
    {
        var f = HealthyFinancials()[0];
        f.Equity = f.TotalAssets; // обязательств нет — X4 не определён

        ReliabilityScoreService.ComputeAltmanZEm(f).Should().BeNull();
    }

    // ─── Профиль 2: ВДО (покрытие 1.5, выручка 0.8 млрд) → Score 60, Yellow ────────────────

    [Fact]
    public void HighYieldIssuer_CoverageOnePointFive_RevenueUnderOneBillion_IsYellowWithScore60()
    {
        // A: дефолтов нет 12 + листинг 2 → 8 + оферты нет 5 + не квал 3 + брокер Moderate 5 = 33/45.
        // B (млн ₽): выручка 800 → 0; 2200/2330 = 150/100 = 1.5 → 2; (400+200-50)/300 = 1.83 → 5; 500/400 = 1.25 → 2;
        //    тренд 700→800 и чистая 10→20 → 6; Z: X1=0.1, X2=0.05, X3=0.15, X4=300/700=0.4286 →
        //    3.25+0.656+0.163+1.008+0.45 = 5.527 (серая зона 4.35–5.85) → 3. Итого B = 18/40.
        // C: спред +30 б.п. (0.0230 против медианы 0.0200) → 3; цена без просадки → 6; ликвидность Neutral → 3 = 12/20.
        // Score = (33+18+12)/105 = 60.0; Coverage = 1.
        var input = Base() with
        {
            IssuerHasDefault = false, IssuerHasTechnicalDefault = false, ListLevel = 2, IsQualifiedInvestors = false,
            BrokerRiskLevel = BrokerRiskLevel.Moderate,
            FinancialsStatus = IssuerFinancialsStatuses.Ok,
            Financials =
            [
                new IssuerFinancials
                {
                    Inn = "7700000002", Period = LastYear, Source = IssuerFinancialsSources.GirBo,
                    Revenue = 800e6m, OperatingProfit = 150e6m, InterestPaid = 100e6m, NetProfit = 20e6m, Equity = 300e6m, TotalAssets = 1000e6m,
                    CurrentAssets = 500e6m, CurrentLiabilities = 400e6m, Cash = 50e6m, LongDebt = 400e6m, ShortDebt = 200e6m, RetainedEarnings = 50e6m,
                },
                new IssuerFinancials { Inn = "7700000002", Period = LastYear - 1, Source = IssuerFinancialsSources.GirBo, Revenue = 700e6m, NetProfit = 10e6m },
            ],
            GSpreadFraction = 0.0230m, GSpreadHistory = Enumerable.Repeat(0.0200m, 10).ToList(),
            PricePercent = 98m, PriceHistory = Enumerable.Repeat(98m, 10).ToList(),
            Liquidity = SignalLevel.Neutral, LiquidityScoreRaw = LiquidityScore.Medium, TurnoverMedianRub = 1_000_000m,
        };

        var s = ReliabilityScoreService.Evaluate(input);

        Component(s, "listing").Points.Should().Be(8);
        Component(s, "broker_risk_level").Points.Should().Be(5);
        Component(s, "revenue_size").Points.Should().Be(0);
        Component(s, "interest_coverage").Points.Should().Be(2);
        Component(s, "interest_coverage").RawValue.Should().Be(1.5m);
        Component(s, "leverage").Points.Should().Be(5);
        Component(s, "current_ratio").Points.Should().Be(2);
        Component(s, "trend").Points.Should().Be(6);
        Component(s, "altman_z_em").Points.Should().Be(3);
        Component(s, "spread_momentum").Points.Should().Be(3);
        Component(s, "liquidity").Points.Should().Be(3);
        s.Score.Should().Be(60.0m);
        s.Coverage.Should().Be(1.000m);
        s.Level.Should().Be(ReliabilityLight.Yellow);
        s.CriticalFlags.Should().BeEmpty();
    }

    // ─── Профиль 3: техдефолт (4 балла + флаг) и дефолт (Red) ─────────────────────────────

    [Fact]
    public void TechnicalDefault_GivesFourPointsAndWarningFlag_NotCritical()
    {
        var input = CalmMarket(CleanStructure(Base())) with { IssuerHasTechnicalDefault = true };

        var s = ReliabilityScoreService.Evaluate(input);

        Component(s, "issuer_default_history").Points.Should().Be(4);
        s.WarningFlags.Should().Contain(ReliabilityFlags.IssuerTechDefault);
        s.CriticalFlags.Should().BeEmpty();
        // Без отчётности (слой B) зелёным не бывает, хотя балл (57/65 = 87.7) и покрытие (65/105 = 0.619) высокие.
        s.Score.Should().Be(87.7m);
        s.Coverage.Should().Be(0.619m);
        s.Level.Should().Be(ReliabilityLight.Yellow);
    }

    [Fact]
    public void IssuerDefault_IsCriticalAndRed_EvenWithEverythingElseHealthy()
    {
        var input = CalmMarket(CleanStructure(Base())) with
        {
            IssuerHasDefault = true,
            Financials = HealthyFinancials(),
            FinancialsStatus = IssuerFinancialsStatuses.Ok,
        };

        var s = ReliabilityScoreService.Evaluate(input);

        Component(s, "issuer_default_history").Points.Should().Be(0);
        s.CriticalFlags.Should().Contain(ReliabilityFlags.IssuerDefaulted);
        s.Level.Should().Be(ReliabilityLight.Red);
    }

    [Fact]
    public void DefaultFlagsUnknown_AreMissing_NotTwelvePoints()
    {
        var input = CalmMarket(CleanStructure(Base())) with { IssuerHasDefault = null, IssuerHasTechnicalDefault = null };

        var component = Component(ReliabilityScoreService.Evaluate(input), "issuer_default_history");

        component.Missing.Should().BeTrue();
        component.Points.Should().Be(0);
        component.RawDisplay.Should().Contain("нет данных");
    }

    // ─── Профиль 4: банк (кредитная организация) — слой B Missing, Yellow, Coverage < 0.6 ──

    [Theory]
    [InlineData(IssuerFinancialsStatuses.Cb)]
    [InlineData(IssuerFinancialsStatuses.NotFound)]
    [InlineData(IssuerFinancialsStatuses.Hidden)]
    [InlineData(IssuerFinancialsStatuses.Error)]
    [InlineData(null)]
    public void NoUsableFinancials_AnyStatusNotOk_LayerBIsMissing_ResultYellowWithLowCoverage(string? status)
    {
        // A без брокера: 12+15+5+3 = 35; C без истории: только ликвидность 6 → доступно 41, набрано 41 → Score 100.
        // totalMax = 45 + 40 + 20 = 105 → Coverage = 41/105 = 0.390.
        var input = CleanStructure(Base()) with
        {
            BrokerRiskLevel = null,
            Liquidity = SignalLevel.Good, LiquidityScoreRaw = LiquidityScore.High, TurnoverMedianRub = 10_000_000m,
            FinancialsStatus = status,
        };

        var s = ReliabilityScoreService.Evaluate(input);

        s.Components.Where(c => c.Layer == "B").Should().HaveCount(6).And.OnlyContain(c => c.Missing && c.Points == 0);
        s.Score.Should().Be(100m);
        s.Coverage.Should().Be(0.390m);
        s.Coverage.Should().BeLessThan(0.6m);
        s.Level.Should().Be(ReliabilityLight.Yellow);
    }

    [Fact]
    public void BankStatusCb_ExplainsReasonInRawDisplay()
    {
        var s = ReliabilityScoreService.Evaluate(Base() with { FinancialsStatus = IssuerFinancialsStatuses.Cb });

        Component(s, "revenue_size").RawDisplay.Should().Contain("кредитная организация");
    }

    [Fact]
    public void FullBankWithoutFundamentals_StaysYellow_EvenWhenCoverageReachesPointSixTwo()
    {
        // A 45 + C 20 = 65 из 105 → Coverage 0.619 ≥ 0.6 и Score 100, но слоя B нет → не Green.
        var input = CalmMarket(CleanStructure(Base())) with { FinancialsStatus = IssuerFinancialsStatuses.Cb };

        var s = ReliabilityScoreService.Evaluate(input);

        s.Coverage.Should().BeGreaterOrEqualTo(0.6m);
        s.Level.Should().Be(ReliabilityLight.Yellow);
        s.Reason.Should().Contain("нет отчётности");
    }

    // ─── Профиль 5: ОФЗ — суверенный бонус вместо слоя B, Green ───────────────────────────

    [Fact]
    public void Ofz_SovereignBonusReplacesLayerB_IsGreenWithFullCoverage()
    {
        // A 45 + sovereign 15 + C 20 = 80/80 (компонентов слоя B нет вообще).
        var input = CalmMarket(CleanStructure(Base())) with { Sector = CandidateRiskSignalService.GovernmentSectorLabel };

        var s = ReliabilityScoreService.Evaluate(input);

        s.Components.Should().NotContain(c => c.Key == "revenue_size");
        Component(s, "sovereign").Points.Should().Be(15);
        s.Score.Should().Be(100m);
        s.Coverage.Should().Be(1.000m);
        s.Level.Should().Be(ReliabilityLight.Green);
    }

    [Fact]
    public void Municipal_GetsTenSovereignPoints()
    {
        var s = ReliabilityScoreService.Evaluate(CalmMarket(CleanStructure(Base())) with { Sector = CandidateRiskSignalService.MunicipalSectorLabel });

        Component(s, "sovereign").Points.Should().Be(10);
        Component(s, "sovereign").MaxPoints.Should().Be(10);
    }

    [Fact]
    public void OfzWithoutCard_StillGreen_ButOfzWithOnlyBankFieldsIsYellowByCoverage()
    {
        // Без карточки: default и not_qual Missing; брокер Low 10 → доступно 15+5+10+15+20 = 65 из 80 → 0.8125 → Green.
        var noCard = CalmMarket(CleanStructure(Base())) with
        {
            Sector = CandidateRiskSignalService.GovernmentSectorLabel,
            IssuerHasDefault = null, IssuerHasTechnicalDefault = null, IsQualifiedInvestors = null,
        };
        ReliabilityScoreService.Evaluate(noCard).Level.Should().Be(ReliabilityLight.Green);

        // Только поля банка: листинг 15 + оферта 5 + sovereign 15 + ликвидность 6 = 41 из 80 → 0.5125 < 0.6 → Yellow.
        var bankOnly = Base() with
        {
            Sector = CandidateRiskSignalService.GovernmentSectorLabel, ListLevel = 1,
            Liquidity = SignalLevel.Good, LiquidityScoreRaw = LiquidityScore.High, TurnoverMedianRub = 10_000_000m,
        };
        var s = ReliabilityScoreService.Evaluate(bankOnly);
        s.Coverage.Should().Be(0.513m);
        s.Level.Should().Be(ReliabilityLight.Yellow);
    }

    // ─── Профиль 6: спред-шок ─────────────────────────────────────────────────────────────

    [Fact]
    public void SpreadShock_IsCriticalAndRed_AndHealthyCheapBondIsNot()
    {
        // История 0.0230, сегодня 0.0300: сдвиг +70 б.п. > 50 б.п. → 0 баллов + SpreadShock → Red.
        var shocked = CalmMarket(CleanStructure(Base())) with
        {
            Financials = HealthyFinancials(), FinancialsStatus = IssuerFinancialsStatuses.Ok,
            GSpreadFraction = 0.0300m, GSpreadHistory = Enumerable.Repeat(0.0230m, 12).ToList(),
        };
        var s = ReliabilityScoreService.Evaluate(shocked);
        Component(s, "spread_momentum").Points.Should().Be(0);
        s.CriticalFlags.Should().Contain(ReliabilityFlags.SpreadShock);
        s.Level.Should().Be(ReliabilityLight.Red);

        // «Дешёвая» здоровая бумага: УРОВЕНЬ спреда высокий, но он стабилен (сдвиг 0) → риск-скор не краснит (ось Value отдельно).
        var cheap = shocked with { GSpreadFraction = 0.0900m, GSpreadHistory = Enumerable.Repeat(0.0900m, 12).ToList() };
        ReliabilityScoreService.Evaluate(cheap).Level.Should().Be(ReliabilityLight.Green);
    }

    [Theory]
    [InlineData(0.0220, 8, false)]  // +20 б.п. ровно — ещё «спокойно»
    [InlineData(0.0221, 3, false)]  // чуть больше 20 б.п.
    [InlineData(0.0250, 3, false)]  // +50 б.п. ровно — предупреждение, не шок
    [InlineData(0.0251, 0, true)]   // > +50 б.п. — шок
    public void SpreadMomentum_Boundaries(double todaySpread, int expectedPoints, bool shock)
    {
        var input = CalmMarket(Base()) with { GSpreadFraction = (decimal)todaySpread, GSpreadHistory = Enumerable.Repeat(0.0200m, 15).ToList() };

        var s = ReliabilityScoreService.Evaluate(input);

        Component(s, "spread_momentum").Points.Should().Be(expectedPoints);
        s.CriticalFlags.Contains(ReliabilityFlags.SpreadShock).Should().Be(shock);
    }

    [Fact]
    public void SpreadMomentum_FewerThanTenDays_IsMissing()
    {
        var input = CalmMarket(Base()) with { GSpreadHistory = Enumerable.Repeat(0.03m, 9).ToList() };

        Component(ReliabilityScoreService.Evaluate(input), "spread_momentum").Missing.Should().BeTrue();
    }

    [Theory]
    [InlineData(99.0, 6, false)]   // 1% от максимума 100
    [InlineData(97.0, 3, false)]   // 3%
    [InlineData(95.0, 3, false)]   // ровно 5%
    [InlineData(94.0, 0, true)]    // 6%
    public void PriceDrawdown_Boundaries(double price, int expectedPoints, bool warning)
    {
        var input = CalmMarket(Base()) with { PricePercent = (decimal)price, PriceHistory = Enumerable.Repeat(100m, 15).ToList() };

        var s = ReliabilityScoreService.Evaluate(input);

        Component(s, "price_drawdown").Points.Should().Be(expectedPoints);
        s.WarningFlags.Contains(ReliabilityFlags.PriceDrawdown).Should().Be(warning);
        s.CriticalFlags.Should().NotContain(ReliabilityFlags.PriceDrawdown);
    }

    // ─── Профиль 7: скрытая отчётность + ручной ввод ──────────────────────────────────────

    [Fact]
    public void HiddenFinancials_ManualRowsWin_AutomaticRowsAreIgnored()
    {
        var hiddenWithManual = CalmMarket(CleanStructure(Base())) with
        {
            FinancialsStatus = IssuerFinancialsStatuses.Hidden,
            Financials = HealthyFinancials(IssuerFinancialsSources.Manual),
        };
        var withManual = ReliabilityScoreService.Evaluate(hiddenWithManual);
        withManual.Components.Where(c => c.Layer == "B").Sum(c => c.Points).Should().Be(40);
        Component(withManual, "revenue_size").Source.Should().Contain("Ручной ввод");
        withManual.Level.Should().Be(ReliabilityLight.Green);

        // Те же цифры, но источник girbo при статусе hidden — недоступны (статус ≠ ok) → слой B Missing.
        var hiddenAutoOnly = hiddenWithManual with { Financials = HealthyFinancials(IssuerFinancialsSources.GirBo) };
        var auto = ReliabilityScoreService.Evaluate(hiddenAutoOnly);
        auto.Components.Where(c => c.Layer == "B").Should().OnlyContain(c => c.Missing);
        Component(auto, "revenue_size").RawDisplay.Should().Contain("скрыл отчётность");
    }

    [Fact]
    public void ManualRating_AaaScoresTenAndGrowsLayerA_StaleRatingIsMissing()
    {
        var baseInput = CalmMarket(CleanStructure(Base())) with { Financials = HealthyFinancials(), FinancialsStatus = IssuerFinancialsStatuses.Ok };

        var fresh = ReliabilityScoreService.Evaluate(baseInput with
        {
            Ratings = [new IssuerRatingInput("АКРА", "AAA(RU)", Today.AddMonths(-3))],
        });
        Component(fresh, "manual_rating").Points.Should().Be(10);
        fresh.Components.Sum(c => c.MaxPoints).Should().Be(115, "максимум слоя A вырос на 10");
        fresh.Score.Should().Be(100m);

        var expired = ReliabilityScoreService.Evaluate(baseInput with
        {
            Ratings = [new IssuerRatingInput("АКРА", "AAA(RU)", Today.AddMonths(-19))],
        });
        Component(expired, "manual_rating").Missing.Should().BeTrue();
        Component(expired, "manual_rating").RawDisplay.Should().Contain("протух");
        expired.Coverage.Should().Be(0.913m); // 105 / 115

        ReliabilityScoreService.Evaluate(baseInput).Components.Should().NotContain(c => c.Key == "manual_rating");
    }

    [Theory]
    [InlineData("AAA", 10, false)]
    [InlineData("AA+", 10, false)]
    [InlineData("ruAA-", 10, false)]
    [InlineData("A(RU)", 8, false)]
    [InlineData("A++(RU)", 8, false)]
    [InlineData("BBB-", 6, false)]
    [InlineData("BB+", 4, false)]
    [InlineData("B", 2, false)]
    [InlineData("CCC", 0, true)]
    [InlineData("D", 0, true)]
    [InlineData("отозван", 0, true)]
    public void ManualRating_Scale(string rating, int expectedPoints, bool junk)
    {
        var s = ReliabilityScoreService.Evaluate(Base() with { Ratings = [new IssuerRatingInput("Эксперт РА", rating, Today.AddMonths(-1))] });

        Component(s, "manual_rating").Points.Should().Be(expectedPoints);
        s.WarningFlags.Contains(ReliabilityFlags.JunkRating).Should().Be(junk);
        s.CriticalFlags.Should().NotContain(ReliabilityFlags.JunkRating);
    }

    [Fact]
    public void ManualRating_SeveralAgencies_TakesTheLowestFreshOne()
    {
        var s = ReliabilityScoreService.Evaluate(Base() with
        {
            Ratings =
            [
                new IssuerRatingInput("АКРА", "A(RU)", Today.AddMonths(-2)),
                new IssuerRatingInput("Эксперт РА", "ruBBB", Today.AddMonths(-2)),
                new IssuerRatingInput("НКР", "AAA.ru", Today.AddMonths(-30)), // протух — не участвует
            ],
        });

        Component(s, "manual_rating").Points.Should().Be(6);
    }

    // ─── Stale ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void StaleFinancials_PointsCounted_ButLayerBCoverageHalved()
    {
        // Отчётность за (год-3) всегда старше 20 месяцев. Баллы считаются как обычно, вклад слоя B в Coverage ×0.5:
        // (45 + 20 + 40*0.5) / 105 = 85/105 = 0.810.
        var stale = HealthyFinancials();
        foreach (var f in stale) f.Period -= 2;
        var input = CalmMarket(CleanStructure(Base())) with { Financials = stale, FinancialsStatus = IssuerFinancialsStatuses.Ok };

        var s = ReliabilityScoreService.Evaluate(input);

        s.Components.Where(c => c.Layer == "B").Should().OnlyContain(c => c.Stale && c.RawDisplay!.Contains("Stale"));
        s.Components.Where(c => c.Layer == "B").Sum(c => c.Points).Should().Be(40);
        s.Score.Should().Be(100m);
        s.Coverage.Should().Be(0.810m);
        s.Level.Should().Be(ReliabilityLight.Green);
    }

    [Fact]
    public void FreshFinancials_AreNotStale_PreviousYearIsFine()
    {
        var s = ReliabilityScoreService.Evaluate(CalmMarket(CleanStructure(Base())) with { Financials = HealthyFinancials(), FinancialsStatus = IssuerFinancialsStatuses.Ok });

        s.Components.Where(c => c.Layer == "B").Should().OnlyContain(c => !c.Stale);
    }

    // ─── Критические флаги слоя B ─────────────────────────────────────────────────────────

    [Fact]
    public void InterestUncovered_BelowOne_IsCriticalAndRed()
    {
        var f = HealthyFinancials();
        f[0].OperatingProfit = 0.1e9m; // 0.1 / 0.3 = 0.33
        var s = ReliabilityScoreService.Evaluate(CalmMarket(CleanStructure(Base())) with { Financials = f, FinancialsStatus = IssuerFinancialsStatuses.Ok });

        Component(s, "interest_coverage").Points.Should().Be(0);
        s.CriticalFlags.Should().Contain(ReliabilityFlags.InterestUncovered);
        s.Level.Should().Be(ReliabilityLight.Red);
    }

    [Fact]
    public void NegativeEquity_IsCriticalAndRed()
    {
        var f = HealthyFinancials();
        f[0].Equity = -1e8m;
        var s = ReliabilityScoreService.Evaluate(CalmMarket(CleanStructure(Base())) with { Financials = f, FinancialsStatus = IssuerFinancialsStatuses.Ok });

        Component(s, "leverage").Points.Should().Be(0);
        s.CriticalFlags.Should().Contain(ReliabilityFlags.NegativeEquity);
        s.Level.Should().Be(ReliabilityLight.Red);
    }

    [Fact]
    public void InterestCoverage_NoInterestWithDebt_IsMissing_NoInterestNoDebt_IsFull()
    {
        var withDebt = HealthyFinancials();
        withDebt[0].InterestPaid = null;
        Component(ReliabilityScoreService.Evaluate(Base() with { Financials = withDebt, FinancialsStatus = IssuerFinancialsStatuses.Ok }), "interest_coverage")
            .Missing.Should().BeTrue();

        var noDebt = HealthyFinancials();
        noDebt[0].InterestPaid = 0m;
        noDebt[0].LongDebt = 0m;
        noDebt[0].ShortDebt = 0m;
        var c = Component(ReliabilityScoreService.Evaluate(Base() with { Financials = noDebt, FinancialsStatus = IssuerFinancialsStatuses.Ok }), "interest_coverage");
        c.Missing.Should().BeFalse();
        c.Points.Should().Be(8);
    }

    [Theory]
    [InlineData(2.9, 5)]
    [InlineData(3.0, 8)]
    [InlineData(1.9, 2)]
    [InlineData(1.0, 2)]
    [InlineData(0.99, 0)]
    public void InterestCoverage_Boundaries(double ratio, int expectedPoints)
    {
        var f = HealthyFinancials();
        f[0].InterestPaid = 100m;
        f[0].OperatingProfit = (decimal)ratio * 100m;

        var c = Component(ReliabilityScoreService.Evaluate(Base() with { Financials = f, FinancialsStatus = IssuerFinancialsStatuses.Ok }), "interest_coverage");

        c.Points.Should().Be(expectedPoints);
    }

    [Theory]
    [InlineData(0.5, 8)]   // чистый долг / капитал
    [InlineData(1.0, 5)]
    [InlineData(1.99, 5)]
    [InlineData(2.0, 2)]
    [InlineData(3.99, 2)]
    [InlineData(4.0, 0)]
    public void Leverage_Boundaries(double netDebtToEquity, int expectedPoints)
    {
        var f = HealthyFinancials();
        f[0].Equity = 100m;
        f[0].Cash = 0m;
        f[0].LongDebt = (decimal)netDebtToEquity * 100m;
        f[0].ShortDebt = 0m;

        Component(ReliabilityScoreService.Evaluate(Base() with { Financials = f, FinancialsStatus = IssuerFinancialsStatuses.Ok }), "leverage")
            .Points.Should().Be(expectedPoints);
    }

    [Theory]
    [InlineData(1.5, 4)]
    [InlineData(1.49, 2)]
    [InlineData(1.0, 2)]
    [InlineData(0.99, 0)]
    public void CurrentRatio_Boundaries(double ratio, int expectedPoints)
    {
        var f = HealthyFinancials();
        f[0].CurrentLiabilities = 100m;
        f[0].CurrentAssets = (decimal)ratio * 100m;

        Component(ReliabilityScoreService.Evaluate(Base() with { Financials = f, FinancialsStatus = IssuerFinancialsStatuses.Ok }), "current_ratio")
            .Points.Should().Be(expectedPoints);
    }

    [Theory]
    [InlineData(4_000_000_000, 8)]
    [InlineData(3_999_999_999, 4)]
    [InlineData(1_000_000_000, 4)]
    [InlineData(999_999_999, 0)]
    public void RevenueSize_Boundaries(long revenue, int expectedPoints)
    {
        var f = HealthyFinancials();
        f[0].Revenue = revenue;

        Component(ReliabilityScoreService.Evaluate(Base() with { Financials = f, FinancialsStatus = IssuerFinancialsStatuses.Ok }), "revenue_size")
            .Points.Should().Be(expectedPoints);
    }

    [Fact]
    public void Trend_MixedBothFallingAndLossYear_AndTooFewYears()
    {
        IssuerFinancials Year(int p, decimal rev, decimal net) => new() { Inn = "1", Period = p, Source = IssuerFinancialsSources.GirBo, Revenue = rev, NetProfit = net };
        ScoreComponent Trend(params IssuerFinancials[] years) => Component(
            ReliabilityScoreService.Evaluate(Base() with { Financials = years, FinancialsStatus = IssuerFinancialsStatuses.Ok }), "trend");

        Trend(Year(LastYear, 300, 30), Year(LastYear - 1, 200, 20), Year(LastYear - 2, 100, 10)).Points.Should().Be(6);
        Trend(Year(LastYear, 300, 5), Year(LastYear - 1, 200, 20)).Points.Should().Be(3, "выручка растёт, прибыль падает — смешанно");
        Trend(Year(LastYear, 100, 5), Year(LastYear - 1, 200, 20), Year(LastYear - 2, 300, 30)).Points.Should().Be(0, "обе падают");
        Trend(Year(LastYear, 300, -5), Year(LastYear - 1, 200, 20)).Points.Should().Be(0, "убыток в последнем году");
        Trend(Year(LastYear, 300, 30)).Missing.Should().BeTrue("меньше 2 лет");
    }

    [Fact]
    public void AltmanBoundaries_AreShiftedByTheEmConstant()
    {
        // Подбираем Z-ровно на порогах через X3: фиксируем остальное нулевым. TA=100, Equity=50 → X4 = 1 → +1.05.
        // Z = 3.25 + 1.05 + 6.72 * OP/100 (CA=CL, RE=0).
        IssuerFinancials WithOp(decimal op) => new()
        {
            Inn = "1", Period = LastYear, Source = IssuerFinancialsSources.GirBo,
            TotalAssets = 100m, Equity = 50m, CurrentAssets = 10m, CurrentLiabilities = 10m, RetainedEarnings = 0m, OperatingProfit = op,
        };

        // Z = 4.30 + 0.0672*OP. OP=10 → 4.972 (серая зона), OP=23.2 → 5.859 (≥ 5.85).
        ReliabilityScoreService.ComputeAltmanZEm(WithOp(0m)).Should().Be(4.30m);
        ReliabilityScoreService.ComputeAltmanZEm(WithOp(23.2m)).Should().BeApproximately(5.859m, 0.001m);

        Component(ReliabilityScoreService.Evaluate(Base() with { Financials = [WithOp(0m)], FinancialsStatus = "ok" }), "altman_z_em")
            .Points.Should().Be(0, "Z=4.30 < 4.35 — зона дистресса");
        Component(ReliabilityScoreService.Evaluate(Base() with { Financials = [WithOp(10m)], FinancialsStatus = "ok" }), "altman_z_em")
            .Points.Should().Be(3, "Z=4.97 — серая зона");
        Component(ReliabilityScoreService.Evaluate(Base() with { Financials = [WithOp(23.2m)], FinancialsStatus = "ok" }), "altman_z_em")
            .Points.Should().Be(6, "Z=5.859 ≥ 5.85");
    }

    // ─── Светофор: Red-правила ────────────────────────────────────────────────────────────

    [Fact]
    public void DeadLiquidity_CautionWithLowTurnover_IsRed_ButCautionWithBigTurnoverIsNot()
    {
        var healthy = CalmMarket(CleanStructure(Base())) with { Financials = HealthyFinancials(), FinancialsStatus = IssuerFinancialsStatuses.Ok };

        var dead = ReliabilityScoreService.Evaluate(healthy with { Liquidity = SignalLevel.Caution, LiquidityScoreRaw = LiquidityScore.Low, TurnoverMedianRub = 100_000m });
        dead.Level.Should().Be(ReliabilityLight.Red);
        dead.Reason.Should().Contain("300 тыс.");

        var caution = ReliabilityScoreService.Evaluate(healthy with { Liquidity = SignalLevel.Caution, LiquidityScoreRaw = LiquidityScore.Medium, TurnoverMedianRub = 2_000_000m });
        caution.Level.Should().NotBe(ReliabilityLight.Red, "Caution по листингу 3 при живом обороте — не «мёртвая» ликвидность");
    }

    [Fact]
    public void LowScore_BelowThirtyFive_IsRed_ButNoData_IsNotRed()
    {
        // Листинг 3 (0/15), оферта есть (0/5), квал-only (0/3): доступно 23, набрано 0 → Score 0 → Red.
        var bad = Base() with { ListLevel = 3, OfferDate = Today.AddMonths(6), IsQualifiedInvestors = true };
        ReliabilityScoreService.Evaluate(bad).Level.Should().Be(ReliabilityLight.Red);

        // Пустой вход: «нет данных» не превращается в ноль молча — считаются только listing (вне списков, 0/15) и no_offer (5/5).
        var empty = ReliabilityScoreService.Evaluate(Base());
        empty.Components.Where(c => !c.Missing).Select(c => c.Key).Should().BeEquivalentTo("listing", "no_offer");
    }

    [Fact]
    public void ListingThree_BlocksGreen_EvenWithPerfectScore()
    {
        var input = CalmMarket(CleanStructure(Base())) with
        {
            ListLevel = 3, Financials = HealthyFinancials(), FinancialsStatus = IssuerFinancialsStatuses.Ok,
        };

        var s = ReliabilityScoreService.Evaluate(input);

        s.Score.Should().BeGreaterThanOrEqualTo(65m);
        s.Level.Should().Be(ReliabilityLight.Yellow);
        s.Reason.Should().Contain("листинг 3");
    }

    [Fact]
    public void Offer_FutureOfferZeroPoints_PastOfferIgnored()
    {
        var future = ReliabilityScoreService.Evaluate(Base() with { OfferDate = Today.AddMonths(3) });
        Component(future, "no_offer").Points.Should().Be(0);

        var past = ReliabilityScoreService.Evaluate(Base() with { OfferDate = Today.AddDays(-5) });
        Component(past, "no_offer").Points.Should().Be(5);
    }

    [Fact]
    public void Liquidity_NoneScore_IsMissing_AndLevelsMapToPoints()
    {
        ScoreComponent Liq(SignalLevel? level, LiquidityScore? raw) =>
            Component(ReliabilityScoreService.Evaluate(Base() with { Liquidity = level, LiquidityScoreRaw = raw, TurnoverMedianRub = 1m }), "liquidity");

        Liq(SignalLevel.Neutral, LiquidityScore.None).Missing.Should().BeTrue();
        Liq(null, null).Missing.Should().BeTrue();
        Liq(SignalLevel.Good, LiquidityScore.High).Points.Should().Be(6);
        Liq(SignalLevel.Neutral, LiquidityScore.Medium).Points.Should().Be(3);
        Liq(SignalLevel.Caution, LiquidityScore.Low).Points.Should().Be(0);
    }

    [Fact]
    public void BrokerRisk_UnspecifiedOrNull_IsMissing()
    {
        Component(ReliabilityScoreService.Evaluate(Base() with { BrokerRiskLevel = BrokerRiskLevel.Unspecified }), "broker_risk_level").Missing.Should().BeTrue();
        Component(ReliabilityScoreService.Evaluate(Base() with { BrokerRiskLevel = BrokerRiskLevel.High }), "broker_risk_level").Points.Should().Be(0);
    }

    [Theory]
    [InlineData("AAA", "AAA")]
    [InlineData("aa+", "AA")]
    [InlineData("ruA-", "A")]
    [InlineData("BBB(RU)", "BBB")]
    [InlineData("Отозван", "WD")]
    public void TryNormalizeRating_Accepts(string input, string expected)
    {
        ReliabilityScoreService.TryNormalizeRating(input, out var normalized).Should().BeTrue();
        normalized.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Z")]
    [InlineData("AAAA")]
    [InlineData("отлично")]
    public void TryNormalizeRating_Rejects(string input) =>
        ReliabilityScoreService.TryNormalizeRating(input, out _).Should().BeFalse();
}
