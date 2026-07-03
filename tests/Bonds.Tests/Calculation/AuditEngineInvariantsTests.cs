using Bonds.Core.Calculation;
using Bonds.Core.Models;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Calculation;

/// <summary>
/// Audit(engine): математические инварианты движка расчётов, проверенные независимым
/// пересчётом и сверкой с публичным MOEX ISS (см. CALC_AUDIT_ENGINE.md в корне репозитория).
/// Эти тесты не дублируют существующие юнит-тесты калькуляторов — они фиксируют
/// кросс-калькуляторные свойства (пар-бумага/купон, нулевой купон/срок, монотонность цена↔YTM,
/// PVBP↔дюрация, XIRR на простом сценарии) и граничные случаи солверов/НКД, которые не были
/// явно покрыты. Референсные числа получены независимым Python-пересчётом в scratchpad
/// (вне репозитория), не скопированы из реализации.
/// </summary>
public class AuditEngineInvariantsTests
{
    private const ulong InstrumentId = 900;
    private static readonly DateOnly AsOf = new(2025, 1, 1);

    // ─── Инвариант: бумага по номиналу с ежегодным купоном → YTM ≈ ставке купона ──────────────

    [Fact]
    public void Audit_ParBondAnnualCoupon_YtmApproximatelyEqualsCouponRate()
    {
        // Номинал 1000, годовой купон 8% (80 руб/год), цена ровно по номиналу (dirty=clean=1000,
        // НКД=0 — расчёт на дату начала периода), горизонт 5 лет. Классический инвариант
        // облигационной математики: при цене = номиналу YTM = купонная ставка (с точностью до
        // расхождения календарных лет 365 vs 365.25 при Act/365 day-count — отсюда допуск 5бп,
        // не 1e-4).
        const decimal faceValue = 1000m;
        const decimal couponRate = 0.08m;
        const decimal couponAmount = faceValue * couponRate; // 80
        var maturity = AsOf.AddYears(5);

        var coupons = new List<CouponSchedule>();
        for (var y = 1; y <= 5; y++)
        {
            coupons.Add(TestModelFactory.Coupon(InstrumentId, AsOf.AddYears(y), couponAmount, periodDays: 365));
        }

        var cashFlow = BondCashFlowBuilder.Build(faceValue, AsOf, maturity, coupons, amortizations: null);
        var ytm = YtmCalculator.Calculate(faceValue, AsOf, cashFlow);

        ytm.Should().NotBeNull();
        ytm!.Value.EffectiveYield.Should().BeApproximately(couponRate, 5e-4m,
            "при цене ровно по номиналу YTM должен приблизительно совпадать со ставкой купона");
    }

    // ─── Инвариант: дюрация Маколея зеро-купона = срок до погашения ────────────────────────────

    [Fact]
    public void Audit_ZeroCouponBond_MacaulayDurationEqualsYearsToMaturity()
    {
        var maturity = AsOf.AddYears(5);
        const decimal faceValue = 1000m;
        const decimal price = 620m; // произвольная дисконтная цена, не влияет на инвариант

        // Бескупонный поток: пустой график купонов, единственный платёж — погашение номинала.
        var cashFlow = BondCashFlowBuilder.Build(faceValue, AsOf, maturity, Array.Empty<CouponSchedule>(), amortizations: null);
        cashFlow.Should().HaveCount(1, "у зеро-купона единственный будущий поток — погашение");

        var ytm = YtmCalculator.Calculate(price, AsOf, cashFlow);
        ytm.Should().NotBeNull();

        var duration = DurationCalculator.Calculate(price, ytm!.Value.EffectiveYield, AsOf, cashFlow, couponsPerYear: 1);
        duration.Should().NotBeNull();

        var expectedYears = (maturity.DayNumber - AsOf.DayNumber) / 365m;
        duration!.Value.MacaulayDurationYears.Should().BeApproximately(expectedYears, 1e-6m,
            "для единственного денежного потока средневзвешенный срок вырождается в его собственный срок");
    }

    // ─── Инвариант: дюрация Маколея купонной бумаги строго меньше срока до погашения ───────────

