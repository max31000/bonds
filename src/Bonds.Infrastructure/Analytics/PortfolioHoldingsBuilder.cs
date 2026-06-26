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

    public PortfolioHoldingsBuilder(
        IPositionRepository positions,
        IInstrumentRepository instruments,
        ICouponScheduleRepository coupons,
        IAmortizationScheduleRepository amortizations,
        IOfferScheduleRepository offers,
        IMarketQuoteRepository quotes,
        IYieldCurveRepository yieldCurve)
    {
        _positions = positions;
        _instruments = instruments;
        _coupons = coupons;
        _amortizations = amortizations;
        _offers = offers;
        _quotes = quotes;
        _yieldCurve = yieldCurve;
    }

    /// <summary>Holdings для всех позиций счёта (composition/comparison/scatter/replacement — этап 08).</summary>
    public async Task<IReadOnlyList<PortfolioHolding>> BuildForAccountAsync(ulong accountId, DateOnly asOf, CancellationToken ct = default)
    {
        var positions = (await _positions.GetByAccountIdAsync(accountId)).ToList();
        var curve = await _yieldCurve.GetLatestAsync();

        var holdings = new List<PortfolioHolding>(positions.Count);
        foreach (var position in positions)
        {
            ct.ThrowIfCancellationRequested();
            var holding = await BuildOneAsync(position, asOf, curve, ct);
            if (holding is not null) holdings.Add(holding);
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

        var metrics = CalculateMetrics(instrument, position, coupons, amortizations, offers, quote, curve, asOf);
        var holding = ToHolding(position, instrument, metrics, quote);

        return (holding, instrument, metrics);
    }

    private async Task<PortfolioHolding?> BuildOneAsync(Position position, DateOnly asOf, YieldCurveSnapshot? curve, CancellationToken ct)
    {
        var instrument = await _instruments.GetByIdAsync(position.InstrumentId);
        if (instrument is null) return null; // нарушена ссылочная целостность — пропускаем, не падаем (та же устойчивость, что у SyncCycleService)

        var coupons = (await _coupons.GetByInstrumentIdAsync(position.InstrumentId)).ToList();
        var amortizations = (await _amortizations.GetByInstrumentIdAsync(position.InstrumentId)).ToList();
        var offers = (await _offers.GetByInstrumentIdAsync(position.InstrumentId)).ToList();
        var quote = await _quotes.GetLatestAsync(position.InstrumentId);

        var metrics = CalculateMetrics(instrument, position, coupons, amortizations, offers, quote, curve, asOf);
        return ToHolding(position, instrument, metrics, quote);
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
    private static PortfolioHolding ToHolding(Position position, Instrument instrument, BondMetrics metrics, MarketQuote? quote)
    {
        var marketValue = (quote?.DirtyPrice ?? metrics.DirtyPrice) * position.Quantity;

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
        };
    }
}
