using Bonds.Core.Currencies;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Infrastructure.Connectors.Moex;
using Microsoft.Extensions.Logging;

namespace Bonds.Infrastructure.Sync;

/// <summary>Итог суточного шага курса валют (для лога/тестов).</summary>
public sealed record CurrencyRateRefreshResult(int CurrenciesRefreshed, int RatePointsWritten);

/// <summary>
/// Задача 55: суточный шаг истории курса валют (MOEX CETS) рядом с <see cref="FundMarketDataRefresher"/> (тот же шаг цикла
/// <see cref="Bonds.Infrastructure.Scheduling.SyncCycleService"/>). Для каждой валюты справочника: история <c>CLOSE</c> (первый раз — 2 года,
/// далее с последней даты; повтор последней даты идемпотентен) → <b>РУБЛИ ЗА 1 ЕДИНИЦУ</b> = <c>CLOSE ÷ FACEVALUE</c>
/// (<see cref="CurrencyRateUnits.RubPerUnit"/>; JPY котируется за 100). Ворота «раз в сутки» и «пустые» валюты — общие с фондами
/// (<see cref="SyncGuards"/>). Сбой одной валюты не мешает остальным; исключения вызывающий шаг превращает в LogWarning.
/// </summary>
public sealed class CurrencyRateRefresher
{
    public const int HistoryYears = 2;

    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, DateOnly> EmptyAttemptedOn = new();

    private readonly ICurrencyRepository _currencies;
    private readonly ICurrencyMarketDataRepository _marketData;
    private readonly IMoexIssClient _moex;
    private readonly ILogger<CurrencyRateRefresher> _logger;

    public CurrencyRateRefresher(
        ICurrencyRepository currencies,
        ICurrencyMarketDataRepository marketData,
        IMoexIssClient moex,
        ILogger<CurrencyRateRefresher> logger)
    {
        _currencies = currencies;
        _marketData = marketData;
        _moex = moex;
        _logger = logger;
    }

    public async Task<CurrencyRateRefreshResult> RefreshIfDueAsync(DateOnly today, CancellationToken ct = default)
    {
        var currencies = await _currencies.GetAllAsync();
        if (currencies.Count == 0) return new CurrencyRateRefreshResult(0, 0);

        var expected = FundMarketDataRefresher.LastCompletedSessionDate(today);
        int refreshed = 0, points = 0;

        foreach (var currency in currencies)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var lastDate = await _marketData.GetLastRateDateAsync(currency.Id, ct);
                if (SyncGuards.ShouldSkipMarketRefresh(lastDate, expected, EmptyAttemptedOn, currency.Id, today)) continue;

                var secid = currency.MoexSecid ?? CurrencyMoexSecid.Derive(currency.Ticker, currency.IsoCode);
                var written = 0;
                if (!string.IsNullOrEmpty(secid))
                {
                    var from = lastDate ?? today.AddYears(-HistoryYears);
                    var history = await _moex.GetCurrencyHistoryAsync(secid, from, today, ct);
                    var rates = history
                        .Where(p => p.CloseRubPerFaceValue is > 0m)
                        // CLOSE — рубли за FACEVALUE единиц; в БД — рубли за 1 единицу (единственное место конверсии).
                        .Select(p => new CurrencyRatePoint(p.TradeDate, CurrencyRateUnits.RubPerUnit(p.CloseRubPerFaceValue!.Value, currency.FaceValue)))
                        .ToList();
                    if (rates.Count > 0)
                    {
                        await _marketData.UpsertRatesAsync(currency.Id, rates, ct);
                        written = rates.Count;
                    }
                }

                points += written;
                SyncGuards.RecordMarketRefreshOutcome(lastDate, written > 0, EmptyAttemptedOn, currency.Id, today);
                refreshed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Currency rate refresh failed for currency {CurrencyId} ({Iso})", currency.Id, currency.IsoCode);
            }
        }

        if (points > 0)
        {
            _logger.LogInformation("Currency rates refreshed: {Currencies} currency(ies), {Points} rate point(s)", refreshed, points);
        }

        return new CurrencyRateRefreshResult(refreshed, points);
    }
}
