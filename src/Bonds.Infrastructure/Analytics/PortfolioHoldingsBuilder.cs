using Bonds.Core.Analytics;
using Bonds.Core.Calculation;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;

namespace Bonds.Infrastructure.Analytics;

/// <summary>
/// Связующее звено между репозиториями (этап 03) и чистыми аналитическими сервисами
/// (<see cref="PortfolioCompositionService"/>, <see cref="PositionComparisonService"/>,
/// <see cref="SwitchAnalysisService"/>, <see cref="BondMetricsCalculator"/>) — этап 08
/// явно называет этот пробел: "между репозиториями и аналитическими сервисами сейчас нет
/// готового сборщика". Для каждой позиции счёта достаёт Instrument + CouponSchedule +
/// AmortizationSchedule + OfferSchedule + последнюю MarketQuote + актуальный
/// YieldCurveSnapshot, считает <see cref="BondMetrics"/> через чистый движок (этап 05) и
/// возвращает список <see cref="PortfolioHolding"/> — общий вход для всех аналитических
/// эндпоинтов этапа 08 (positions/composition/scatter/comparison/replacement/cashflow-метрик).
/// <para>
/// Логика расчёта рыночной стоимости и метрик зеркалит <c>SyncCycleService.RunSignalsAsync</c>
/// (этап 07) — это не дублирование по ошибке, а осознанное переиспользование уже проверенного
/// паттерна сборки; вынесено в отдельный публичный класс именно потому, что теперь нужно
/// несколько независимых потребителей (HTTP-эндпоинты), а не только цикл синка.
/// </para>
/// <para>
/// <b>Cost basis (plan/14 §B):</b> <see cref="PortfolioHolding.CostBasis"/> считается через
/// чистый <see cref="PositionCostBasisService"/> поверх журнала <see cref="Operation"/>.
/// В <see cref="BuildForAccountAsync"/> журнал читается ОДНИМ батч-запросом на весь счёт
/// (<see cref="IOperationRepository.GetByAccountIdAsync"/>) и группируется по InstrumentId в
/// памяти — не N+1 (план явно требует не дёргать репозиторий операций в цикле по позициям).
/// </para>
/// </summary>
public sealed class PortfolioHoldingsBuilder
{
    private readonly IPositionRepository _positions;
    private readonly IInstrumentRepository _instruments;
    private readonly ICouponScheduleRepository _coupons;
    private readonly IAmortizationScheduleRepository _amortizations;
    private readonly IOfferScheduleRepository _offers;
    private readonly IMarketQuoteRepository _quotes;
    private readonly IYieldCurveRepository _yieldCurve;
    private readonly IOperationRepository _operations;

    public PortfolioHoldingsBuilder(
        IPositionRepository positions,
        IInstrumentRepository instruments,
        ICouponScheduleRepository coupons,
        IAmortizationScheduleRepository amortizations,
        IOfferScheduleRepository offers,
        IMarketQuoteRepository quotes,
        IYieldCurveRepository yieldCurve,
        IOperationRepository operations)
    {
        _positions = positions;
        _instruments = instruments;
        _coupons = coupons;
        _amortizations = amortizations;
        _offers = offers;
        _quotes = quotes;
        _yieldCurve = yieldCurve;
        _operations = operations;
    }

    /// <summary>Holdings для всех позиций счёта (composition/comparison/scatter/replacement/positions — этап 08, plan/14).</summary>
    public async Task<IReadOnlyList<PortfolioHolding>> BuildForAccountAsync(ulong accountId, DateOnly asOf, CancellationToken ct = default)
    {
        var positions = (await _positions.GetByAccountIdAsync(accountId)).ToList();
        var curve = await _yieldCurve.GetLatestAsync();

        // Один запрос на весь журнал счёта (plan/14 §B — "не N+1"), сгруппированный по инструменту.
        var operationsByInstrument = (await _operations.GetByAccountIdAsync(accountId))
            .Where(op => op.InstrumentId.HasValue)
            .GroupBy(op => op.InstrumentId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Operation>)g.ToList());

        var holdings = new List<PortfolioHolding>(positions.Count);
        foreach (var position in positions)
        {
            ct.ThrowIfCancellationRequested();
            var journal = operationsByInstrument.TryGetValue(position.InstrumentId, out var ops)
                ? ops
                : Array.Empty<Operation>();
            var holding = await BuildOneAsync(position, asOf, curve, journal, ct);
            if (holding is not null) holdings.Add(holding);
        }

