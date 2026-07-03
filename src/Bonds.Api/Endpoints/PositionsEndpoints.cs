using System.Security.Claims;
using Bonds.Api.Middleware;
using Bonds.Core.Analytics;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Core.Time;
using Bonds.Infrastructure.Analytics;

namespace Bonds.Api.Endpoints;

/// <summary>
/// GET /api/positions — таблица позиций с метриками (plan/08, spec §9 "Таблица позиций").
/// GET /api/positions/{id} — детальная карточка позиции/инструмента (расписания, метрики, флаги).
/// Holdings собираются через <see cref="PortfolioHoldingsBuilder"/> (этап 08 — сборщик,
/// закрывающий пробел между репозиториями и аналитическими сервисами).
/// </summary>
public static class PositionsEndpoints
{
    public static void MapPositionsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/positions", GetPositions);
        app.MapGet("/api/positions/{id}", GetPositionById);
    }

    private static async Task<IResult> GetPositions(
        ClaimsPrincipal principal,
        IAccountRepository accountRepo,
        PortfolioHoldingsBuilder holdingsBuilder)
    {
        var accountId = await ResolveAccountIdAsync(principal, accountRepo);
        if (accountId is null) return Results.Ok(new PositionsResponseDto { Positions = [], Disclaimer = Disclaimers.Metrics });

        var asOf = BusinessClock.MoscowToday();
        var holdings = await holdingsBuilder.BuildForAccountAsync(accountId.Value, asOf);

        var dto = new PositionsResponseDto
        {
            Positions = holdings.Select(ToRowDto).ToList(),
            Disclaimer = Disclaimers.Metrics,
        };

        return Results.Ok(dto);
    }

    private static async Task<IResult> GetPositionById(
        ulong id,
        string? range,
        ClaimsPrincipal principal,
        IAccountRepository accountRepo,
        IPositionRepository positionRepo,
        IInstrumentRepository instrumentRepo,
        ICouponScheduleRepository couponRepo,
        IAmortizationScheduleRepository amortizationRepo,
        IOfferScheduleRepository offerRepo,
        IOperationRepository operationRepo,
        PortfolioHoldingsBuilder holdingsBuilder,
        InstrumentPriceHistoryService priceHistoryService)
    {
        var accountId = await ResolveAccountIdAsync(principal, accountRepo);
        if (accountId is null) throw new NotFoundException("Позиция не найдена");

        var position = await positionRepo.GetByIdAsync(id, accountId.Value);
        if (position is null) throw new NotFoundException($"Позиция {id} не найдена");

        var asOf = BusinessClock.MoscowToday();
        var built = await holdingsBuilder.BuildDetailedAsync(position, asOf);
        if (built is null) throw new NotFoundException($"Инструмент по позиции {id} не найден");

        var (holding, instrument, metrics) = built.Value;

        // plan/19 §A.1: график цены — кэш поверх MOEX ISS history, дозагружается только хвост.
        var from = RangeStartDate(range, asOf, instrument.MaturityDate);
        var priceHistory = await priceHistoryService.GetOrRefreshAsync(
            instrument.Id, instrument.Isin, instrument.Secid, from, asOf);

        // plan/19 §A.2: календарь бумаги — купоны/амортизации/оферты, уже загруженные для метрик
        // (holdingsBuilder.BuildDetailedAsync читает их сам, но не отдаёт наружу) — читаем ещё раз
        // напрямую из репозиториев (дешёвые point-запросы по одному instrumentId, не N+1 по счёту).
        var coupons = (await couponRepo.GetByInstrumentIdAsync(instrument.Id))
            .OrderBy(c => c.CouponDate)
            .ToList();
        var amortizations = (await amortizationRepo.GetByInstrumentIdAsync(instrument.Id))
            .OrderBy(a => a.Date)
            .ToList();
        var offers = (await offerRepo.GetByInstrumentIdAsync(instrument.Id))
            .OrderBy(o => o.Date)
            .ToList();

        // plan/19 §A.3: журнал операций пользователя по этому инструменту.
        var operations = (await operationRepo.GetByAccountIdAsync(accountId.Value))
            .Where(op => op.InstrumentId == instrument.Id)
            .OrderByDescending(op => op.Date)
            .ToList();

        // plan/19 §A.4: «если продать сейчас» — выручка минус комиссия (+P&L при известном cost basis).
        var ifSoldNow = IfSoldNowService.Calculate(holding.MarketValueRub, holding.CostBasis);

        var dto = new PositionDetailDto
        {
            PositionId = position.Id,
            InstrumentId = instrument.Id,
            Isin = instrument.Isin,
            Name = instrument.Name,
            Issuer = instrument.Issuer,
            Sector = instrument.Sector,
            Quantity = position.Quantity,
            FaceValue = instrument.FaceValue,
            Currency = instrument.Currency,
            CouponType = instrument.CouponType.ToString(),
            MaturityDate = instrument.MaturityDate,
            HorizonDate = holding.HorizonDate,
            CalculatedToOffer = holding.IsCalculatedToOffer,
            HasAmortization = instrument.HasAmortization,
            HasOffers = instrument.HasOffers,
            CleanPrice = metrics.CleanPrice,
            AccruedInterest = metrics.AccruedInterest,
            DirtyPrice = metrics.DirtyPrice,
            MarketValueRub = holding.MarketValueRub,
            YtmEffective = metrics.YtmEffective,
            YtmSimple = metrics.YtmSimple,
            CurrentYield = metrics.CurrentYield,
            MacaulayDuration = metrics.MacaulayDuration,
            ModifiedDuration = metrics.ModifiedDuration,
            Convexity = metrics.Convexity,
            Pvbp = metrics.Pvbp,
            GSpread = metrics.GSpread,
            IsFloater = metrics.IsFloater,
            IsIndexed = metrics.IsIndexed,
            IsEstimated = metrics.IsEstimated,
            DataIncomplete = metrics.DataIncomplete,
            IsOutOfScopeCurrency = instrument.IsOutOfScopeCurrency,
            Notes = metrics.Notes,
            AverageCostRub = holding.CostBasis?.AverageCostRub,
            InvestedRub = holding.CostBasis?.InvestedRub,
            UnrealizedPnlRub = holding.CostBasis?.UnrealizedPnlRub,
            UnrealizedPnlPercent = holding.CostBasis?.UnrealizedPnlPercent,
            CouponsReceivedRub = holding.CostBasis?.CouponsReceivedRub,
            TotalReturnPercent = holding.CostBasis?.TotalReturnPercent,
            CostBasisIncomplete = holding.CostBasis?.HasUnknownLots ?? false,
            PriceHistory = priceHistory.Select(p => new PriceHistoryPointDto
            {
                Date = p.Date,
                ClosePricePercent = p.ClosePricePercent,
                AccruedInterestRub = p.AccruedInterestRub,
            }).ToList(),
            CouponSchedule = coupons.Select(c => new CouponScheduleItemDto
            {
                CouponDate = c.CouponDate,
                ValueRub = c.ValueRub,
                ValueForPositionRub = c.ValueRub.HasValue ? c.ValueRub.Value * position.Quantity : null,
                IsKnown = c.IsKnown,
                IsPast = c.CouponDate <= asOf,
            }).ToList(),
            AmortizationSchedule = amortizations.Select(a => new AmortizationScheduleItemDto
            {
                Date = a.Date,
                AmountRub = a.AmountRub,
                AmountForPositionRub = a.AmountRub * position.Quantity,
                IsPast = a.Date <= asOf,
            }).ToList(),
            OfferSchedule = offers.Select(o => new OfferScheduleItemDto
            {
                Date = o.Date,
                OfferType = o.OfferType.ToString(),
                IsExecuted = o.IsExecuted,
                IsPast = o.Date <= asOf,
            }).ToList(),
            Operations = operations.Select(op => new OperationItemDto
            {
                Id = op.Id,
                Type = op.Type.ToString(),
                Date = op.Date,
                AmountRub = op.AmountRub,
                Quantity = op.Quantity,
            }).ToList(),
            IfSoldNow = new IfSoldNowDto
            {
                MarketValueRub = ifSoldNow.MarketValueRub,
                CommissionRub = ifSoldNow.CommissionRub,
                CommissionRate = ifSoldNow.CommissionRate,
                NetProceedsRub = ifSoldNow.NetProceedsRub,
                RealizedPnlRub = ifSoldNow.RealizedPnlRub,
                RealizedPnlPercent = ifSoldNow.RealizedPnlPercent,
                CouponsReceivedRub = ifSoldNow.CouponsReceivedRub,
                TotalReturnWithCouponsRub = ifSoldNow.TotalReturnWithCouponsRub,
                PnlAvailable = ifSoldNow.PnlAvailable,
                Disclaimer = ifSoldNow.Disclaimer,
            },
            Disclaimer = Disclaimers.Metrics,
        };

        return Results.Ok(dto);
    }

    /// <summary>
    /// Начало окна графика цены по параметру `range` (plan/19 §B.2: 1м/6м/1г/всё). "Всё" — от
    /// сегодняшней даты минус 10 лет ИЛИ от даты погашения минус 10 лет, если бумага короче
    /// (не уходим в бесконечное прошлое без причины — MOEX history всё равно не хранит вечность,
    /// а более длинный запрос — просто больше сетевого трафика без пользы для UI-графика).
    /// Неизвестное/отсутствующее значение `range` — дефолт "6m" (безопасная деградация).
    /// </summary>
    private static DateOnly RangeStartDate(string? range, DateOnly asOf, DateOnly maturityDate)
    {
        return range switch
        {
            "1m" => asOf.AddMonths(-1),
            "1y" => asOf.AddYears(-1),
            "all" => MinDate(asOf.AddYears(-10), maturityDate),
            _ => asOf.AddMonths(-6), // "6m" и любое нераспознанное значение
        };
    }

    private static DateOnly MinDate(DateOnly a, DateOnly b) => a < b ? a : b;

    private static PositionRowDto ToRowDto(Core.Analytics.PortfolioHolding h) => new()
    {
        PositionId = h.PositionId,
        InstrumentId = h.InstrumentId,
        Name = h.Name,
        Isin = h.Isin,
        Issuer = h.Issuer,
        Sector = h.Sector,
        Quantity = h.Quantity,
        MarketValueRub = h.MarketValueRub,
        CouponType = h.CouponType.ToString(),
        MaturityDate = h.MaturityDate,
        HorizonDate = h.HorizonDate,
        CalculatedToOffer = h.IsCalculatedToOffer,
        YtmEffective = h.YtmEffective,
        CurrentYield = h.CurrentYield,
        ModifiedDuration = h.ModifiedDuration,
        GSpread = h.GSpread,
        IsFloater = h.IsFloater,
        IsIndexed = h.IsIndexed,
        IsEstimated = h.IsEstimated,
        DataIncomplete = h.DataIncomplete,
        IsOutOfScopeCurrency = h.IsOutOfScopeCurrency,
        AverageCostRub = h.CostBasis?.AverageCostRub,
        InvestedRub = h.CostBasis?.InvestedRub,
        UnrealizedPnlRub = h.CostBasis?.UnrealizedPnlRub,
        UnrealizedPnlPercent = h.CostBasis?.UnrealizedPnlPercent,
        CouponsReceivedRub = h.CostBasis?.CouponsReceivedRub,
        TotalReturnPercent = h.CostBasis?.TotalReturnPercent,
        CostBasisIncomplete = h.CostBasis?.HasUnknownLots ?? false,
    };

    /// <summary>
    /// Резолвит AccountId владельца запроса: UserId из JWT → IAccountRepository.GetByUserIdAsync
    /// (первый/единственный счёт, single-user продукт — spec §2). Null, если у пользователя ещё
    /// нет ни одного счёта (чистая инсталляция до первого синка) — вызывающий код должен
    /// деградировать на пустой ответ, не на 500.
    /// </summary>
    internal static async Task<ulong?> ResolveAccountIdAsync(ClaimsPrincipal principal, IAccountRepository accountRepo)
    {
        var sub = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!ulong.TryParse(sub, out var userId)) return null;

        var accounts = await accountRepo.GetByUserIdAsync(userId);
        return accounts.Select(a => a.Id).OrderBy(id => id).FirstOrDefault();
    }
}

