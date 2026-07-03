using Bonds.Core.Calculation;
using Bonds.Core.Models;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Calculation;

/// <summary>
/// Тесты фасада движка (plan/05 Часть C/D): сборка всех метрик в единый <see cref="BondMetrics"/>
/// с уважением обязательных краевых случаев — флоатер, индексируемая бумага, амортизация,
/// оферта, неполные данные (plan/05 Часть B). Это основной интеграционный слой чистой логики
/// этапа 05 (сам движок остаётся без I/O — вход полностью собран в тесте как value-объект).
/// </summary>
public class BondMetricsCalculatorTests
{
    private const ulong InstrumentId = 42;
    private static readonly DateOnly AsOf = new(2025, 1, 1);

    private static BondMetricsCalculatorInput FixedCouponInput(
        DateOnly maturity,
        decimal cleanPrice,
        IReadOnlyList<CouponSchedule> coupons,
        bool dataIncomplete = false,
        IReadOnlyList<AmortizationSchedule>? amortizations = null,
        IReadOnlyList<OfferSchedule>? offers = null,
        YieldCurveSnapshot? curve = null) => new()
    {
        InstrumentId = InstrumentId,
        AsOf = AsOf,
        FaceValue = 1000m,
        MaturityDate = maturity,
        CouponType = CouponType.Fixed,
        HasAmortization = amortizations is { Count: > 0 },
        HasOffers = offers is { Count: > 0 },
        DataIncomplete = dataIncomplete,
        CleanPrice = cleanPrice,
        Coupons = coupons,
        Amortizations = amortizations ?? Array.Empty<AmortizationSchedule>(),
        Offers = offers ?? Array.Empty<OfferSchedule>(),
        CurveSnapshot = curve,
    };

    // ─── Фикс-купонная бумага: полный набор метрик ─────────────────────────

    [Fact]
    public void Calculate_FixedCouponBond_ProducesFullMetricsSet()
    {
        var maturity = AsOf.AddDays(730);
        var coupons = new[]
        {
            TestModelFactory.Coupon(InstrumentId, AsOf.AddDays(365), 100m, periodDays: 365),
            TestModelFactory.Coupon(InstrumentId, maturity, 100m, periodDays: 365),
        };

        var input = FixedCouponInput(maturity, cleanPrice: 966.1989795918366m, coupons)
            with
        {
            AccruedInterestFromSource = 0m, // считаем на дату ровно начала периода — НКД=0 для простоты эталона
        };

        var metrics = BondMetricsCalculator.Calculate(input);

        metrics.IsFloater.Should().BeFalse();
        metrics.IsIndexed.Should().BeFalse();
        metrics.DataIncomplete.Should().BeFalse();
        metrics.CalculatedToOffer.Should().BeFalse();
        metrics.DirtyPrice.Should().Be(966.1989795918366m);
        metrics.YtmEffective.Should().NotBeNull();
        metrics.YtmEffective!.Value.Should().BeApproximately(0.12m, 1e-4m);
        metrics.MacaulayDuration.Should().NotBeNull();
        metrics.ModifiedDuration.Should().NotBeNull();
        metrics.Convexity.Should().NotBeNull();
        metrics.Pvbp.Should().NotBeNull();
    }

    [Fact]
    public void Calculate_FixedCouponBond_WithCurveSnapshot_ComputesGSpread()
    {
        var maturity = AsOf.AddDays(730);
        var coupons = new[]
        {
            TestModelFactory.Coupon(InstrumentId, AsOf.AddDays(365), 100m, periodDays: 365),
            TestModelFactory.Coupon(InstrumentId, maturity, 100m, periodDays: 365),
        };
        // B1 в базисных пунктах (методика MOEX, не в долях) — exp(861.776976.../10000)-1 = 0.09
        // ровно, при b2=b3=0 (Свенссон вырождается в константу, без поправок Gi).
        var curve = TestModelFactory.CurveSnapshot(b1: 861.7769624105241m, b2: 0m, b3: 0m, t1: 1m);

        var input = FixedCouponInput(maturity, cleanPrice: 966.1989795918366m, coupons, curve: curve)
            with
        { AccruedInterestFromSource = 0m };

        var metrics = BondMetricsCalculator.Calculate(input);

        metrics.GSpread.Should().NotBeNull();
        // Кривая константная (b2=b3=0, Gi=0) => значение кривой на любой срок = 0.09; G-спред = YTM(0.12) - 0.09.
        metrics.GSpread!.Value.Should().BeApproximately(0.03m, 1e-3m);
    }

