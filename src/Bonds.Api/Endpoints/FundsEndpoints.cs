using System.Text.RegularExpressions;
using Bonds.Api.Auth;
using Bonds.Core.Currencies;
using Bonds.Core.Funds;
using Bonds.Core.Income;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Core.Time;
using Bonds.Infrastructure.Analytics;
using static Bonds.Api.Endpoints.AnalyticsSupport;

namespace Bonds.Api.Endpoints;

/// <summary>
/// Фонды (БПИФ) в продукте (задача 53): <c>GET /api/funds</c>, <c>GET /api/funds/{id}</c>, <c>PUT /api/funds/{id}/settings</c> и общий баланс
/// <c>GET /api/portfolio/summary</c>. Фонды — отдельный домен: в облигационные аналитики не подмешиваются. P&amp;L считает
/// <see cref="IncomeLedgerBuilder"/> (одна методика со страницей «Доходы»), оценка доходности — <see cref="FundOutlookService"/>.
/// </summary>
public static partial class FundsEndpoints
{
    internal const string FundsDisclaimer =
        "Оценка на основе истории цен паёв и выплат; не гарантия будущей доходности и не инвестиционная рекомендация. " +
        "У фондов с плавающим купоном выплаты следуют за ключевой ставкой. P&L — метод средней цены, налоги — как удержал брокер.";

    /// <summary>Верхняя граница ручной модифицированной дюрации фонда, ГОДЫ.</summary>
    internal const decimal MaxDurationOverrideYears = 30m;

    [GeneratedRegex("^[A-Z0-9]{2,16}$")]
    private static partial Regex IndexCodeRegex();