/// <summary>Дисклеймеры, общие для нескольких эндпоинтов (spec §6/§11 — "все расчёты — аналитические оценки, не рекомендации").</summary>
internal static class Disclaimers
{
    public const string Metrics =
        "Все расчётные метрики (YTM, дюрация, G-спред и т.д.) — аналитические оценки, не инвестиционные рекомендации.";
}

public sealed record PositionsResponseDto
{
    public required IReadOnlyList<PositionRowDto> Positions { get; init; }
    public required string Disclaimer { get; init; }
}

/// <summary>Строка таблицы позиций (spec §9: доходность/дюрация/G-спред/срок/тип купона/флаги).</summary>
public sealed record PositionRowDto
{
    public required ulong PositionId { get; init; }
    public required ulong InstrumentId { get; init; }
    public string? Name { get; init; }
    public string? Isin { get; init; }
    public string? Issuer { get; init; }
    public string? Sector { get; init; }
    public required decimal Quantity { get; init; }
    public required decimal MarketValueRub { get; init; }
    public string CurrencyRub => "RUB";
    public required string CouponType { get; init; }
    public required DateOnly MaturityDate { get; init; }
    public required DateOnly HorizonDate { get; init; }
    public required bool CalculatedToOffer { get; init; }
    public decimal? YtmEffective { get; init; }
    public decimal? CurrentYield { get; init; }
    public decimal? ModifiedDuration { get; init; }
    public decimal? GSpread { get; init; }
    public required bool IsFloater { get; init; }
    public required bool IsIndexed { get; init; }
    public required bool IsEstimated { get; init; }
    public required bool DataIncomplete { get; init; }