    [Fact]
    public void Audit_CouponBearingBond_MacaulayDurationIsLessThanYearsToMaturity()
    {
        var maturity = AsOf.AddYears(10);
        const decimal faceValue = 1000m;
        var coupons = new List<CouponSchedule>();
        for (var y = 1; y <= 10; y++)
        {
            coupons.Add(TestModelFactory.Coupon(InstrumentId, AsOf.AddYears(y), 70m, periodDays: 365));
        }

        var cashFlow = BondCashFlowBuilder.Build(faceValue, AsOf, maturity, coupons, amortizations: null);
        var ytm = YtmCalculator.Calculate(950m, AsOf, cashFlow);
        ytm.Should().NotBeNull();

        var duration = DurationCalculator.Calculate(950m, ytm!.Value.EffectiveYield, AsOf, cashFlow, couponsPerYear: 1);
        duration.Should().NotBeNull();

        var yearsToMaturity = (maturity.DayNumber - AsOf.DayNumber) / 365m;
        duration!.Value.MacaulayDurationYears.Should().BeLessThan(yearsToMaturity,
            "промежуточные купонные выплаты сокращают средневзвешенный срок относительно чистого срока до погашения");
    }

    // ─── Инвариант: модифицированная дюрация = Маколей / (1 + y/k) ─────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(12)]
    public void Audit_ModifiedDuration_EqualsMacaulayOverOnePlusYieldOverFrequency(int couponsPerYear)
    {
        var maturity = AsOf.AddYears(3);
        const decimal faceValue = 1000m;
        var coupons = new List<CouponSchedule>
        {
            TestModelFactory.Coupon(InstrumentId, AsOf.AddYears(1), 60m, periodDays: 365),
            TestModelFactory.Coupon(InstrumentId, AsOf.AddYears(2), 60m, periodDays: 365),
            TestModelFactory.Coupon(InstrumentId, maturity, 60m, periodDays: 365),
        };
        var cashFlow = BondCashFlowBuilder.Build(faceValue, AsOf, maturity, coupons, amortizations: null);
        const decimal ytm = 0.09m;

        var duration = DurationCalculator.Calculate(950m, ytm, AsOf, cashFlow, couponsPerYear);
        duration.Should().NotBeNull();

        var expectedModified = duration!.Value.MacaulayDurationYears / (1m + ytm / couponsPerYear);
        duration.Value.ModifiedDuration.Should().BeApproximately(expectedModified, 1e-9m);
    }

    // ─── Инвариант: рост цены → падение YTM (монотонность) ─────────────────────────────────────

    [Fact]
    public void Audit_HigherDirtyPrice_ProducesLowerYtm_Monotonic()
    {
        var maturity = AsOf.AddYears(5);
        const decimal faceValue = 1000m;
        var coupons = new List<CouponSchedule>();
        for (var y = 1; y <= 5; y++)
        {
            coupons.Add(TestModelFactory.Coupon(InstrumentId, AsOf.AddYears(y), 75m, periodDays: 365));
        }
        var cashFlow = BondCashFlowBuilder.Build(faceValue, AsOf, maturity, coupons, amortizations: null);

        var prices = new[] { 850m, 900m, 950m, 1000m, 1050m, 1100m };
        var yields = prices
            .Select(p => YtmCalculator.Calculate(p, AsOf, cashFlow))
            .Select(r => { r.Should().NotBeNull(); return r!.Value.EffectiveYield; })
            .ToList();

        for (var i = 1; i < yields.Count; i++)
        {
            yields[i].Should().BeLessThan(yields[i - 1],
                $"цена {prices[i]} выше цены {prices[i - 1]} => YTM должен быть ниже (обратная зависимость цена/доходность)");
        }
    }

    // ─── Инвариант: PVBP согласован с модифицированной дюрацией и грязной ценой ────────────────

