using Bonds.Core.Currencies;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Infrastructure.Connectors.Cbr;
using Bonds.Infrastructure.Connectors.Moex;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bonds.Infrastructure.Sync;

/// <summary>Итог суточного шага курса валют (для лога/тестов).</summary>
public sealed record CurrencyRateRefreshResult(int CurrenciesRefreshed, int RatePointsWritten);

/// <summary>
/// Задача 55: суточный шаг истории курса валют (MOEX CETS) рядом с <see cref="FundMarketDataRefresher"/> (тот же шаг цикла
/// <see cref="Bonds.Infrastructure.Scheduling.SyncCycleService"/>). Для каждой валюты справочника: история <c>CLOSE</c> (первый раз — 2 года,
/// далее с последней даты; повтор последней даты идемпотентен) → <b>РУБЛИ ЗА 1 ЕДИНИЦУ</b> = <c>CLOSE ÷ FACEVALUE</c>
/// (<see cref="CurrencyRateUnits.RubPerUnit"/>; JPY котируется за 100). Ворота «раз в сутки» и «пустые» валюты — общие с фондами
/// (<see cref="SyncGuards"/>). Сбой одной валюты не мешает остальным; исключения вызывающий шаг превращает в LogWarning.
/// <para>
/// <b>Всегда отслеживаемые валюты (задача 60).</b> USD, EUR, GBP (<see cref="CurrencyRateOptions.AlwaysTrackedCurrencies"/>) заводятся в справочник
/// заглушкой (<c>figi = FX:USD</c>, заменится настоящим FIGI при первом синке T-Invest), даже если валюты нет в T-Invest: курс нужен для
/// оценки брокерских позиций в рублях. USD берётся с MOEX CETS (<c>USD000UTSTOM</c>), а EUR/GBP — с официального курса ЦБ РФ
/// (<see cref="TrackedCurrencies"/>: <c>EUR_RUB__TOM</c>/<c>GBPRUB_TOM</c> на бирже не торгуются). Если MOEX ничего не отдал, а у валюты
/// известен код ЦБ — берётся курс ЦБ (в обеих ветках — РУБЛИ за 1 единицу).
/// </para>
/// </summary>
public sealed class CurrencyRateRefresher
{
    public const int HistoryYears = 2;

    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, DateOnly> EmptyAttemptedOn = new();

    private readonly ICurrencyRepository _currencies;
    private readonly ICurrencyMarketDataRepository _marketData;
    private readonly IMoexIssClient _moex;
    private readonly ICbrRatesClient _cbr;
    private readonly CurrencyRateOptions _options;
    private readonly ILogger<CurrencyRateRefresher> _logger;

    public CurrencyRateRefresher(
        ICurrencyRepository currencies,
        ICurrencyMarketDataRepository marketData,
        IMoexIssClient moex,
        ICbrRatesClient cbr,
        IOptions<CurrencyRateOptions> options,
        ILogger<CurrencyRateRefresher> logger)
    {
        _currencies = currencies;
        _marketData = marketData;
        _moex = moex;
        _cbr = cbr;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Заводит в справочнике заглушки для всегда отслеживаемых валют, которых там ещё нет (уже существующая запись T-Invest не трогается).</summary>
    internal async Task EnsureTrackedCurrenciesAsync()
    {
        foreach (var iso in _options.AlwaysTrackedCurrencies)
        {
            var source = TrackedCurrencies.Find(iso);
            if (source is null)
            {
                _logger.LogWarning("Currency rates: unknown always-tracked currency {Iso} — skipped (no rate source configured)", iso);
                continue;
            }

            if (await _currencies.GetByIsoAsync(source.Iso) is not null) continue;

            await _currencies.UpsertAsync(new Currency
            {
                Figi = TrackedCurrencies.PlaceholderFigi(source.Iso),
                IsoCode = source.Iso,
                Ticker = source.MoexSecid,
                Name = source.Name,
                MoexSecid = source.MoexSecid,
                FaceValue = 1m,
            });
        }
    }

    public async Task<CurrencyRateRefreshResult> RefreshIfDueAsync(DateOnly today, CancellationToken ct = default)
    {
        await EnsureTrackedCurrenciesAsync();
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

                var tracked = TrackedCurrencies.Find(currency.IsoCode);
                // Валюты без торгов на MOEX (EUR, GBP) с пустым secid не опрашиваем: вывод «{ISO}RUB_TOM» дал бы мёртвую доску.
                var secid = currency.MoexSecid
                    ?? (tracked is { MoexSecid: null } ? null : CurrencyMoexSecid.Derive(currency.Ticker, currency.IsoCode));
                var from = lastDate ?? today.AddYears(-HistoryYears);
                var written = 0;
                if (!string.IsNullOrEmpty(secid))
                {
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

                if (written == 0 && tracked?.CbrId is { } cbrId)
                {
                    // ЦБ: VunitRate уже рубли за 1 единицу (номинал учтён парсером) — повторной конверсии нет.
                    var cbrRates = (await _cbr.GetRatesAsync(cbrId, from, today, ct))
                        .Select(p => new CurrencyRatePoint(p.Date, p.RubPerUnit))
                        .ToList();
                    if (cbrRates.Count > 0)
                    {
                        await _marketData.UpsertRatesAsync(currency.Id, cbrRates, ct);
                        written = cbrRates.Count;
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
