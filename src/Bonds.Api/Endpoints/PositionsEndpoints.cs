using System.Security.Claims;
using Bonds.Api.Middleware;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
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

        var asOf = DateOnly.FromDateTime(DateTime.UtcNow);
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
        ClaimsPrincipal principal,
        IAccountRepository accountRepo,
        IPositionRepository positionRepo,
        PortfolioHoldingsBuilder holdingsBuilder)
    {
        var accountId = await ResolveAccountIdAsync(principal, accountRepo);
        if (accountId is null) throw new NotFoundException("Позиция не найдена");

        var position = await positionRepo.GetByIdAsync(id, accountId.Value);
        if (position is null) throw new NotFoundException($"Позиция {id} не найдена");

        var asOf = DateOnly.FromDateTime(DateTime.UtcNow);
        var built = await holdingsBuilder.BuildDetailedAsync(position, asOf);
        if (built is null) throw new NotFoundException($"Инструмент по позиции {id} не найден");

        var (holding, instrument, metrics) = built.Value;

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
            Disclaimer = Disclaimers.Metrics,
        };

        return Results.Ok(dto);
    }

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
    public required string Disclaimer { get; init; }
}