    /// <summary>§11: номинал в иностранной валюте — вне рублёвого контура MVP (UI помечает бейджем).</summary>
    public bool IsOutOfScopeCurrency { get; init; }

    // ---- Цена входа / P&L "от цены входа" (plan/14 §A/§B) — null, если по журналу не посчитать. ----

    /// <summary>Средняя цена входа за бумагу (average cost, см. doc-comment PositionCostBasisService).</summary>
    public decimal? AverageCostRub { get; init; }

    /// <summary>Вложено в текущий остаток = AverageCostRub × Quantity.</summary>
    public decimal? InvestedRub { get; init; }

    /// <summary>Текущая рыночная стоимость минус вложенное.</summary>
    public decimal? UnrealizedPnlRub { get; init; }

    /// <summary>Доля (0.12 = 12%) — форматтер фронта умножает на 100.</summary>
    public decimal? UnrealizedPnlPercent { get; init; }

    /// <summary>Сумма купонных операций по бумаге за всё время (не только за текущий остаток).</summary>
    public decimal? CouponsReceivedRub { get; init; }

    /// <summary>(UnrealizedPnlRub + CouponsReceivedRub) / InvestedRub — доля.</summary>
    public decimal? TotalReturnPercent { get; init; }

    /// <summary>True — журнал операций не покрывает весь текущий остаток (докуплено/продано до начала истории синка); метрики выше приблизительны.</summary>
    public required bool CostBasisIncomplete { get; init; }
}