    [Fact]
    public void Audit_Pvbp_IsConsistentWithModifiedDurationAndDirtyPrice()
    {
        var maturity = AsOf.AddYears(4);
        const decimal faceValue = 1000m;
        var coupons = new List<CouponSchedule>();
        for (var y = 1; y <= 4; y++)
        {
            coupons.Add(TestModelFactory.Coupon(InstrumentId, AsOf.AddYears(y), 65m, periodDays: 365));
        }
        var cashFlow = BondCashFlowBuilder.Build(faceValue, AsOf, maturity, coupons, amortizations: null);
        const decimal dirtyPrice = 970m;

        var ytm = YtmCalculator.Calculate(dirtyPrice, AsOf, cashFlow);
        ytm.Should().NotBeNull();

        var duration = DurationCalculator.Calculate(dirtyPrice, ytm!.Value.EffectiveYield, AsOf, cashFlow, couponsPerYear: 1);
        duration.Should().NotBeNull();

        var expectedPvbp = duration!.Value.ModifiedDuration * dirtyPrice * 0.0001m;
        duration.Value.Pvbp.Should().BeApproximately(expectedPvbp, 1e-9m,
            "PVBP обязан быть тождественно modDur * dirtyPrice * 0.0001 — прямое определение, не оценка");

        // Приближённая проверка смысла PVBP: переоценка потока по (y+1бп) должна отличаться от
        // цены примерно на -PVBP (линейное приближение первого порядка, точность ограничена
        // выпуклостью — используем мягкий допуск).
        var bumpedYtm = ytm.Value.EffectiveYield + 0.0001m;
        var flows = cashFlow
            .Select(c => (Days: (double)(c.Date.DayNumber - AsOf.DayNumber), Amount: (double)c.TotalAmount))
            .Where(f => f.Days > 0)
            .ToList();
        var bumpedPrice = (decimal)flows.Sum(f => f.Amount / Math.Pow(1.0 + (double)bumpedYtm, f.Days / 365.0));
        var actualPriceDrop = dirtyPrice - bumpedPrice;

        actualPriceDrop.Should().BeApproximately(duration.Value.Pvbp, 0.01m,
            "PVBP должен приближённо предсказывать падение цены при росте доходности на 1 б.п.");
    }

    // ─── Инвариант: XIRR потока «покупка → продажа через год с ростом стоимости на 10%» ≈ 0.10 ──

    [Fact]
    public void Audit_Xirr_SingleBuyAndSellAfterOneYearWithTenPercentGain_ApproximatesTenPercent()
    {
        var buyDate = new DateOnly(2025, 1, 1);
        var sellDate = buyDate.AddYears(1);

        var flows = new List<XirrCalculator.CashFlow>
        {
            new(buyDate, -10_000m),
            new(sellDate, 11_000m), // ровно +10% через ровно 1 календарный год
        };

        var result = XirrCalculator.Calculate(flows);

        result.Should().NotBeNull();
        result!.Value.Rate.Should().BeApproximately(0.10m, 5e-4m,
            "один поток покупки и один поток продажи через год с ростом на 10% должен давать XIRR ≈ 10% годовых");
    }

    // ─── Инвариант: НКД = 0 в дату купона и растёт линейно между купонами ──────────────────────

    [Fact]
    public void Audit_AccruedInterest_GrowsLinearlyBetweenCoupons_ZeroAtCouponDate()
    {
        // Три купона подряд: НКД в дату ВТОРОГО купона (не последнего в графике — иначе движок
        // корректно вернёт null, т.к. горизонт не за пределами графика, см.
        // AccruedInterestCalculatorTests.Calculate_NoFutureCoupon_ReturnsNull) должен быть равен
        // нулю (копится в счёт следующего периода "с чистого листа").
        var periodStart = new DateOnly(2025, 1, 1);
        var couponDate = periodStart.AddDays(182);
        var thirdCoupon = couponDate.AddDays(182);
        var coupons = new List<CouponSchedule>
        {
            TestModelFactory.Coupon(InstrumentId, periodStart, 40m, periodDays: 182),
            TestModelFactory.Coupon(InstrumentId, couponDate, 40m, periodDays: 182),
            TestModelFactory.Coupon(InstrumentId, thirdCoupon, 40m, periodDays: 182),
        };

        var atStart = AccruedInterestCalculator.Calculate(periodStart, coupons);
        var atQuarter = AccruedInterestCalculator.Calculate(periodStart.AddDays(45), coupons);
        var atHalf = AccruedInterestCalculator.Calculate(periodStart.AddDays(91), coupons);
        var atCoupon = AccruedInterestCalculator.Calculate(couponDate, coupons);

        atStart.Should().Be(0m, "НКД в начале периода = 0");
        atCoupon.Should().Be(0m, "НКД в дату купона = 0 (копится в счёт СЛЕДУЮЩЕГО купона, не текущего)");

        atQuarter.Should().NotBeNull();
        atHalf.Should().NotBeNull();

        // Точные эталоны по формуле пропорционального накопления (купон * прошедшие_дни / период),
        // а не относительное "ровно вдвое" — 91/45 не даёт ровно 2х из-за целочисленных дней.
        var expectedAtQuarter = 40m * 45m / 182m;
        var expectedAtHalf = 40m * 91m / 182m;
        atQuarter!.Value.Should().BeApproximately(expectedAtQuarter, 1e-4m);
        atHalf!.Value.Should().BeApproximately(expectedAtHalf, 1e-4m);
    }

