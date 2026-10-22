using Bonds.Api.Auth;
using Bonds.Api.Middleware;
using Bonds.Core.Analytics;
using Bonds.Core.Calculation;
using Bonds.Core.CashFlow;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Interfaces;
using Bonds.Core.Models;
using Bonds.Core.Time;
using Bonds.Core.Universe;
using Bonds.Infrastructure.Analytics;
using Bonds.Infrastructure.Universe;
using Microsoft.Extensions.Options;
using static Bonds.Api.Endpoints.AnalyticsSupport;
using static Bonds.Core.Analytics.RelativeValueService;

namespace Bonds.Api.Endpoints;

public sealed record CompositionResponseDto
{
    public required decimal TotalMarketValueRub { get; init; }
    public required IReadOnlyList<CompositionShareDto> ByIssuer { get; init; }
    public required IReadOnlyList<CompositionShareDto> BySector { get; init; }
    public required IReadOnlyList<CompositionShareDto> ByCouponType { get; init; }
    public required IReadOnlyList<CompositionShareDto> ByDurationBucket { get; init; }

    /// <summary>Разрез «Облигации / Фонды» (задача 57); при <c>includeFunds=false</c> пуст. Тот же тип, что остальные разрезы (<c>SharePercent</c> — ПРОЦЕНТЫ).</summary>
    public IReadOnlyList<CompositionShareDto> ByAssetClass { get; init; } = [];

    /// <summary>Фонды вошли в расчёт (<c>includeFunds=true</c>, значение по умолчанию).</summary>
    public bool FundsIncluded { get; init; }

    /// <summary>У пользователя есть открытые позиции в фондах с известной ценой — UI показывает переключатель «С фондами / Только облигации».</summary>
    public bool HasFunds { get; init; }

    public required string Disclaimer { get; init; }
}

public sealed record CompositionShareDto
{
    public required string Key { get; init; }
    public required decimal MarketValueRub { get; init; }

    /// <summary>
    /// Audit(portfolio) P-1: В ПРОЦЕНТАХ (0-100), НЕ в долях — осознанное исключение из
    /// конвенции «бэкенд = доли». Фронт применяет <c>formatSharePercent</c>, НЕ <c>formatPercent</c>.
    /// См. doc-comment <see cref="Bonds.Core.Analytics.CompositionShare.SharePercent"/>.
    /// </summary>
    public required decimal SharePercent { get; init; }
}

public sealed record ScatterResponseDto
{
    public required IReadOnlyList<ScatterPointDto> Points { get; init; }
    public required IReadOnlyList<CurvePointDto> Curve { get; init; }
    public DateOnly? CurveAsOf { get; init; }
    public required string Disclaimer { get; init; }
}

public sealed record ScatterPointDto
{
    public required ulong PositionId { get; init; }
    public required ulong InstrumentId { get; init; }
    public string? Name { get; init; }
    public string? Issuer { get; init; }
    public required decimal ModifiedDuration { get; init; }

    /// <summary>дюрация Маколея — ось X scatter строится по ней, чтобы визуальное «над/под
    /// кривой» совпадало со знаком G-спреда (он тоже считается по Маколею).</summary>
    public required decimal MacaulayDuration { get; init; }
    public decimal? EffectiveYield { get; init; }
    public required string YieldKind { get; init; }
    public required bool IsFloater { get; init; }
    public required bool IsIndexed { get; init; }
    public required bool IsEstimated { get; init; }
    public required bool DataIncomplete { get; init; }

    /// <summary>true — точка watchlist-бумаги без позиции (полый маркер, отдельная категория легенды на фронте).</summary>
    public required bool IsWatchlist { get; init; }
}

public sealed record CurvePointDto
{
    public required decimal TermYears { get; init; }
    public required decimal Yield { get; init; }
}

public sealed record RateScenarioResponseDto
{
    public required decimal CurrentValueRub { get; init; }

    /// <summary>H-1/M-1: процентно-чувствительная часть портфеля (бумаги с дюрацией). Δ считается
    /// только от неё; флоатеры/бумаги без дюрации входят в CurrentValueRub, но не в Δ.</summary>
    public decimal RateSensitiveValueRub { get; init; }
    public required IReadOnlyList<RateScenarioPointDto> Scenarios { get; init; }

    /// <summary>Фонды вошли в базу и в Δ (<c>includeFunds=true</c>, по умолчанию).</summary>
    public bool FundsIncluded { get; init; }

    /// <summary>У пользователя есть открытые позиции в фондах с известной ценой (для показа переключателя).</summary>
    public bool HasFunds { get; init; }

    /// <summary>Фонды с их вкладом (пусто при <c>includeFunds=false</c>).</summary>
    public IReadOnlyList<RateScenarioFundDto> Funds { get; init; } = [];

    public required string Disclaimer { get; init; }
}

/// <summary>Фонд в сценариях ставок (задача 57). Деньги — РУБЛИ, дюрация — модифицированная, ГОДЫ (оценка).</summary>
public sealed record RateScenarioFundDto
{
    public required ulong FundId { get; init; }
    public string? Ticker { get; init; }
    public string? Name { get; init; }
    public required decimal MarketValueRub { get; init; }

    /// <summary>Модифицированная дюрация, ГОДЫ; null — чувствительность неизвестна (фонд в базе, но не в Δ).</summary>
    public decimal? ModifiedDurationYears { get; init; }

    /// <summary>"manual" | "index:RGBI" | "unknown".</summary>
    public required string DurationSource { get; init; }

    /// <summary>Вклад фонда в Δ при +100 б.п., РУБЛИ (отрицательный); null — дюрации нет. Без выпуклости.</summary>
    public decimal? DeltaAtPlus100bpRub { get; init; }
}

public sealed record RateScenarioPointDto
{
    public required int ShiftBp { get; init; }
    public required decimal NewValueRub { get; init; }
    public required decimal DeltaRub { get; init; }

    /// <summary>
    /// Audit(portfolio) P-1: В ПРОЦЕНТАХ (0-100), НЕ в долях — осознанное исключение из
    /// конвенции «бэкенд = доли». Фронт (Analytics.tsx) читает через <c>.toFixed(2)</c>
    /// напрямую, БЕЗ <c>formatPercent</c>. См. doc-comment
    /// <see cref="Bonds.Core.Analytics.RateScenarioPoint.DeltaPercent"/>.
    /// </summary>
    public required decimal DeltaPercent { get; init; }
}

public sealed record TrajectoryResponseDto
{
    public required decimal InitialValueRub { get; init; }
    public required IReadOnlyList<TrajectoryPointDto> WithReinvest { get; init; }
    public required IReadOnlyList<TrajectoryPointDto> WithoutReinvest { get; init; }
    public required decimal ReinvestRateUsed { get; init; }
    public required string Disclaimer { get; init; }
}

public sealed record TrajectoryPointDto
{
    public required string Month { get; init; }
    public required decimal PortfolioValueRub { get; init; }
    public required decimal CumulativeIncomeRub { get; init; }
}