/// <summary>Детальная карточка позиции/инструмента (GET /api/positions/{id}).</summary>
public sealed record PositionDetailDto
{
    public required ulong PositionId { get; init; }
    public required ulong InstrumentId { get; init; }
    public required string Isin { get; init; }
    public string? Name { get; init; }
    public string? Issuer { get; init; }
    public string? Sector { get; init; }
    public required decimal Quantity { get; init; }
    public required decimal FaceValue { get; init; }
    public required string Currency { get; init; }
    public required string CouponType { get; init; }
    public required DateOnly MaturityDate { get; init; }
    public required DateOnly HorizonDate { get; init; }
    public required bool CalculatedToOffer { get; init; }
    public required bool HasAmortization { get; init; }
    public required bool HasOffers { get; init; }

    public required decimal CleanPrice { get; init; }
    public required decimal AccruedInterest { get; init; }
    public required decimal DirtyPrice { get; init; }
    public required decimal MarketValueRub { get; init; }

    public decimal? YtmEffective { get; init; }
    public decimal? YtmSimple { get; init; }
    public decimal? CurrentYield { get; init; }
    public decimal? MacaulayDuration { get; init; }
    public decimal? ModifiedDuration { get; init; }
    public decimal? Convexity { get; init; }
    public decimal? Pvbp { get; init; }
    public decimal? GSpread { get; init; }

    public required bool IsFloater { get; init; }
    public required bool IsIndexed { get; init; }
    public required bool IsEstimated { get; init; }
    public required bool DataIncomplete { get; init; }

    /// <summary>§11: номинал в иностранной валюте — вне рублёвого контура MVP (UI помечает бейджем).</summary>
    public bool IsOutOfScopeCurrency { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }

    // ---- Цена входа / P&L "от цены входа" (plan/14) — null, если по журналу не посчитать. ----

    /// <summary>Средняя цена входа за бумагу (average cost, см. doc-comment PositionCostBasisService).</summary>
    public decimal? AverageCostRub { get; init; }

    /// <summary>Вложено в текущий остаток = AverageCostRub × Quantity.</summary>
    public decimal? InvestedRub { get; init; }

    /// <summary>Текущая рыночная стоимость минус вложенное.</summary>
    public decimal? UnrealizedPnlRub { get; init; }

    /// <summary>Доля (0.12 = 12%) — форматтер фронта умножает на 100.</summary>
    public decimal? UnrealizedPnlPercent { get; init; }