    public static void MapFundsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/funds", GetFunds);
        app.MapGet("/api/funds/{fundId}", GetFundDetail);
        app.MapPut("/api/funds/{fundId}/settings", UpdateSettings);
        app.MapGet("/api/portfolio/summary", GetPortfolioSummary);
    }

    private static async Task<IResult> GetFunds(
        ICurrentUser currentUser,
        IAccountRepository accountRepo,
        FundOverviewBuilder overview,
        CancellationToken ct)
    {
        var asOf = BusinessClock.MoscowToday();
        var accountId = await PositionsEndpoints.ResolveAccountIdAsync(currentUser, accountRepo);
        if (accountId is null)
            return Results.Ok(new FundsResponseDto { AsOf = asOf, Funds = [], Totals = EmptyTotals, Disclaimer = FundsDisclaimer });

        var items = await overview.BuildAsync(accountId.Value, asOf, openOnly: true, ct: ct);
        var bondsValue = await overview.GetBondsValueRubAsync(accountId.Value, asOf, ct);
        var portfolioValue = bondsValue + items.Sum(i => i.Income.Result!.Income.MarketValueCleanRub);

        var rows = items.Select(i => ToRow(i, portfolioValue)).ToList();
        var totals = new FundsTotalsDto
        {
            MarketValueRub = rows.Sum(r => r.MarketValueRub),
            InvestedRub = rows.Sum(r => r.InvestedRub),
            UnrealizedPnlRub = rows.Sum(r => r.UnrealizedPnlRub ?? 0m),
            IncomeNetRub = rows.Sum(r => r.DistributionsNetRub),
            TotalResultRub = rows.Sum(r => r.TotalResultRub),
            DataIncomplete = rows.Any(r => r.DataIncomplete),
        };
        return Results.Ok(new FundsResponseDto { AsOf = asOf, Funds = rows, Totals = totals, Disclaimer = FundsDisclaimer });
    }

    private static async Task<IResult> GetFundDetail(
        ulong fundId,
        ICurrentUser currentUser,
        IAccountRepository accountRepo,
        FundOverviewBuilder overview,
        IFundOperationRepository fundOperations,
        CancellationToken ct)
    {
        var asOf = BusinessClock.MoscowToday();
        var accountId = await PositionsEndpoints.ResolveAccountIdAsync(currentUser, accountRepo);
        if (accountId is null) return Results.NotFound();

        var item = (await overview.BuildAsync(accountId.Value, asOf, [fundId], openOnly: false, ct: ct)).FirstOrDefault();
        if (item is null) return Results.NotFound();

        var bondsValue = await overview.GetBondsValueRubAsync(accountId.Value, asOf, ct);
        var portfolioValue = bondsValue + await overview.GetFundsValueRubAsync(accountId.Value, ct);

        var ops = await fundOperations.GetByFundAsync(accountId.Value, fundId);
        var from = asOf.AddYears(-FundOverviewBuilder.HistoryYears);

        return Results.Ok(new FundDetailDto
        {
            AsOf = asOf,
            Fund = ToRow(item, portfolioValue),
            PriceHistory = item.Prices.OrderBy(p => p.TradeDate)
                .Select(p => new FundPricePointDto { Date = p.TradeDate, CloseRub = p.CloseRub }).ToList(),
            Distributions = item.Distributions.Where(d => d.RecordDate >= from).OrderBy(d => d.RecordDate)
                .Select(d => new FundDistributionDto
                {
                    RecordDate = d.RecordDate,
                    PaymentDate = d.PaymentDate,
                    AmountPerShareRub = d.AmountPerShareRub,
                    YieldOnPriceFraction = YieldOnPrice(item.Prices, d),
                }).ToList(),
            Operations = ops.OrderByDescending(o => o.Date)
                .Select(o => new FundOperationDto { Date = o.Date, Type = o.Type, AmountRub = o.AmountRub, Quantity = o.Quantity }).ToList(),
            Settings = new FundSettingsDto
            {
                ForecastMethod = item.Fund.ForecastMethod,
                BenchmarkIndex = item.Fund.BenchmarkIndex,
                DurationOverrideYears = item.Fund.DurationOverrideYears,
                DurationEstimate = FundDurationEstimateDto.From(item.DurationEstimate),
            },
            Disclaimer = FundsDisclaimer,
        });
    }

    private static async Task<IResult> UpdateSettings(
        ulong fundId,
        UpdateFundSettingsRequest request,
        IFundRepository funds,
        FundOverviewBuilder overview,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ForecastMethod)
            || int.TryParse(request.ForecastMethod, out _)
            || !Enum.TryParse<FundForecastMethod>(request.ForecastMethod.Trim(), ignoreCase: true, out var method)
            || !Enum.IsDefined(method))
            return ValidationError("Неизвестный forecastMethod: допустимо auto, distributions, indexYield, trailingReturn.");

        string? benchmark = null;
        if (!string.IsNullOrWhiteSpace(request.BenchmarkIndex))
        {
            benchmark = request.BenchmarkIndex.Trim();
            if (!IndexCodeRegex().IsMatch(benchmark))
                return ValidationError("benchmarkIndex должен соответствовать ^[A-Z0-9]{2,16}$ (например, RGBI).");
        }

        // Ручная модифицированная дюрация — ГОДЫ; null = сброс. Предел 30 лет — заведомо выше любой облигации.
        if (request.DurationOverrideYears is { } duration && (duration < 0m || duration > MaxDurationOverrideYears))
            return ValidationError($"durationOverrideYears должна быть в диапазоне 0–{MaxDurationOverrideYears} лет (модифицированная дюрация, годы).");
        var durationOverride = request.DurationOverrideYears is { } d ? Math.Round(d, 4, MidpointRounding.AwayFromZero) : (decimal?)null;

        if (await funds.GetByIdAsync(fundId) is null) return Results.NotFound();

        await funds.UpdateSettingsAsync(fundId, method, benchmark, durationOverride);
        var updated = await funds.GetByIdAsync(fundId);
        var estimate = updated is null ? null : await overview.GetDurationEstimateAsync(updated, BusinessClock.MoscowToday(), ct);
        return Results.Ok(new FundSettingsDto
        {
            ForecastMethod = method,
            BenchmarkIndex = benchmark,
            DurationOverrideYears = durationOverride,
            DurationEstimate = estimate is null ? null : FundDurationEstimateDto.From(estimate),
        });
    }

    private static async Task<IResult> GetPortfolioSummary(
        ICurrentUser currentUser,
        IAccountRepository accountRepo,
        FundOverviewBuilder overview,
        IncomeLedgerBuilder incomeBuilder,
        CurrencyOverviewBuilder currencyOverview,
        BrokerHoldingsBuilder brokerHoldings,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        var asOf = BusinessClock.MoscowToday();

        // Другие брокеры (Freedom, задача 60): не входят в totalRub (его смысл - T-Invest), идут отдельным полем и в capitalTotalRub.
        // Сбой оценки - только лог: сводка T-Invest не должна падать из-за вторичного счёта.
        decimal otherBrokers = 0m;
        var otherBrokersIncomplete = false;
        if (await currentUser.GetUserIdAsync() is { } userId)
        {
            try
            {
                (otherBrokers, otherBrokersIncomplete) = await brokerHoldings.GetOtherBrokersValueAsync(userId, asOf, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Portfolio summary: other brokers valuation failed — returned without it");
                otherBrokersIncomplete = true;
            }
        }

        var accountId = await PositionsEndpoints.ResolveAccountIdAsync(currentUser, accountRepo);
        if (accountId is null)
            return Results.Ok(new PortfolioSummaryDto
            {
                BondsValueRub = 0m, FundsValueRub = 0m, TotalRub = 0m,
                OtherBrokersValueRub = otherBrokers, OtherBrokersDataIncomplete = otherBrokersIncomplete, CapitalTotalRub = otherBrokers,
                Disclaimer = FundsDisclaimer,
            });

        var account = await accountRepo.GetByIdAsync(accountId.Value);
        var bonds = await overview.GetBondsValueRubAsync(accountId.Value, asOf, ct);
        var (funds, fundsIncomplete) = await overview.GetFundsValueAsync(accountId.Value, ct);
        // Валюта (задача 55): брокерский cashRub (TotalAmountCurrencies) УЖЕ включает её, пересчитанную в рубли. В итог она НЕ
        // добавляется (иначе двойной счёт) - только выделяется из денег: cashRubOnly = cashRub - currencyValueRub.
        var (currencyValue, currencyIncomplete) = await currencyOverview.GetCurrenciesValueAsync(accountId.Value, ct);
        var total = bonds + funds + (account?.CashRub ?? 0m);

        // XIRR портфеля — тот же сервис и тот же набор активов, что у GET /api/income (без периода).
        decimal? xirr = null;
        try
        {
            var data = await incomeBuilder.BuildAsync(accountId.Value, asOf, null, ct);
            var calculated = data.Assets.Where(a => a.Result is not null).Select(a => a.Result!).ToList();
            xirr = IncomeStatisticsService.Compute(calculated, data.AccountEntries, null, null, asOf).PortfolioXirrFraction;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // XIRR — вторичная метрика: отказ не должен ронять баланс.
            logger.LogWarning(ex, "Portfolio summary: XIRR calculation failed — returned without it");
        }

        return Results.Ok(new PortfolioSummaryDto
        {
            BondsValueRub = bonds,
            FundsValueRub = funds,
            CashRub = account?.CashRub,
            CurrencyValueRub = currencyValue,
            CashRubOnly = account?.CashRub is { } cash ? CashBreakdown.CashRubOnly(cash, currencyValue) : null,
            CurrencyDataIncomplete = currencyIncomplete,
            TotalRub = total,
            OtherBrokersValueRub = otherBrokers,
            OtherBrokersDataIncomplete = otherBrokersIncomplete,
            CapitalTotalRub = total + otherBrokers,
            BrokerTotalRub = account?.BrokerTotalRub,
            BrokerDiffRub = account?.BrokerTotalRub is { } broker ? broker - total : null,
            BalanceSyncedAt = account?.BalanceSyncedAt,
            PortfolioXirrFraction = xirr,
            FundsDataIncomplete = fundsIncomplete,
            Disclaimer = FundsDisclaimer,
        });
    }

    private static readonly FundsTotalsDto EmptyTotals = new()
    {
        MarketValueRub = 0m, InvestedRub = 0m, UnrealizedPnlRub = 0m, IncomeNetRub = 0m, TotalResultRub = 0m,
    };

    private static decimal? YieldOnPrice(IReadOnlyList<FundPricePoint> prices, FundDistribution d)
    {
        var price = prices.Where(p => p.TradeDate <= d.RecordDate).OrderByDescending(p => p.TradeDate)
            .Select(p => (decimal?)p.CloseRub).FirstOrDefault();
        return price is > 0m ? d.AmountPerShareRub / price : null;
    }

    private static FundRowDto ToRow(FundOverviewItem item, decimal portfolioValueRub)
    {
        var inc = item.Income.Result!.Income;
        var qty = inc.QuantityNow;
        var price = item.Income.PriceRub; // та же цена, от которой посчитаны стоимость и P&L
        var net = inc.IncomeGrossRub + inc.TaxesRub;

        return new FundRowDto
        {
            FundId = item.Fund.Id,
            Ticker = item.Fund.Ticker,
            Name = item.Fund.Name,
            ManagementCompany = item.Fund.ManagementCompany,
            Quantity = qty,
            CurrentPriceRub = price,
            PriceAsOf = item.Position?.PriceAsOf,
            MarketValueRub = inc.MarketValueCleanRub,
            AvgCostRub = qty > 0m ? inc.CostRemainingRub / qty : 0m,
            InvestedRub = inc.CostRemainingRub,
            UnrealizedPnlRub = inc.UnrealizedPnlRub,
            UnrealizedPnlFraction = inc.UnrealizedPnlRub is { } u && inc.CostRemainingRub > 0m ? u / inc.CostRemainingRub : null,
            DistributionsGrossRub = inc.IncomeGrossRub,
            TaxesRub = inc.TaxesRub,
            DistributionsNetRub = net,
            TotalResultRub = inc.TotalResultRub,
            TotalReturnFraction = inc.TotalReturnFraction,
            XirrFraction = inc.XirrFraction,
            TotalExpenseFraction = item.Fund.TotalExpenseFraction,
            Performance = ToDto(item.Performance),
            Outlook = FundOutlookDto.From(item.Outlook),
            DurationEstimate = FundDurationEstimateDto.From(item.DurationEstimate),
            ShareOfPortfolioFraction = portfolioValueRub > 0m ? inc.MarketValueCleanRub / portfolioValueRub : null,
            HasUnknownLots = inc.HasUnknownLots,
            DataIncomplete = item.Income.DataIncomplete,
        };
    }

    private static FundPerformanceDto ToDto(FundPerformance p) => new()
    {
        M1 = ToDto(p.M1), M3 = ToDto(p.M3), M6 = ToDto(p.M6), M12 = ToDto(p.M12), SinceFirstBuy = ToDto(p.SinceFirstBuy),
    };

    private static FundWindowReturnDto? ToDto(FundWindowReturn? w) =>
        w is null ? null : new FundWindowReturnDto { ReturnFraction = w.ReturnFraction, AnnualizedFraction = w.AnnualizedFraction, Days = w.Days };
}
