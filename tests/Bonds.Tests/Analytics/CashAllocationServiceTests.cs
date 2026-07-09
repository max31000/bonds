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
        bool lotSizeIsAssumed = true,
        decimal cleanPriceRub = 0m,
        decimal accruedRub = 0m,
        decimal commissionRub = 0m,
        bool isComparable = true) => new()
    {
        InstrumentId = instrumentId,
        Name = $"Bond {instrumentId}",
        Issuer = issuer,
        EffectiveYield = effectiveYield,
        PricePerLotRub = pricePerLotRub,
        LotSize = lotSize,
        LotSizeIsAssumed = lotSizeIsAssumed,
        CurrentIssuerMarketValueRub = currentIssuerMarketValueRub,
        CleanPriceRub = cleanPriceRub,
        AccruedRub = accruedRub,
        CommissionRub = commissionRub,
        IsComparable = isComparable,
    };

    /// <summary>Задача 34 — кандидат рыночного источника (market/recommended): InstrumentId=null,
    /// Issuer=null (банк не хранит эмитента), Secid/Sector заполнены — см. doc-comment
    /// CashAllocationCandidate.</summary>
    private static CashAllocationCandidate SectorCandidate(
        string secid, string sector, decimal yield, decimal price) => new()
    {
        InstrumentId = null,
        Secid = secid,
        Name = $"Bond {secid}",
        Issuer = null,
        Sector = sector,
        EffectiveYield = yield,
        PricePerLotRub = price,
        LotSize = 1m,
        LotSizeIsAssumed = true,
        CurrentIssuerMarketValueRub = 0m,
        IsComparable = true,
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

    // ─── Задача 31 часть B.3: гейт сравнимости (флоатер исключён, даже с самой высокой доходностью) ──

    [Fact]
    public void Allocate_NonComparableCandidateWithHighestYield_IsSkipped_FixedCandidateGetsBudgetInstead()
    {
        // Флоатер (IsComparable=false) с самой высокой EffectiveYield (0.25 — CurrentYield, не YTM,
        // подставлен вызывающим слоем как обычно) ранжировался бы первым и получил бы всю сумму —
        // гейт сравнимости отсекает его ДО ранжирования по доходности, поэтому вся сумма (2 лота по
        // 1000, большой портфель — лимит концентрации не мешает) уходит фикс-купонной "A", как если
        // бы флоатера не было в списке кандидатов вовсе (эталон).
        var candidates = new[]
        {
            Candidate(1, effectiveYield: 0.25m, pricePerLotRub: 1000m, issuer: "Floater Co", isComparable: false),
            Candidate(2, effectiveYield: 0.12m, pricePerLotRub: 1000m, issuer: "A"),
        };

        var result = CashAllocationService.Allocate(amountRub: 2000m, candidates, currentPortfolioValueRub: 100_000m);

        result.Skipped.Should().ContainSingle(s => s.InstrumentId == 1 && s.Reason == CashAllocationSkipReason.NotComparable);
        result.Allocations.Should().ContainSingle(a => a.InstrumentId == 2, "флоатер исключён — вся сумма достаётся фикс-купонной бумаге");
        result.Allocations[0].Quantity.Should().Be(2m);
        result.Allocations[0].EstimatedCostRub.Should().Be(2000m);
        result.LeftoverRub.Should().Be(0m);
    }

    [Fact]
    public void Allocate_NonComparableCandidate_NotCheckedForYieldOrPrice_SkippedAsNotComparableFirst()
    {
        // Гейт сравнимости идёт ПЕРЕД гейтом доходности/цены (план часть B.3) — флоатер без цены
        // вообще должен уйти в NotComparable, а не в NoPrice (порядок проверок имеет значение для
        // диагностики причины в UI).
        var candidates = new[]
        {
            Candidate(1, effectiveYield: null, pricePerLotRub: 0m, issuer: "Floater Co", isComparable: false),
        };

        var result = CashAllocationService.Allocate(amountRub: 1000m, candidates, currentPortfolioValueRub: 100_000m);

        result.Skipped.Should().ContainSingle(s => s.InstrumentId == 1 && s.Reason == CashAllocationSkipReason.NotComparable);
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

    // ─── Задача 24: разложение цены лота (чистая цена + НКД + комиссия) ────────────────────────

    [Fact]
    public void Allocate_SplitsEstimatedCostAcrossMultipleLots_SumsReconcile()
    {
        // Один лот стоит 1046 = 1012 (чистая) + 34 (НКД) + 0.5*... комиссия — используем ровные числа:
        // clean 1000 + accrued 40 + commission 6 = 1046 за лот, покупаем 2 лота на 2100 руб.
        var candidates = new[]
        {
            Candidate(1, 0.12m, pricePerLotRub: 1046m, issuer: "A", cleanPriceRub: 1000m, accruedRub: 40m, commissionRub: 6m),
        };

        var result = CashAllocationService.Allocate(amountRub: 2100m, candidates, currentPortfolioValueRub: 100_000m);

        result.Allocations.Should().ContainSingle();
        var line = result.Allocations[0];
        line.Quantity.Should().Be(2m); // 2 лота (LotSize=1)
        line.EstimatedCostRub.Should().Be(2092m); // 2 * 1046

        // Разложение должно сходиться: clean + accrued + commission = estimatedCost.
        (line.CleanCostRub + line.AccruedCostRub + line.CommissionCostRub).Should().Be(line.EstimatedCostRub);
        line.CleanCostRub.Should().Be(2000m); // 2 * 1000
        line.AccruedCostRub.Should().Be(80m); // 2 * 40
        line.CommissionCostRub.Should().Be(12m); // 2 * 6
    }

    [Fact]
    public void Allocate_WithoutBreakdownProvided_DefaultsToZero_DoesNotThrow()
    {
        var candidates = new[]
        {
            Candidate(1, 0.12m, pricePerLotRub: 1000m, issuer: "A"), // cleanPriceRub/accruedRub/commissionRub не заданы — 0 по умолчанию
        };

        var result = CashAllocationService.Allocate(amountRub: 1000m, candidates, currentPortfolioValueRub: 100_000m);

        result.Allocations.Should().ContainSingle();
        result.Allocations[0].CleanCostRub.Should().Be(0m);
        result.Allocations[0].AccruedCostRub.Should().Be(0m);
        result.Allocations[0].CommissionCostRub.Should().Be(0m);
    }

    // ─── Задача 34 часть D: source=market/recommended — ось Sector, лимит лотов, ось None ────────

    [Fact]
    public void Allocate_SectorAxis_RespectsSectorShareLimit_MovesRemainderToOtherSector()
    {
        // Эталон вручную (план часть D.1, лимит 30%, amountRub=10 000):
        // A1 (Корп, yield 0.20) покупается первым — лоты по 1000 руб, доля сектора считается от
        // ФИКСИРОВАННОГО amountRub (не от растущей базы, см. doc-comment Allocate): лот 1 → 10%,
        // лот 2 → 20%, лот 3 → 30% (не строго больше лимита — покупается), лот 4 → 40% > 30% — стоп.
        // A1 заканчивает с 3 лотами (3000 руб).
        // A2 (тоже Корп, yield 0.18, ниже A1) видит бакет "Корп" уже на 3000 — первый же лот дал бы
        // 4000/10000=40% > 30% — блокируется ПОЛНОСТЬЮ (0 лотов), несмотря на то что доходнее B1.
        // B1 (Гос, yield 0.10) — бакет "Гос" ещё пуст, покупается по той же логике: 3 лота (3000 руб).
        // Итог: A1=3000, B1=3000, A2 пропущен лимитом, остаток = 10000-3000-3000=4000.
        var candidates = new[]
        {
            SectorCandidate("A1", "Корп", yield: 0.20m, price: 1000m),
            SectorCandidate("A2", "Корп", yield: 0.18m, price: 1000m),
            SectorCandidate("B1", "Гос", yield: 0.10m, price: 1000m),
        };

        var result = CashAllocationService.Allocate(
            amountRub: 10_000m,
            candidates,
            currentPortfolioValueRub: 0m,
            concentrationAxis: CashAllocationConcentrationAxis.Sector,
            maxSectorSharePercent: 30m);

        result.Allocations.Should().HaveCount(2);
        result.Allocations.Single(a => a.Secid == "A1").Quantity.Should().Be(3m);
        result.Allocations.Single(a => a.Secid == "A1").EstimatedCostRub.Should().Be(3000m);
        result.Allocations.Single(a => a.Secid == "B1").Quantity.Should().Be(3m);
        result.Allocations.Single(a => a.Secid == "B1").EstimatedCostRub.Should().Be(3000m);

        result.Skipped.Should().ContainSingle(
            s => s.Secid == "A2" && s.Reason == CashAllocationSkipReason.ConcentrationLimit,
            "A2 доходнее B1, но сектор 'Корп' уже занял отведённые 30% — деньги честно уходят в другой сектор");

        result.LeftoverRub.Should().Be(4000m);
    }

    [Fact]
    public void Allocate_SectorAxisWithoutMaxSharePercent_Throws()
    {
        var act = () => CashAllocationService.Allocate(
            1000m, [], currentPortfolioValueRub: 0m, concentrationAxis: CashAllocationConcentrationAxis.Sector);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Allocate_MaxLotsPerCandidate_CapsQuantityToOne_RegardlessOfLeftoverMoney()
    {
        // Задача 34 часть D.4 — "1 лот на secid": ось None (source=market) без лимита концентрации,
        // но maxLotsPerCandidate=1 всё равно останавливает каждого кандидата после первого лота, даже
        // если денег и лимита хватило бы на больше.
        var candidates = new[]
        {
            SectorCandidate("A1", "Корп", yield: 0.20m, price: 1000m),
            SectorCandidate("B1", "Гос", yield: 0.10m, price: 1000m),
        };

        var result = CashAllocationService.Allocate(
            amountRub: 10_000m,
            candidates,
            currentPortfolioValueRub: 0m,
            concentrationAxis: CashAllocationConcentrationAxis.None,
            maxLotsPerCandidate: 1);

        result.Allocations.Should().HaveCount(2);
        result.Allocations.Should().OnlyContain(a => a.Quantity == 1m);
        result.Allocations.Single(a => a.Secid == "A1").EstimatedCostRub.Should().Be(1000m);
        result.Allocations.Single(a => a.Secid == "B1").EstimatedCostRub.Should().Be(1000m);
        result.LeftoverRub.Should().Be(8000m, "оба кандидата получили ровно по 1 лоту, несмотря на 8000 оставшихся денег");
    }

    [Fact]
    public void Allocate_NoneAxis_NotEnoughMoneyForOneLot_SkippedAsInsufficientFunds_NotConcentrationLimit()
    {
        // Задача 34 — ось None не имеет лимита концентрации вообще, поэтому нулевой исход может быть
        // ТОЛЬКО нехваткой денег: причина обязана честно отличаться от ConcentrationLimit (см.
        // doc-comment CashAllocationSkipReason.InsufficientFunds) — иначе market-ответ лгал бы
        // пользователю про несуществующий лимит.
        var candidates = new[]
        {
            SectorCandidate("A1", "Корп", yield: 0.20m, price: 10_000m),
        };

        var result = CashAllocationService.Allocate(
            amountRub: 500m,
            candidates,
            currentPortfolioValueRub: 0m,
            concentrationAxis: CashAllocationConcentrationAxis.None,
            maxLotsPerCandidate: 1);

        result.Allocations.Should().BeEmpty();
        result.Skipped.Should().ContainSingle(s => s.Secid == "A1" && s.Reason == CashAllocationSkipReason.InsufficientFunds);
    }

    [Fact]
    public void Allocate_IssuerAxisExplicit_SameResultAsDefault_RegressionForSourcePortfolio()
    {
        // Задача 34 часть D.2 — явная передача concentrationAxis=Issuer (значение по умолчанию) не
        // меняет поведение source=portfolio ни на йоту: тот же сценарий, что
        // Allocate_ConcentrationLimit_StopsLeaderAndFillsSecondCandidate, вызванный с новой сигнатурой.
        var candidates = new[]
        {
            Candidate(1, 0.20m, pricePerLotRub: 10_000m, issuer: "A", currentIssuerMarketValueRub: 240_000m),
            Candidate(2, 0.15m, pricePerLotRub: 10_000m, issuer: "B", currentIssuerMarketValueRub: 0m),
        };

        var result = CashAllocationService.Allocate(
            amountRub: 20_000m,
            candidates,
            currentPortfolioValueRub: 250_000m,
            maxIssuerSharePercent: 25m,
            concentrationAxis: CashAllocationConcentrationAxis.Issuer);

        result.Skipped.Should().ContainSingle(s => s.InstrumentId == 1 && s.Reason == CashAllocationSkipReason.ConcentrationLimit);
        result.Allocations.Should().ContainSingle(a => a.InstrumentId == 2);
        result.Allocations[0].EstimatedCostRub.Should().Be(20_000m);
        result.LeftoverRub.Should().Be(0m);
    }
}
