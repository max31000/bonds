using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Infrastructure.Connectors.TInvest;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bonds.Infrastructure.Quotes;

/// <summary>
/// Лёгкий контур "только цены" (plan/16 часть A). Раз в <see cref="LiveQuotesOptions.PollingInterval"/>
/// в торговые часы MOEX опрашивает текущие цены открытых позиций через существующий
/// <see cref="ITInvestPortfolioClient.GetQuotesAsync"/> и пишет тики в intraday_quotes.
/// <para>
/// <b>Осознанное ограничение (plan/16 явное требование).</b> Поллинг, не gRPC-стриминг
/// MarketDataStream и не SignalR — single-user продукт, раз в 30-60 сек полностью закрывает
/// потребность и на порядок проще в эксплуатации. Точка расширения — этот класс сам за
/// интерфейсом не спрятан намеренно: план прямо говорит "точку расширения оставить (интерфейс),
/// реализацию — нет", то есть для будущего стриминга здесь и сейчас реализации не появится.
/// </para>
/// <para>
/// <b>Устойчивость.</b> Тот же паттерн, что <see cref="Scheduling.SyncSchedulerHostedService"/>:
/// try/catch вокруг каждого тика, ошибка (нет токена, нет позиций, сбой сети/API) — Warning-лог и
/// пропуск итерации, без падения процесса (plan/16: "молча пропустить итерацию, не падать").
/// </para>
/// </summary>
public sealed class LiveQuotesPollingService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly LiveQuotesOptions _options;
    private readonly ILogger<LiveQuotesPollingService> _logger;
    private readonly TimeZoneInfo _moscowTimeZone;

    public LiveQuotesPollingService(
        IServiceScopeFactory scopeFactory,
        IOptions<LiveQuotesOptions> options,
        ILogger<LiveQuotesPollingService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
        _moscowTimeZone = ResolveMoscowTimeZone();
    }

    private static TimeZoneInfo ResolveMoscowTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Russian Standard Time");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Не должно случаться — TickAsync уже ловит свои ожидаемые ошибки, но защищаемся
                // от неожиданных (та же устойчивость, что SyncSchedulerHostedService).
                _logger.LogError(ex, "Unexpected error in live quotes polling tick");
            }

            try
            {
                await Task.Delay(_options.PollingInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private bool IsWithinTradingHours(DateTime utcNow)
    {
        var nowMsk = TimeZoneInfo.ConvertTimeFromUtc(utcNow, _moscowTimeZone);
        if (nowMsk.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;

        var timeOfDay = TimeOnly.FromDateTime(nowMsk);
        return timeOfDay >= _options.TradingWindowStartMsk && timeOfDay <= _options.TradingWindowEndMsk;
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var utcNow = DateTime.UtcNow;
        if (!IsWithinTradingHours(utcNow))
        {
            return; // Вне торговых часов MOEX (plan/16: "не торговые часы — молча пропустить").
        }

        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;

        var accountRepo = sp.GetRequiredService<IAccountRepository>();
        var accountId = await accountRepo.GetPrimaryAccountIdAsync();
        if (accountId is null)
        {
            return; // Ещё нет онбординга — нечего опрашивать.
        }

        var positionRepo = sp.GetRequiredService<IPositionRepository>();
        var positions = (await positionRepo.GetByAccountIdAsync(accountId.Value))
            .Where(p => p.Quantity != 0)
            .ToList();
        if (positions.Count == 0)
        {
            return;
        }

        var instrumentRepo = sp.GetRequiredService<IInstrumentRepository>();
        var figiByInstrumentId = new Dictionary<ulong, string>();
        // НКД по позиции — из последнего полного синка (Position.Accrued, обновляется
        // BondSyncService). Лёгкий контур не пересчитывает НКД сам (это работа движка на полном
        // синке) — берём последнее известное значение как разумное приближение для грязной цены
        // между полными синками (spec §4.4 деградация, а не точный расчёт).
        var accruedByInstrumentId = new Dictionary<ulong, decimal>();
        // Номинал нужен, чтобы перевести котировку T-Invest marketdata из пунктов (% от номинала)
        // в рубли — см. doc-comment LiveQuoteConverter. IsOutOfScopeCurrency — валютные бумаги
        // исключаются из лёгкого контура целиком (см. ниже), их номинал не в рублях.
        var faceValueByInstrumentId = new Dictionary<ulong, decimal>();
        foreach (var position in positions)
        {
            var instrument = await instrumentRepo.GetByIdAsync(position.InstrumentId);
            if (instrument?.Figi is { Length: > 0 } figi)
            {
                if (instrument.IsOutOfScopeCurrency)
                {
                    // Валютный номинал — конвертация котировки в пунктах в рубли без курса
                    // невозможна (spec §11 — вне рублёвого MVP-скоупа). Не поллим такую позицию:
                    // она честно падает в fallback "последний известный тик"/статичная цена
                    // полного синка вместо неверного расчёта (задание, п.2).
                    continue;
                }

                figiByInstrumentId[position.InstrumentId] = figi;
                accruedByInstrumentId[position.InstrumentId] = position.Accrued;
                faceValueByInstrumentId[position.InstrumentId] = instrument.FaceValue;
            }
        }

        if (figiByInstrumentId.Count == 0)
        {
            _logger.LogWarning("Live quotes tick: no FIGI mapped for any open position — skipping");
            return;
        }

        IReadOnlyDictionary<string, TInvestQuote> quotes;
        try
        {
            var tInvestClient = sp.GetRequiredService<ITInvestPortfolioClient>();
            quotes = await tInvestClient.GetQuotesAsync(figiByInstrumentId.Values.ToList(), ct);
        }
        catch (InvalidOperationException ex)
        {
            // Токен не настроен/недействителен — известное восстановимое состояние (см.
            // TInvestPortfolioClient.GetClientAsync), не ошибка уровня Error.
            _logger.LogWarning("Live quotes tick skipped: {Reason}", ex.Message);
            return;
        }
        catch (Exception ex)
        {
            // Сбой сети/API T-Invest — не валим сервис, следующая итерация попробует снова.
            _logger.LogWarning(ex, "Live quotes tick failed to fetch quotes from T-Invest");
            return;
        }

        var intradayRepo = sp.GetRequiredService<IIntradayQuoteRepository>();
        var retentionCutoffUtc = utcNow - _options.Retention;
        var tsUtc = utcNow;

        foreach (var (instrumentId, figi) in figiByInstrumentId)
        {
            if (!quotes.TryGetValue(figi, out var quote) || quote.LastPrice is not { } lastPricePoints)
            {
                continue; // Нет свежей цены по этой бумаге в этом тике — пропускаем, не пишем 0.
            }

            // GetQuotesAsync (T-Invest marketdata) отдаёт цену в ПУНКТАХ (% от номинала), не в
            // рублях — см. doc-comment TInvestQuote.LastPrice и LiveQuoteConverter. Переводим в
            // рубли через номинал инструмента и прибавляем НКД.
            var accrued = accruedByInstrumentId.GetValueOrDefault(instrumentId);
            var faceValue = faceValueByInstrumentId.GetValueOrDefault(instrumentId);
            var dirtyPrice = LiveQuoteConverter.TryComputeDirtyPriceRub(
                lastPricePoints, faceValue, accrued, isOutOfScopeCurrency: false);
            if (dirtyPrice is null)
            {
                continue;
            }

            await intradayRepo.InsertAndPruneAsync(
                new IntradayQuote { InstrumentId = instrumentId, TsUtc = tsUtc, DirtyPriceRub = dirtyPrice.Value },
                retentionCutoffUtc);
        }
    }
}
