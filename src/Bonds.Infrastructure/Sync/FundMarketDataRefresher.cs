using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Infrastructure.Connectors.Moex;
using Bonds.Infrastructure.Connectors.TInvest;
using Microsoft.Extensions.Logging;

namespace Bonds.Infrastructure.Sync;

/// <summary>Итог суточного шага рыночных данных фондов (для лога/тестов).</summary>
public sealed record FundMarketDataRefreshResult(int FundsRefreshed, int PricePointsWritten, int DistributionsWritten, int IndexPointsWritten);

/// <summary>
/// Задача 51 часть E: суточный шаг рыночных данных фондов в контуре <see cref="Bonds.Infrastructure.Scheduling.SyncCycleService"/>.
/// Для каждого фонда справочника (держим сейчас ИЛИ держали): история цен паёв MOEX TQBR (РУБЛИ за пай; первый раз — 2 года, далее
/// с последней даты), выплаты на пай из T-Invest (рубли, за 2 года, upsert); плюс история индекса RGBI (доля/годы) и индексов-бенчмарков фондов.
/// Ворота «раз в сутки» — по БД (последняя сохранённая дата моложе <see cref="LastCompletedSessionDate"/> → пропуск): переживает рестарты
/// и ручной «Обновить данные». Без доступного токена T-Invest выплаты пропускаются, MOEX-часть работает. Сбой одного фонда не мешает остальным;
/// исключения вызывающий шаг цикла превращает в LogWarning (цикл не помечается неуспешным).
/// </summary>
public sealed class FundMarketDataRefresher
{
    /// <summary>Индекс доходности облигаций по умолчанию (прогноз IndexYield для фондов гособлигаций).</summary>
    public const string DefaultIndexCode = "RGBI";

    /// <summary>Глубина первичной загрузки истории и выплат.</summary>
    public const int HistoryYears = 2;

    /// <summary>
    /// Фонды, по которым в БД так и нет ни одной цены, и дата последней попытки (аналог <c>BondSyncService.EmptyPortfolioSeen</c>):
    /// такой фонд (нет secid / MOEX пуст) иначе дёргал бы MOEX/T-Invest каждый цикл — пробуем не чаще раза в календарный день.
    /// In-memory (сервис scoped): после рестарта первая попытка повторяется, это безвредно.
    /// </summary>
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, DateOnly> EmptyAttemptedOn = new();

    private readonly IFundRepository _funds;
    private readonly IFundMarketDataRepository _marketData;
    private readonly IMoexIssClient _moex;
    private readonly ITInvestPortfolioClient _tInvest;
    private readonly ILogger<FundMarketDataRefresher> _logger;

    public FundMarketDataRefresher(
        IFundRepository funds,
        IFundMarketDataRepository marketData,
        IMoexIssClient moex,
        ITInvestPortfolioClient tInvest,
        ILogger<FundMarketDataRefresher> logger)
    {
        _funds = funds;
        _marketData = marketData;
        _moex = moex;
        _tInvest = tInvest;
        _logger = logger;
    }

    /// <summary>
    /// Последняя торговая дата, по которую данные считаются «свежими» к <paramref name="today"/>: предыдущий будний день
    /// (сегодняшняя свеча появляется только после закрытия, поэтому в течение дня она не ожидается). Праздники не учитываются —
    /// в такие дни шаг делает один лишний инкрементальный запрос на фонд, это безвредно.
    /// </summary>
    internal static DateOnly LastCompletedSessionDate(DateOnly today)
    {
        var d = today.AddDays(-1);
        while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) d = d.AddDays(-1);
        return d;
    }

    public async Task<FundMarketDataRefreshResult> RefreshIfDueAsync(DateOnly today, bool tInvestAvailable, CancellationToken ct = default)
    {
        var funds = await _funds.GetAllAsync();
        if (funds.Count == 0) return new FundMarketDataRefreshResult(0, 0, 0, 0);

        var expected = LastCompletedSessionDate(today);
        int refreshed = 0, prices = 0, distributions = 0, indexPoints = 0;

        foreach (var fund in funds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var lastDate = await _marketData.GetLastPriceDateAsync(fund.Id, ct);
                if (lastDate is { } last && last >= expected) continue;
                var pricesBefore = prices;
                if (lastDate is null && EmptyAttemptedOn.TryGetValue(fund.Id, out var attempted) && attempted >= today) continue;

                var secid = fund.Secid ?? fund.Ticker;
                if (!string.IsNullOrEmpty(secid))
                {
                    // Повторно берём и саму последнюю дату — перезапись идемпотентна, зато поправляет недозакрытую свечу.
                    var from = lastDate ?? today.AddYears(-HistoryYears);
                    var history = await _moex.GetSharesHistoryAsync(secid, from, today, ct);
                    var points = history
                        .Where(p => p.CloseRub is > 0m)
                        .Select(p => new FundPricePoint(p.TradeDate, p.CloseRub!.Value))
                        .ToList();
                    if (points.Count > 0)
                    {
                        await _marketData.UpsertPricesAsync(fund.Id, points, ct);
                        prices += points.Count;
                    }
                }

                if (tInvestAvailable)
                {
                    var dist = await _tInvest.GetFundDistributionsAsync(fund.Figi, today.AddYears(-HistoryYears), today, ct);
                    if (dist.Count > 0)
                    {
                        await _marketData.UpsertDistributionsAsync(
                            fund.Id,
                            dist.Select(d => new FundDistribution(d.RecordDate, d.PaymentDate, d.AmountPerShareRub)).ToList(),
                            ct);
                        distributions += dist.Count;
                    }
                }

                if (lastDate is null && prices == pricesBefore) EmptyAttemptedOn[fund.Id] = today;
                else EmptyAttemptedOn.TryRemove(fund.Id, out _);

                refreshed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Fund market data refresh failed for fund {FundId} ({Ticker})", fund.Id, fund.Ticker);
            }
        }

        // Индексы: RGBI всегда + явные бенчмарки фондов.
        var codes = funds
            .Select(f => f.BenchmarkIndex)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c!.Trim())
            .Append(DefaultIndexCode)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var code in codes)
        {
            try
            {
                var lastDate = await _marketData.GetLastIndexDateAsync(code, ct);
                if (lastDate is { } last && last >= expected) continue;

                var from = lastDate ?? today.AddYears(-HistoryYears);
                var history = await _moex.GetIndexHistoryAsync(code, from, today, ct);
                if (history.Count == 0) continue;

                await _marketData.UpsertIndexHistoryAsync(
                    code,
                    history.Select(p => new MarketIndexPoint(p.TradeDate, p.Close, p.YieldFraction, p.DurationYears)).ToList(),
                    ct);
                indexPoints += history.Count;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Index history refresh failed for {IndexCode}", code);
            }
        }

        if (prices + distributions + indexPoints > 0)
        {
            _logger.LogInformation(
                "Fund market data refreshed: {Funds} fund(s), {Prices} price point(s), {Distributions} distribution(s), {Index} index point(s)",
                refreshed, prices, distributions, indexPoints);
        }

        return new FundMarketDataRefreshResult(refreshed, prices, distributions, indexPoints);
    }
}