    [Fact]
    public void Calculate_MissingCleanPrice_ReturnsResultWithoutYtm_NoException()
    {
        var maturity = AsOf.AddDays(365);
        var coupons = new[] { TestModelFactory.Coupon(InstrumentId, maturity, 100m) };

        var input = FixedCouponInput(maturity, cleanPrice: 0m, coupons) with { CleanPrice = null };

        var act = () => BondMetricsCalculator.Calculate(input);

        act.Should().NotThrow();
        var metrics = act();
        metrics.YtmEffective.Should().BeNull();
        metrics.DataIncomplete.Should().BeTrue("отсутствие цены делает метрики недостоверными");
    }

    // ─── Флоатер ────────────────────────────────────────────────────────────

    [Fact]
    public void Calculate_Floater_DoesNotComputeYtm_ReturnsCurrentYieldAndFlag()
    {
        var maturity = AsOf.AddYears(5);
        var coupons = new[]
        {
            TestModelFactory.Coupon(InstrumentId, AsOf.AddDays(30), 20m, periodDays: 91), // известный текущий купон
            TestModelFactory.Coupon(InstrumentId, AsOf.AddDays(121), null, periodDays: 91, isKnown: false), // будущий — неизвестен
        };

        var input = new BondMetricsCalculatorInput
        {
            InstrumentId = InstrumentId,
            AsOf = AsOf,
            FaceValue = 1000m,
            MaturityDate = maturity,
            CouponType = CouponType.Floating,
            HasAmortization = false,
            HasOffers = false,
            DataIncomplete = false,
            CleanPrice = 1000m,
            AccruedInterestFromSource = 5m,
            Coupons = coupons,
        };

        var metrics = BondMetricsCalculator.Calculate(input);

        metrics.IsFloater.Should().BeTrue();
        metrics.IsEstimated.Should().BeTrue();
        metrics.YtmEffective.Should().BeNull("YTM не считается для флоатера (spec §6)");
        metrics.YtmSimple.Should().BeNull();
        metrics.CurrentYield.Should().NotBeNull();
        // Купон 20 на период 91 день -> годовая ставка = 20 * 365/91 / 1005 (грязная цена 1000+5).
        var expected = 20m * 365m / 91m / 1005m;
        metrics.CurrentYield!.Value.Should().BeApproximately(expected, 1e-4m);
    }

    // ─── Индексируемая бумага ───────────────────────────────────────────────

    [Fact]
    public void Calculate_IndexedBond_TreatedLikeFloater_NoYtm_FlagsSet()
    {
        var maturity = AsOf.AddYears(10);
        var coupons = new[]
        {
            TestModelFactory.Coupon(InstrumentId, AsOf.AddDays(180), 25m, periodDays: 182),
        };

        var input = new BondMetricsCalculatorInput
        {
            InstrumentId = InstrumentId,
            AsOf = AsOf,
            FaceValue = 1000m,
            MaturityDate = maturity,
            CouponType = CouponType.Indexed,
            HasAmortization = false,
            HasOffers = false,
            DataIncomplete = false,
            CleanPrice = 950m,
            AccruedInterestFromSource = 3m,
            Coupons = coupons,
        };

        var metrics = BondMetricsCalculator.Calculate(input);

        metrics.IsIndexed.Should().BeTrue();
        metrics.IsFloater.Should().BeFalse();
        metrics.IsEstimated.Should().BeTrue();
        metrics.YtmEffective.Should().BeNull("для индексируемой бумаги YTM не считается, как для флоатера (spec §6)");
        metrics.CurrentYield.Should().NotBeNull();
    }

    // ─── Валютная (вне рублёвого скоупа) бумага ───────────────────────────────