    // ─── Граничные случаи солверов и построителя потока ─────────────────────────────────────────

    [Fact]
    public void Audit_NegativeYieldEnvironment_YtmConverges_WhenPriceExceedsUndiscountedCashFlow()
    {
        // Цена выше суммы номинала+купонов => подразумеваемая доходность отрицательна.
        // Решатель обязан сойтись (гарантия сходимости — не опция, plan/05).
        var maturity = AsOf.AddYears(2);
        const decimal faceValue = 1000m;
        var coupons = new List<CouponSchedule>
        {
            TestModelFactory.Coupon(InstrumentId, AsOf.AddYears(1), 10m, periodDays: 365),
            TestModelFactory.Coupon(InstrumentId, maturity, 10m, periodDays: 365),
        };
        var cashFlow = BondCashFlowBuilder.Build(faceValue, AsOf, maturity, coupons, amortizations: null);

        // Суммарный недисконтированный поток = 1000 + 10 + 10 = 1020; цена 1100 => YTM < 0.
        var ytm = YtmCalculator.Calculate(1100m, AsOf, cashFlow);

        ytm.Should().NotBeNull("решатель должен находить отрицательные доходности, а не молча отказывать");
        ytm!.Value.EffectiveYield.Should().BeLessThan(0m);
    }

    [Fact]
    public void Audit_RedemptionTomorrow_ProducesSingleNearTermCashFlow_YtmConverges()
    {
        // Бумага гасится завтра — экстремально короткий горизонт (граница между "ещё есть поток"
        // и "уже погашена").
        var maturity = AsOf.AddDays(1);
        const decimal faceValue = 1000m;
        var coupons = new List<CouponSchedule>
        {
            TestModelFactory.Coupon(InstrumentId, maturity, 5m, periodDays: 365),
        };
        var cashFlow = BondCashFlowBuilder.Build(faceValue, AsOf, maturity, coupons, amortizations: null);

        cashFlow.Should().HaveCount(1);
        cashFlow[0].TotalAmount.Should().Be(1005m);

        var ytm = YtmCalculator.Calculate(999m, AsOf, cashFlow);
        ytm.Should().NotBeNull("однодневный горизонт — законный, хоть и экстремальный, вход; решатель обязан сойтись");
    }

    [Fact]
    public void Audit_BondPastLastCoupon_EmptyCashFlowWindow_HorizonBeforeAsOf_YtmReturnsNull()
    {
        // "Бумага после последнего купона": все известные купоны в графике уже в прошлом
        // относительно asOf, а дата погашения тоже уже наступила (рассинхрон данных/просрочка
        // обновления справочника). BondCashFlowBuilder не имеет права придумывать поток "в прошлое".
        var pastMaturity = AsOf.AddDays(-5);
        var coupons = new List<CouponSchedule>
        {
            TestModelFactory.Coupon(InstrumentId, AsOf.AddDays(-200), 40m, periodDays: 182),
            TestModelFactory.Coupon(InstrumentId, pastMaturity, 40m, periodDays: 182),
        };

        var cashFlow = BondCashFlowBuilder.Build(1000m, AsOf, pastMaturity, coupons, amortizations: null);
        cashFlow.Should().BeEmpty("все потоки строго в прошлом относительно asOf — будущего потока нет");

        YtmCalculator.Calculate(950m, AsOf, cashFlow).Should().BeNull();
    }