    /// <summary>Сумма купонных операций по бумаге за всё время (не только за текущий остаток).</summary>
    public decimal? CouponsReceivedRub { get; init; }

    /// <summary>(UnrealizedPnlRub + CouponsReceivedRub) / InvestedRub — доля.</summary>
    public decimal? TotalReturnPercent { get; init; }

    /// <summary>True — журнал операций не покрывает весь текущий остаток; метрики выше приблизительны.</summary>
    public required bool CostBasisIncomplete { get; init; }

    // ---- plan/19: график цены, календарь бумаги, журнал операций, «если продать сейчас». ----

    /// <summary>Дневная история цены за запрошенный `range` (plan/19 §A.1) — из кэша instrument_price_history.</summary>
    public required IReadOnlyList<PriceHistoryPointDto> PriceHistory { get; init; }

    /// <summary>Полный график купонов бумаги (прошедшие + будущие), сумма — на всю позицию.</summary>
    public required IReadOnlyList<CouponScheduleItemDto> CouponSchedule { get; init; }

    /// <summary>Полный график амортизаций бумаги, сумма — на всю позицию.</summary>
    public required IReadOnlyList<AmortizationScheduleItemDto> AmortizationSchedule { get; init; }

    /// <summary>Полный график оферт бумаги.</summary>
    public required IReadOnlyList<OfferScheduleItemDto> OfferSchedule { get; init; }

    /// <summary>Журнал операций пользователя по этому инструменту, новые сверху.</summary>
    public required IReadOnlyList<OperationItemDto> Operations { get; init; }

    /// <summary>«Если продать сейчас» — выручка минус комиссия (+P&L при известном cost basis, plan/14).</summary>
    public required IfSoldNowDto IfSoldNow { get; init; }

    public required string Disclaimer { get; init; }
}

/// <summary>Одна дневная точка графика цены (plan/19 §A.1/§B.2).</summary>
public sealed record PriceHistoryPointDto
{
    public required DateOnly Date { get; init; }

    /// <summary>Цена закрытия, % от номинала. Null — торгов в этот день не было.</summary>
    public decimal? ClosePricePercent { get; init; }
    public decimal? AccruedInterestRub { get; init; }
}

/// <summary>Один купон в календаре бумаги (plan/19 §A.2/§B.4).</summary>
public sealed record CouponScheduleItemDto
{
    public required DateOnly CouponDate { get; init; }

    /// <summary>Размер купона на один номинал. Null — неизвестен (флоатер, далёкий горизонт).</summary>
    public decimal? ValueRub { get; init; }

    /// <summary>ValueRub × количество бумаг в позиции. Null, если ValueRub неизвестен.</summary>
    public decimal? ValueForPositionRub { get; init; }
    public required bool IsKnown { get; init; }
    public required bool IsPast { get; init; }
}

/// <summary>Одна амортизация в календаре бумаги.</summary>
public sealed record AmortizationScheduleItemDto
{
    public required DateOnly Date { get; init; }
    public required decimal AmountRub { get; init; }
    public required decimal AmountForPositionRub { get; init; }
    public required bool IsPast { get; init; }
}

/// <summary>Одна оферта в календаре бумаги.</summary>
public sealed record OfferScheduleItemDto
{
    public required DateOnly Date { get; init; }
    public required string OfferType { get; init; }
    public required bool IsExecuted { get; init; }
    public required bool IsPast { get; init; }
}

/// <summary>Одна операция журнала пользователя по инструменту (plan/19 §A.3/§B.5).</summary>
public sealed record OperationItemDto
{
    public required ulong Id { get; init; }
    public required string Type { get; init; }
    public required DateTime Date { get; init; }
    public required decimal AmountRub { get; init; }
    public decimal? Quantity { get; init; }
}

/// <summary>«Если продать сейчас» (plan/19 §A.4) — см. doc-comment IfSoldNowService.</summary>
public sealed record IfSoldNowDto
{
    public required decimal MarketValueRub { get; init; }
    public required decimal CommissionRub { get; init; }
    public required decimal CommissionRate { get; init; }
    public required decimal NetProceedsRub { get; init; }
    public decimal? RealizedPnlRub { get; init; }
    public decimal? RealizedPnlPercent { get; init; }
    public decimal? CouponsReceivedRub { get; init; }
    public decimal? TotalReturnWithCouponsRub { get; init; }
    public required bool PnlAvailable { get; init; }
    public required string Disclaimer { get; init; }
}
