using Bonds.Api.Auth;
using Bonds.Core.Income;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Time;
using Bonds.Infrastructure.Analytics;
using static Bonds.Api.Endpoints.AnalyticsSupport;

namespace Bonds.Api.Endpoints;

/// <summary>
/// Единый журнал доходов (задача 52): P&amp;L по каждой позиции (включая закрытые), итоги, помесячный ряд, XIRR портфеля.
/// Новый сервис живёт рядом с <c>PositionCostBasisService</c>/<c>IfSoldNowService</c> и их не заменяет.
/// </summary>
public static class IncomeEndpoints
{
    internal const string IncomeDisclaimer =
        "Оценка по журналу брокера; налоги — как удержал брокер; метод средней цены, не FIFO — может отличаться от налоговой отчётности. " +
        "Не инвестиционная рекомендация.";

    public static void MapIncomeEndpoints(this WebApplication app)
    {
        app.MapGet("/api/income", GetIncome);
        app.MapGet("/api/income/{assetKind}/{assetId}/ledger", GetLedger);
    }

    private static async Task<IResult> GetIncome(
        ICurrentUser currentUser,
        IAccountRepository accountRepo,
        IncomeLedgerBuilder builder,
        DateOnly? from,
        DateOnly? to,
        string? assetKind,
        bool? includeClosed,
        CancellationToken ct)
    {
        if (from is not null && to is not null && from > to)
            return ValidationError("Параметр from не может быть позже to.");
        if (!TryParseFilter(assetKind, out var filter, out var filterName))
            return ValidationError("Неизвестный assetKind: допустимо all, bond, fund, currency.");

        var asOf = BusinessClock.MoscowToday();
        var accountId = await PositionsEndpoints.ResolveAccountIdAsync(currentUser, accountRepo);
        var data = accountId is null
            ? new IncomeLedgerData([], [])
            : await builder.BuildAsync(accountId.Value, asOf, filter, ct);

        // includeClosed=false — вид таблицы: итоги и ряд считаются по показанным позициям, чтобы суммы сходились с таблицей.
        // Актив с упавшим расчётом (Result == null) не скрываем: пользователь должен видеть строку с пометкой «данные неполные».
        var shown = data.Assets
            .Where(a => includeClosed != false || a.Result is null || a.Result.Income.Status == PositionIncomeStatus.Open)
            .ToList();
        var calculated = shown.Where(a => a.Result is not null).Select(a => a.Result!).ToList();

        var stats = IncomeStatisticsService.Compute(calculated, data.AccountEntries, from, to, asOf);

        // XIRR портфеля — характеристика всего портфеля и не зависит от вида таблицы: при скрытии закрытых считаем по всем активам.
        var portfolioXirr = includeClosed != false
            ? stats.PortfolioXirrFraction
            : IncomeStatisticsService.Compute(
                data.Assets.Where(a => a.Result is not null).Select(a => a.Result!).ToList(),
                data.AccountEntries, from, to, asOf).PortfolioXirrFraction;

        var positions = shown.Select(a => ToPositionDto(a, asOf, stats)).ToList();

        return Results.Ok(new IncomeResponseDto
        {
            AsOf = asOf,
            From = from,
            To = to,
            AssetKind = filterName,
            Totals = ToDto(stats.Totals, positions.Count),
            AccountLevel = new IncomeAccountLevelDto
            {
                TaxesRub = stats.Totals.AccountTaxesRub,
                FeesRub = stats.Totals.AccountFeesRub,
            },
            Period = ToDto(stats.Period),
            Monthly = stats.Months.Select(ToDto).ToList(),
            Positions = positions,
            PortfolioXirrFraction = portfolioXirr,
            Disclaimer = IncomeDisclaimer,
        });
    }

    private static async Task<IResult> GetLedger(
        string assetKind,
        ulong assetId,
        ICurrentUser currentUser,
        IAccountRepository accountRepo,
        IncomeLedgerBuilder builder,
        CancellationToken ct)
    {
        if (!TryParseFilter(assetKind, out var kind, out _) || kind is null)
            return ValidationError("Неизвестный assetKind: допустимо bond, fund, currency.");

        var accountId = await PositionsEndpoints.ResolveAccountIdAsync(currentUser, accountRepo);
        if (accountId is null) return Results.NotFound();

        var data = await builder.BuildAsync(accountId.Value, BusinessClock.MoscowToday(), kind, ct);
        var asset = data.Assets.FirstOrDefault(a => a.Kind == kind && a.AssetId == assetId);
        if (asset is null) return Results.NotFound();
        if (asset.Result is null) return ValidationError("Не удалось рассчитать журнал актива.");

        return Results.Ok(new IncomeLedgerResponseDto
        {
            AssetKind = asset.Kind,
            AssetId = asset.AssetId,
            Name = asset.Name,
            Ticker = asset.Ticker,
            Isin = asset.Isin,
            Status = asset.Result.Income.Status,
            Entries = asset.Result.Rows.Select(r => new IncomeLedgerEntryDto
            {
                Date = r.Entry.Date,
                Kind = r.Entry.Kind,
                AmountRub = r.Entry.AmountRub,
                Quantity = r.Entry.Quantity,
                AccruedRub = r.Entry.AccruedRub,
                QuantityAfter = r.QuantityAfter,
                AvgCostAfterRub = r.AvgCostAfterRub,
                RealizedPnlRub = r.RealizedPnlRub,
            }).ToList(),
            Disclaimer = IncomeDisclaimer,
        });
    }