    [Fact]
    public void Calculate_OutOfScopeCurrency_DoesNotComputeYtmOrGSpread_FlagsEstimated()
    {
        // T-2/N-2: USD-номинал (замещающая облигация). Цена в ₽, номинал/купон в смешанных
        // единицах → YTM/дюрация/G-спред бессмысленны. Не считаем их, помечаем оценочной.
        var maturity = AsOf.AddDays(730);
        var coupons = new[]
        {
            TestModelFactory.Coupon(InstrumentId, AsOf.AddDays(365), 100m, periodDays: 365),
            TestModelFactory.Coupon(InstrumentId, maturity, 100m, periodDays: 365),
        };
        var curve = TestModelFactory.CurveSnapshot(b1: 861.7769624105241m, b2: 0m, b3: 0m, t1: 1m);

        var input = FixedCouponInput(maturity, cleanPrice: 966.1989795918366m, coupons, curve: curve)
            with
        { AccruedInterestFromSource = 0m, IsOutOfScopeCurrency = true };

        var metrics = BondMetricsCalculator.Calculate(input);

        metrics.YtmEffective.Should().BeNull("валютная бумага не считается в рублёвом контуре");
        metrics.ModifiedDuration.Should().BeNull();
        metrics.Convexity.Should().BeNull();
        metrics.Pvbp.Should().BeNull();
        metrics.GSpread.Should().BeNull("нет рублёвого G-спреда −800 б.п. у USD-бумаги");
        metrics.IsEstimated.Should().BeTrue();
        metrics.Notes.Should().Contain(n => n.Contains("валют", StringComparison.OrdinalIgnoreCase));
    }

    // ─── Амортизация ─────────────────────────────────────────────────────────

    [Fact]
    public void Calculate_AmortizingBond_DiffersPredictablyFromNonAmortizingAnalog()
    {
        var maturity = AsOf.AddDays(730);
        var amortizationDate = AsOf.AddDays(365);

        var nonAmortizingCoupons = new[]
        {
            TestModelFactory.Coupon(InstrumentId, amortizationDate, 100m, periodDays: 365),
            TestModelFactory.Coupon(InstrumentId, maturity, 100m, periodDays: 365),
        };
        var nonAmortizingInput = FixedCouponInput(maturity, cleanPrice: 950m, nonAmortizingCoupons)
            with
        { AccruedInterestFromSource = 0m };

        var amortizingCoupons = new[]
        {
            TestModelFactory.Coupon(InstrumentId, amortizationDate, 100m, periodDays: 365),
            TestModelFactory.Coupon(InstrumentId, maturity, 50m, periodDays: 365), // купон на остаток номинала (500)
        };
        var amortizations = new[] { TestModelFactory.Amortization(InstrumentId, amortizationDate, 500m) };
        var amortizingInput = FixedCouponInput(maturity, cleanPrice: 950m, amortizingCoupons, amortizations: amortizations)
            with
        { AccruedInterestFromSource = 0m };

        var nonAmortizingMetrics = BondMetricsCalculator.Calculate(nonAmortizingInput);
        var amortizingMetrics = BondMetricsCalculator.Calculate(amortizingInput);

        amortizingMetrics.HasAmortization.Should().BeTrue();
        nonAmortizingMetrics.HasAmortization.Should().BeFalse();

        // Денежный поток амортизируемой бумаги возвращает деньги раньше (часть номинала уходит
        // через год вместо двух) при той же цене — дюрация должна быть короче.
        amortizingMetrics.MacaulayDuration.Should().NotBeNull();
        nonAmortizingMetrics.MacaulayDuration.Should().NotBeNull();
        amortizingMetrics.MacaulayDuration!.Value.Should().BeLessThan(nonAmortizingMetrics.MacaulayDuration!.Value,
            "более ранний возврат номинала при амортизации сокращает средневзвешенный срок потока");
    }

