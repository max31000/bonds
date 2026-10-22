using Bonds.Api.Auth;
using Bonds.Core.Currencies;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Time;
using Bonds.Infrastructure.Analytics;

namespace Bonds.Api.Endpoints;

/// <summary>
/// Валюта как актив (задача 55): <c>GET /api/currencies</c>. Валюта - отдельный домен (как фонды): P&amp;L считает <see cref="IncomeLedgerBuilder"/>
/// (одна методика со страницей «Доходы»), курс - единая точка <see cref="IncomeLedgerBuilder.ResolveCurrencyPriceAsync"/>.
/// </summary>
public static class CurrenciesEndpoints
{
    internal const string CurrenciesDisclaimer =
        "Оценка по журналу брокера и курсу MOEX; курсовая разница считается методом средней цены и не равна налоговой базе. " +
        "Прошлое изменение курса не прогноз. Не инвестиционная рекомендация.";

    public static void MapCurrenciesEndpoints(this WebApplication app)
    {
        app.MapGet("/api/currencies", GetCurrencies);
    }

    private static async Task<IResult> GetCurrencies(
        ICurrentUser currentUser,
        IAccountRepository accountRepo,
        CurrencyOverviewBuilder overview,
        CancellationToken ct)
    {
        var asOf = BusinessClock.MoscowToday();
        var accountId = await PositionsEndpoints.ResolveAccountIdAsync(currentUser, accountRepo);
        if (accountId is null)
            return Results.Ok(new CurrenciesResponseDto { AsOf = asOf, Currencies = [], Totals = EmptyTotals, Disclaimer = CurrenciesDisclaimer });

        var items = await overview.BuildAsync(accountId.Value, asOf, ct: ct);
        var rows = items.Select(ToRow).ToList();
        var totals = new CurrenciesTotalsDto
        {
            MarketValueRub = rows.Sum(r => r.MarketValueRub),
            InvestedRub = rows.Sum(r => r.InvestedRub),
            UnrealizedPnlRub = rows.Sum(r => r.UnrealizedPnlRub ?? 0m),
            RealizedPnlRub = rows.Sum(r => r.RealizedPnlRub),
            TotalResultRub = rows.Sum(r => r.TotalResultRub),
            DataIncomplete = rows.Any(r => r.DataIncomplete),
        };
        return Results.Ok(new CurrenciesResponseDto { AsOf = asOf, Currencies = rows, Totals = totals, Disclaimer = CurrenciesDisclaimer });
    }

    private static readonly CurrenciesTotalsDto EmptyTotals = new()
    {
        MarketValueRub = 0m, InvestedRub = 0m, UnrealizedPnlRub = 0m, RealizedPnlRub = 0m, TotalResultRub = 0m,
    };

    private static CurrencyRowDto ToRow(CurrencyOverviewItem item)
    {
        var inc = item.Income.Result!.Income;
        var qty = inc.QuantityNow;
        var price = item.Income.PriceRub; // тот же курс, от которого посчитаны стоимость и P&L

        return new CurrencyRowDto
        {
            CurrencyId = item.Currency.Id,
            IsoCode = item.Currency.IsoCode,
            Ticker = item.Currency.Ticker,
            Name = item.Currency.Name,
            Quantity = qty,
            CurrentPriceRub = price,
            PriceAsOf = item.Position?.PriceAsOf,
            MarketValueRub = inc.MarketValueCleanRub,
            AvgCostRub = qty > 0m ? inc.CostRemainingRub / qty : 0m,
            InvestedRub = inc.CostRemainingRub,
            UnrealizedPnlRub = inc.UnrealizedPnlRub,
            UnrealizedPnlFraction = inc.UnrealizedPnlRub is { } u && inc.CostRemainingRub > 0m ? u / inc.CostRemainingRub : null,
            RealizedPnlRub = inc.RealizedPnlRub,
            TotalResultRub = inc.TotalResultRub,
            TotalReturnFraction = inc.TotalReturnFraction,
            XirrFraction = inc.XirrFraction,
            RateChange = new CurrencyRateChangeDto { M1 = item.RateChange.M1, M3 = item.RateChange.M3, M12 = item.RateChange.M12 },
            RateHistory = item.Rates.OrderBy(r => r.TradeDate)
                .Select(r => new CurrencyRatePointDto { Date = r.TradeDate, CloseRub = r.CloseRub }).ToList(),
            HasUnknownLots = inc.HasUnknownLots,
            DataIncomplete = item.Income.DataIncomplete,
        };
    }
}