    private static bool TryParseFilter(string? raw, out AssetKind? filter, out string name)
    {
        filter = null;
        name = "all";
        switch ((raw ?? "all").Trim().ToLowerInvariant())
        {
            case "all": return true;
            case "bond": filter = AssetKind.Bond; name = "bond"; return true;
            case "fund": filter = AssetKind.Fund; name = "fund"; return true;
            case "currency": filter = AssetKind.Currency; name = "currency"; return true;
            default: return false;
        }
    }

    private static IncomePositionDto ToPositionDto(IncomeAssetInfo a, DateOnly asOf, IncomeStatistics stats)
    {
        var p = a.Result?.Income ?? PositionIncome.Empty(a.Kind, a.AssetId, asOf);
        stats.PositionPeriods.TryGetValue((a.Kind, a.AssetId), out var period);
        return new IncomePositionDto
        {
            AssetKind = a.Kind,
            AssetId = a.AssetId,
            Name = a.Name,
            Ticker = a.Ticker,
            Isin = a.Isin,
            PositionId = a.PositionId,
            FundId = a.FundId,
            CurrencyId = a.CurrencyId,
            DataIncomplete = a.DataIncomplete,
            Status = p.Status,
            FirstDate = p.FirstDate,
            LastDate = p.LastDate,
            HoldingDays = p.HoldingDays,
            QuantityNow = p.QuantityNow,
            InvestedTotalRub = p.InvestedTotalRub,
            CostRemainingRub = p.CostRemainingRub,
            MarketValueCleanRub = p.MarketValueCleanRub,
            AccruedNowRub = p.AccruedNowRub,
            UnrealizedPnlRub = p.UnrealizedPnlRub,
            RealizedPnlRub = p.RealizedPnlRub,
            IncomeGrossRub = p.IncomeGrossRub,
            AccruedPaidRub = p.AccruedPaidRub,
            AccruedReceivedRub = p.AccruedReceivedRub,
            NetCouponIncomeRub = p.NetCouponIncomeRub,
            TaxesRub = p.TaxesRub,
            FeesRub = p.FeesRub,
            PrincipalReturnedRub = p.PrincipalReturnedRub,
            TotalResultRub = p.TotalResultRub,
            TotalReturnFraction = p.TotalReturnFraction,
            XirrFraction = p.XirrFraction,
            HasUnknownLots = p.HasUnknownLots,
            AccruedUnknown = p.AccruedUnknown,
            PeriodIncomeGrossRub = period?.IncomeGrossRub ?? 0m,
            PeriodRealizedPnlRub = period?.RealizedPnlRub ?? 0m,
            PeriodTaxesRub = period?.TaxesRub ?? 0m,
            PeriodFeesRub = period?.FeesRub ?? 0m,
        };
    }

    /// <summary><paramref name="positionsCount"/> — число показанных строк (включая активы с упавшим расчётом): совпадает с <c>positions</c>.</summary>
    private static IncomeTotalsDto ToDto(IncomeTotals t, int positionsCount) => new()
    {
        PositionsCount = positionsCount,
        InvestedTotalRub = t.InvestedTotalRub,
        MarketValueCleanRub = t.MarketValueCleanRub,
        AccruedNowRub = t.AccruedNowRub,
        UnrealizedPnlRub = t.UnrealizedPnlRub,
        RealizedPnlRub = t.RealizedPnlRub,
        IncomeGrossRub = t.IncomeGrossRub,
        AccruedPaidRub = t.AccruedPaidRub,
        AccruedReceivedRub = t.AccruedReceivedRub,
        NetCouponIncomeRub = t.NetCouponIncomeRub,
        TaxesRub = t.TaxesRub,
        FeesRub = t.FeesRub,
        PrincipalReturnedRub = t.PrincipalReturnedRub,
        TotalResultRub = t.TotalResultRub,
        TotalReturnFraction = t.TotalReturnFraction,
    };

    private static IncomePeriodDto ToDto(IncomePeriodTotals p) => new()
    {
        CouponsGrossRub = p.CouponsGrossRub,
        DistributionsGrossRub = p.DistributionsGrossRub,
        CurrencyIncomeGrossRub = p.CurrencyIncomeGrossRub,
        AccruedPaidRub = p.AccruedPaidRub,
        AccruedReceivedRub = p.AccruedReceivedRub,
        TaxesRub = p.TaxesRub,
        FeesRub = p.FeesRub,
        RealizedPnlRub = p.RealizedPnlRub,
        NetRub = p.NetRub,
    };

    private static IncomeMonthDto ToDto(IncomeMonthStats m) => new()
    {
        Month = m.Month,
        CouponsGrossRub = m.CouponsGrossRub,
        DistributionsGrossRub = m.DistributionsGrossRub,
        CurrencyIncomeGrossRub = m.CurrencyIncomeGrossRub,
        AccruedNetRub = m.AccruedNetRub,
        TaxesRub = m.TaxesRub,
        FeesRub = m.FeesRub,
        RealizedPnlRub = m.RealizedPnlRub,
        NetRub = m.NetRub,
    };
}