    /// <summary>
    /// Audit(engine) E-1: амортизация с известной датой, но неизвестной суммой (MBS/ипотечный
    /// агент) должна деградировать YTM/дюрацию/PVBP/G-спред так же, как неизвестный купон
    /// флоатера — не считать их вовсе, а не молча посчитать на исковерканной форме потока
    /// (весь номинал одним платежом на дату юридического погашения).
    /// </summary>
    [Fact]
    public void Calculate_AmortizationWithUnknownAmount_DoesNotComputeYtmOrDuration_MarksEstimated()
    {
        var maturity = AsOf.AddDays(365 * 17);
        var coupons = new[] { TestModelFactory.Coupon(InstrumentId, maturity, 5m, periodDays: 365) };
        var amortizations = new[]
        {
            TestModelFactory.Amortization(InstrumentId, AsOf.AddDays(60), 0m, isKnown: false),
            TestModelFactory.Amortization(InstrumentId, AsOf.AddDays(150), 0m, isKnown: false),
        };

        var input = FixedCouponInput(maturity, cleanPrice: 88.84m, coupons, amortizations: amortizations)
            with
        { AccruedInterestFromSource = 0m };

        var metrics = BondMetricsCalculator.Calculate(input);

        metrics.YtmEffective.Should().BeNull("остаточный номинал недостоверен из-за неизвестных амортизаций — YTM не считаем");
        metrics.MacaulayDuration.Should().BeNull();
        metrics.ModifiedDuration.Should().BeNull();
        metrics.Pvbp.Should().BeNull();
        metrics.GSpread.Should().BeNull();
        metrics.IsEstimated.Should().BeTrue();
    }

    // ─── Оферта ──────────────────────────────────────────────────────────────

    [Fact]
    public void Calculate_BondWithEligibleOffer_CalculatesToOfferNotMaturity()
    {
        var maturity = AsOf.AddYears(10);
        var offerDate = AsOf.AddDays(365);
        var coupons = new[]
        {
            TestModelFactory.Coupon(InstrumentId, offerDate, 100m, periodDays: 365),
            TestModelFactory.Coupon(InstrumentId, maturity, 100m, periodDays: 365), // далеко за горизонтом оферты
        };
        var offers = new[] { TestModelFactory.Offer(InstrumentId, offerDate, OfferType.Put) };

        var input = FixedCouponInput(maturity, cleanPrice: 950m, coupons, offers: offers)
            with
        { AccruedInterestFromSource = 0m };

        var metrics = BondMetricsCalculator.Calculate(input);

        metrics.CalculatedToOffer.Should().BeTrue();
        metrics.HorizonDate.Should().Be(offerDate);
        metrics.YtmEffective.Should().NotBeNull();
    }

    [Fact]
    public void Calculate_OfferCloserThan14Days_IsIgnored_CalculatesToMaturity()
    {
        var maturity = AsOf.AddDays(400);
        var tooCloseOffer = AsOf.AddDays(10);
        var coupons = new[] { TestModelFactory.Coupon(InstrumentId, maturity, 100m, periodDays: 400) };
        var offers = new[] { TestModelFactory.Offer(InstrumentId, tooCloseOffer, OfferType.Put) };

        var input = FixedCouponInput(maturity, cleanPrice: 950m, coupons, offers: offers)
            with
        { AccruedInterestFromSource = 0m };

        var metrics = BondMetricsCalculator.Calculate(input);

        metrics.CalculatedToOffer.Should().BeFalse();
        metrics.HorizonDate.Should().Be(maturity);
    }

    // ─── Купон с нулевой суммой (E-2) ───────────────────────────────────────

    /// <summary>
    /// Audit(engine) E-2: представительный (ближайший регулярный) купон равен ровно 0 (грязные
    /// данные MOEX, value_rub=0, не null — технически IsKnown=true), а соседние купоны по графику
    /// ненулевые — подозрительно, вероятно дефект данных. Не блокируем расчёт (текущая доходность
    /// формально 0% — арифметически верно "как есть"), но добавляем предупреждающую заметку,
    /// чтобы это не выглядело неотличимо от "всё в порядке".
    /// </summary>
    [Fact]
    public void Calculate_RepresentativeCouponIsZero_AmongNonZeroNeighbors_AddsSuspiciousNote()
    {
        var maturity = AsOf.AddDays(365 * 2);
        var coupons = new[]
        {
            TestModelFactory.Coupon(InstrumentId, AsOf.AddDays(365), 0m, periodDays: 365), // ближайший будущий — подозрительный ноль
            TestModelFactory.Coupon(InstrumentId, maturity, 100m, periodDays: 365),
        };

        var input = FixedCouponInput(maturity, cleanPrice: 950m, coupons)
            with
        { AccruedInterestFromSource = 0m };

        var metrics = BondMetricsCalculator.Calculate(input);

        metrics.CurrentYield.Should().Be(0m, "арифметически ноль — данные берутся as-is, не выдумываем значение");
        metrics.Notes.Should().Contain(n => n.Contains("нул", StringComparison.OrdinalIgnoreCase),
            "нулевой купон среди ненулевых соседей должен быть явно помечен как подозрительный, не молча посчитан");
    }