    [Fact]
    public void Audit_CurrentYield_ZeroAccruedInterest_DoesNotDivideByZero()
    {
        // Грязная цена > 0, но НКД=0 (дата ровно на границе периода) — знаменатель CurrentYield
        // это dirtyPrice, не accrued, так что деления на ноль здесь в принципе не должно быть;
        // явный regression-тест на этот случай (граница §8 "деление на ноль в current yield").
        var coupons = new List<CouponSchedule>
        {
            TestModelFactory.Coupon(InstrumentId, AsOf.AddDays(182), 30m, periodDays: 182),
        };

        var result = CurrentYieldCalculator.Calculate(AsOf, dirtyPrice: 1000m, coupons);

        result.Should().NotBeNull();
        result!.Value.Should().BeApproximately(30m * 365m / 182m / 1000m, 1e-6m);
    }

    [Fact]
    public void Audit_CurrentYield_ZeroDirtyPrice_ReturnsNullInsteadOfDivideByZeroOrInfinity()
    {
        var coupons = new List<CouponSchedule>
        {
            TestModelFactory.Coupon(InstrumentId, AsOf.AddDays(182), 30m, periodDays: 182),
        };

        CurrentYieldCalculator.Calculate(AsOf, dirtyPrice: 0m, coupons).Should().BeNull();
    }

    [Fact]
    public void Audit_EmptyAllSchedules_BondMetricsCalculator_DoesNotThrow_MarksEstimated()
    {
        // Полностью пустые графики (купоны/амортизации/оферты) — деградированный, но легитимный
        // вход (например, только что заведённый инструмент, MOEX ещё не отдал bondization).
        var input = new BondMetricsCalculatorInput
        {
            InstrumentId = InstrumentId,
            AsOf = AsOf,
            FaceValue = 1000m,
            MaturityDate = AsOf.AddYears(3),
            CouponType = CouponType.Fixed,
            HasAmortization = false,
            HasOffers = false,
            DataIncomplete = false,
            CleanPrice = 950m,
            AccruedInterestFromSource = 0m,
            Coupons = Array.Empty<CouponSchedule>(),
            Amortizations = Array.Empty<AmortizationSchedule>(),
            Offers = Array.Empty<OfferSchedule>(),
        };

        var act = () => BondMetricsCalculator.Calculate(input);

        act.Should().NotThrow();
        var metrics = act();
        // Пустой график купонов всё ещё образует денежный поток из одного погашения — YTM
        // формально считается (см. существующий тест Calculate_EmptyCouponSchedule_...).
        metrics.YtmEffective.Should().NotBeNull();
    }

    [Fact]
    public void Audit_ZeroValueCoupon_TreatedAsKnownNotUnknown_DoesNotThrow()
    {
        // "Купон с нулевой суммой" (данные MOEX бывают грязные, spec-аудит §методы п.3): парсер
        // ставит IsKnown=true, если value_rub присутствует и равен 0 (0 - валидное decimal-значение,
        // не null) — это осознанное поведение (ноль отличим от null), но проверяем, что дальше по
        // цепочке (YTM/НКД) это не приводит к делению на ноль/исключению, просто добавляет нулевой
        // поток к общей сумме.
        var maturity = AsOf.AddYears(1);
        var coupons = new List<CouponSchedule>
        {
            TestModelFactory.Coupon(InstrumentId, maturity, 0m, periodDays: 365, isKnown: true),
        };

        var cashFlow = BondCashFlowBuilder.Build(1000m, AsOf, maturity, coupons, amortizations: null);
        cashFlow.Should().HaveCount(1);
        cashFlow[0].CouponAmount.Should().Be(0m);
        cashFlow[0].PrincipalAmount.Should().Be(1000m);

        var act = () => YtmCalculator.Calculate(950m, AsOf, cashFlow);
        act.Should().NotThrow();
        act().Should().NotBeNull("нулевой купон не делает поток невалидным — просто нет купонной составляющей");
    }
}