        return holdings;
    }

    /// <summary>
    /// Задача 20 (watchlist): holdings для бумаг БЕЗ позиции — тот же расчётный путь, что у
    /// <see cref="BuildForAccountAsync"/> (инструмент + расписания + котировка + Gcurve →
    /// <see cref="BondMetricsCalculator"/>), но без Position/Operation (watchlist-запись не связана
    /// со счётом). Quantity фиксируется в 1 (одна бумага) и MarketValueRub = грязная цена ОДНОЙ
    /// бумаги — так «цена лота» для allocation-эндпоинта (<c>MarketValueRub / Quantity</c>, та же
    /// формула, что и для обычных holdings) считается без специального ветвления. CostBasis не
    /// имеет смысла вне портфеля — всегда null. PositionId=0 — синтетическое значение, отличающее
    /// watchlist-запись от реальной позиции; сами эндпоинты watchlist читают по InstrumentId.
    /// </summary>
    public async Task<IReadOnlyList<PortfolioHolding>> BuildForInstrumentsAsync(
        IReadOnlyCollection<ulong> instrumentIds, DateOnly asOf, CancellationToken ct = default)
    {
        if (instrumentIds.Count == 0) return [];

        var curve = await _yieldCurve.GetLatestAsync();
        var holdings = new List<PortfolioHolding>(instrumentIds.Count);

        foreach (var instrumentId in instrumentIds)
        {
            ct.ThrowIfCancellationRequested();
            var instrument = await _instruments.GetByIdAsync(instrumentId);
            if (instrument is null) continue; // ссылочная целостность нарушена — пропускаем, не падаем

            var coupons = (await _coupons.GetByInstrumentIdAsync(instrumentId)).ToList();
            var amortizations = (await _amortizations.GetByInstrumentIdAsync(instrumentId)).ToList();
            var offers = (await _offers.GetByInstrumentIdAsync(instrumentId)).ToList();
            var quote = await _quotes.GetLatestAsync(instrumentId);

            var syntheticPosition = new Position
            {
                InstrumentId = instrumentId,
                Quantity = 1m,
                Accrued = quote?.Accrued ?? 0m,
                DataIncomplete = instrument.DataIncomplete,
            };

            var metrics = CalculateMetrics(instrument, syntheticPosition, coupons, amortizations, offers, quote, curve, asOf);
            var pricePerUnitRub = quote?.DirtyPrice ?? metrics.DirtyPrice;

            holdings.Add(new PortfolioHolding
            {
                PositionId = 0,
                InstrumentId = instrument.Id,
                Quantity = 1m,
                MarketValueRub = pricePerUnitRub,
                Name = instrument.Name,
                Isin = instrument.Isin,
                Issuer = instrument.Issuer,
                Sector = instrument.Sector,
                CouponType = instrument.CouponType,
                MaturityDate = instrument.MaturityDate,
                HorizonDate = metrics.HorizonDate,
                IsCalculatedToOffer = metrics.CalculatedToOffer,
                ModifiedDuration = metrics.ModifiedDuration,
                MacaulayDuration = metrics.MacaulayDuration,
                Convexity = metrics.Convexity,
                YtmEffective = metrics.YtmEffective,
                CurrentYield = metrics.CurrentYield,
                GSpread = metrics.GSpread,
                IsFloater = metrics.IsFloater,
                IsIndexed = metrics.IsIndexed,
                IsEstimated = metrics.IsEstimated,
                DataIncomplete = metrics.DataIncomplete,
                IsOutOfScopeCurrency = instrument.IsOutOfScopeCurrency,
                CostBasis = null,
            });
        }

        return holdings;
    }

    /// <summary>Holding одной позиции (GET /api/positions/{id}) — null, если ссылочная целостность нарушена (инструмент не найден).</summary>
    public async Task<(PortfolioHolding Holding, Instrument Instrument, BondMetrics Metrics)?> BuildDetailedAsync(
        Position position, DateOnly asOf, CancellationToken ct = default)
    {
        var curve = await _yieldCurve.GetLatestAsync();
        var instrument = await _instruments.GetByIdAsync(position.InstrumentId);
        if (instrument is null) return null;

        var coupons = (await _coupons.GetByInstrumentIdAsync(position.InstrumentId)).ToList();
        var amortizations = (await _amortizations.GetByInstrumentIdAsync(position.InstrumentId)).ToList();
        var offers = (await _offers.GetByInstrumentIdAsync(position.InstrumentId)).ToList();
        var quote = await _quotes.GetLatestAsync(position.InstrumentId);
        var journal = (await _operations.GetByAccountIdAsync(position.AccountId))
            .Where(op => op.InstrumentId == position.InstrumentId)
            .ToList();

        var metrics = CalculateMetrics(instrument, position, coupons, amortizations, offers, quote, curve, asOf);
        var holding = ToHolding(position, instrument, metrics, quote, journal);

        return (holding, instrument, metrics);
    }

    private async Task<PortfolioHolding?> BuildOneAsync(
        Position position, DateOnly asOf, YieldCurveSnapshot? curve, IReadOnlyList<Operation> journal, CancellationToken ct)
    {
        var instrument = await _instruments.GetByIdAsync(position.InstrumentId);
        if (instrument is null) return null; // нарушена ссылочная целостность — пропускаем, не падаем (та же устойчивость, что у SyncCycleService)

        var coupons = (await _coupons.GetByInstrumentIdAsync(position.InstrumentId)).ToList();
        var amortizations = (await _amortizations.GetByInstrumentIdAsync(position.InstrumentId)).ToList();
        var offers = (await _offers.GetByInstrumentIdAsync(position.InstrumentId)).ToList();
        var quote = await _quotes.GetLatestAsync(position.InstrumentId);

        var metrics = CalculateMetrics(instrument, position, coupons, amortizations, offers, quote, curve, asOf);
        return ToHolding(position, instrument, metrics, quote, journal);
    }

    private static BondMetrics CalculateMetrics(
        Instrument instrument,
        Position position,
        List<CouponSchedule> coupons,
        List<AmortizationSchedule> amortizations,
        List<OfferSchedule> offers,
        MarketQuote? quote,
        YieldCurveSnapshot? curve,
        DateOnly asOf)
    {
        var input = new BondMetricsCalculatorInput
        {
            InstrumentId = instrument.Id,
            AsOf = asOf,
            FaceValue = instrument.FaceValue,
            MaturityDate = instrument.MaturityDate,
            CouponType = instrument.CouponType,
            HasAmortization = instrument.HasAmortization,
            HasOffers = instrument.HasOffers,
            DataIncomplete = instrument.DataIncomplete || position.DataIncomplete,
            IsOutOfScopeCurrency = instrument.IsOutOfScopeCurrency,
            CleanPrice = quote?.CleanPrice,
            AccruedInterestFromSource = quote?.Accrued ?? position.Accrued,
            Coupons = coupons,
            Amortizations = amortizations,
            Offers = offers,
            CurveSnapshot = curve,
        };

        return BondMetricsCalculator.Calculate(input);
    }

    /// <summary>
    /// Рыночная стоимость holding'а — DirtyPrice из котировки приоритетнее (включает фактический
    /// НКД источника), фолбэк на DirtyPrice, посчитанный движком (см. doc-comment SyncCycleService
    /// про разную семантику деградации у разных потребителей — здесь, как и в Signals, позиция без
    /// котировки получает MarketValue=0, а не исключается из списка).
    /// </summary>
    private static PortfolioHolding ToHolding(
        Position position, Instrument instrument, BondMetrics metrics, MarketQuote? quote, IReadOnlyList<Operation> journal)
    {
        var marketValue = (quote?.DirtyPrice ?? metrics.DirtyPrice) * position.Quantity;
        var costBasis = PositionCostBasisService.Calculate(journal, position.Quantity, marketValue);

        return new PortfolioHolding
        {
            PositionId = position.Id,
            InstrumentId = instrument.Id,
            Quantity = position.Quantity,
            MarketValueRub = marketValue,
            Name = instrument.Name,
            Isin = instrument.Isin,
            Issuer = instrument.Issuer,
            Sector = instrument.Sector,
            CouponType = instrument.CouponType,
            MaturityDate = instrument.MaturityDate,
            HorizonDate = metrics.HorizonDate,
            IsCalculatedToOffer = metrics.CalculatedToOffer,
            ModifiedDuration = metrics.ModifiedDuration,
            MacaulayDuration = metrics.MacaulayDuration,
            Convexity = metrics.Convexity,
            YtmEffective = metrics.YtmEffective,
            CurrentYield = metrics.CurrentYield,
            GSpread = metrics.GSpread,
            IsFloater = metrics.IsFloater,
            IsIndexed = metrics.IsIndexed,
            IsEstimated = metrics.IsEstimated,
            DataIncomplete = metrics.DataIncomplete,
            IsOutOfScopeCurrency = instrument.IsOutOfScopeCurrency,
            CostBasis = costBasis,
        };
    }
}