    [Fact]
    public void Calculate_AllCouponsAreZero_GenuineZeroCoupon_NoSuspiciousNote()
    {
        // Если ВСЕ купоны в графике нулевые — это, скорее всего, настоящая структурированная
        // бумага с нулевым купоном (нет ненулевых "соседей" для сравнения), а не дефект данных —
        // не поднимаем ложную тревогу.
        var maturity = AsOf.AddDays(365 * 2);
        var coupons = new[]
        {
            TestModelFactory.Coupon(InstrumentId, AsOf.AddDays(365), 0m, periodDays: 365),
            TestModelFactory.Coupon(InstrumentId, maturity, 0m, periodDays: 365),
        };

        var input = FixedCouponInput(maturity, cleanPrice: 950m, coupons)
            with
        { AccruedInterestFromSource = 0m };

        var metrics = BondMetricsCalculator.Calculate(input);

        metrics.CurrentYield.Should().Be(0m);
        metrics.Notes.Should().NotContain(n => n.Contains("нул", StringComparison.OrdinalIgnoreCase));
    }

    // ─── Неполные данные ─────────────────────────────────────────────────────

    [Fact]
    public void Calculate_DataIncomplete_MarksResultWithoutThrowing()
    {
        var maturity = AsOf.AddDays(365);
        var coupons = new[] { TestModelFactory.Coupon(InstrumentId, maturity, 100m, periodDays: 365) };

        var input = FixedCouponInput(maturity, cleanPrice: 950m, coupons, dataIncomplete: true)
            with
        { AccruedInterestFromSource = 0m };

        var act = () => BondMetricsCalculator.Calculate(input);

        act.Should().NotThrow();
        var metrics = act();
        metrics.DataIncomplete.Should().BeTrue();
        metrics.Notes.Should().NotBeEmpty();
    }

    [Fact]
    public void Calculate_EmptyCouponSchedule_DoesNotThrow_StillRepaysFaceValueAtMaturity()
    {
        // Пустой график купонов — денежный поток вырождается до единственного платежа
        // (погашение номинала на дату погашения); YTM в этом случае всё ещё формально считаем
        // (как для бескупонной облигации) — это математически корректно, поведение это не
        // "недостоверность по неполноте данных" (за неё отвечает отдельный флаг DataIncomplete
        // на уровне Instrument, проверяется в Calculate_DataIncomplete_MarksResultWithoutThrowing).
        var maturity = AsOf.AddDays(365);

        var input = FixedCouponInput(maturity, cleanPrice: 950m, Array.Empty<CouponSchedule>())
            with
        { AccruedInterestFromSource = 0m };

        var act = () => BondMetricsCalculator.Calculate(input);

        act.Should().NotThrow();
        var metrics = act();
        metrics.YtmEffective.Should().NotBeNull("единственный поток — погашение номинала — всё ещё образует валидный денежный поток для YTM");
    }

    [Fact]
    public void Calculate_NoCouponsAndHorizonBeforeAsOf_NoCashFlow_MarksEstimatedWithoutThrowing()
    {
        // Горизонт раньше даты расчёта (например, рассинхрон данных о погашении) — поток пуст,
        // YTM не считается, но и исключения нет (spec §4.4).
        var maturityInPast = AsOf.AddDays(-1);

        var input = FixedCouponInput(maturityInPast, cleanPrice: 950m, Array.Empty<CouponSchedule>())
            with
        { AccruedInterestFromSource = 0m };

        var act = () => BondMetricsCalculator.Calculate(input);

        act.Should().NotThrow();
        var metrics = act();
        metrics.YtmEffective.Should().BeNull();
        metrics.IsEstimated.Should().BeTrue();
    }
}
