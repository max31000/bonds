using Bonds.Core.Credentials;
using Bonds.Core.Execution;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Core.Policy;
using Bonds.Infrastructure.Credentials;
using Microsoft.Extensions.Logging;

namespace Bonds.Infrastructure.Execution;

/// <summary>
/// Состояние брокера пользователя на ОДИН тик исполнителя (задача 66: ворота — по брокеру). <see cref="HaltReason"/> не null — заявки этого брокера НЕ отправляются
/// (висящие снимаются, неначатые строки закрываются); <see cref="Unavailable"/> — проверку у брокера не удалось выполнить (сеть), строки этого брокера ждут следующего тика.
/// </summary>
public sealed class VenueSession
{
    public required string Broker { get; init; }

    /// <summary>Локальный счёт (<c>accounts.id</c>) брокера; 0 — не привязан.</summary>
    public ulong AccountId { get; init; }

    /// <summary>Номер счёта у брокера (T-Invest — id счёта, Freedom — <c>trader_systems_id</c>); пусто — снять/сверить заявки нельзя.</summary>
    public string BrokerAccountId { get; init; } = string.Empty;

    public string? HaltReason { get; init; }
    public bool Unavailable { get; init; }

    /// <summary>Freedom: тариф счёта торгового ключа (для модели комиссий).</summary>
    public int? TariffId { get; init; }

    public bool Live => HaltReason is null && !Unavailable;
    public bool CanManageOrders => !string.IsNullOrEmpty(BrokerAccountId);
}

/// <summary>Рыночные данные строки перед отправкой: цены — в единицах брокера, деньги — в валюте строки, бумаги — штуки.</summary>
public sealed record VenueMarket(
    decimal? Bid, decimal? Ask, DateTime? QuoteTimeUtc, bool? NormalTrading, bool LimitOrderAvailable, bool ApiTradeAvailable, decimal? AvailableMoney, decimal? AvailablePieces);

/// <summary>
/// Итог перечитывания комиссии строки. <see cref="Complete"/> — по всем исполненным заявкам комиссия получена; <see cref="ExpectedFee"/> — модель по ФАКТИЧЕСКОМУ маршруту
/// (Freedom), в валюте строки; null — сравнивать с оценкой плана (<c>est_fee</c>). <see cref="Note"/> — маршрут для уведомления.
/// </summary>
public sealed record VenueFees(bool Complete, decimal? ExpectedFee, string? Note);

/// <summary>
/// Параметры сопровождения заявок брокера (из <see cref="ExecutionOptions"/>): попытки, ожидание до перевыставления, тики отложенной сверки комиссии.
/// T-Invest — общие <c>MaxAttempts</c>/<c>RepriceAfter</c>/<c>MaxFeeRechecks</c>; Freedom — свои (<c>FreedomMaxAttempts</c>/<c>FreedomRepriceAfter</c>/<c>FreedomMaxFeeRechecks</c>).
/// </summary>
public sealed record VenueLimits(int MaxAttempts, TimeSpan RepriceAfter, int MaxFeeRechecks);

/// <summary>Исполненное по истории сделок (задача 67, #17): <see cref="Lots"/> — целые лоты, <see cref="AvgPricePerPiece"/> — средняя цена штуки (валюта сделки).</summary>
public sealed record HistoryFill(long Lots, decimal? AvgPricePerPiece);

/// <summary>
/// Брокер для исполнителя (задача 66): одна реализация на брокера поверх его ЕДИНСТВЕННОГО торгового шлюза (<see cref="ITInvestOrderGateway"/>, <see cref="IFreedomOrderGateway"/>).
/// Общая логика (порядок строк, предполётные проверки, идемпотентность, перевыставление, снятие перед закрытием, итоги) — в <see cref="PlanExecutionEngine"/>.
/// </summary>
public interface IExecutionVenue
{
    string Broker { get; }

    /// <summary>Имя брокера для сообщений.</summary>
    string Title { get; }

    /// <summary>Комиссия приходит только выпиской (Freedom): перечитывается при завершении строки, сверка откладывается, пока не придут все строки комиссии.</summary>
    bool FeesFromStatement { get; }

    /// <summary>Сессия тика: ворота брокера + сверка торгового ключа у брокера (не прошла — секрет invalid и остановка).</summary>
    Task<VenueSession> OpenAsync(ulong userId, CancellationToken ct);

    /// <summary>Только счёт (без ворот) — для снятия заявок при смене торгового ключа.</summary>
    Task<VenueSession> OpenForManagementAsync(ulong userId);

    Task<ExecutionGateResult> GateAsync(ulong userId);

    string NewClientOrderId();

    /// <summary>Отказ по строке ДО сети (тикер, лотность, авто-подтверждение); null — можно.</summary>
    string? LineRejection(TradePlan plan, TradePlanLine line);

    /// <summary>Идентификатор бумаги у брокера (T-Invest — uid, Freedom — тикер); null — нет метаданных.</summary>
    string? InstrumentId(TradePlanLine line, InstrumentTradingMeta meta);

    PolicyMarket MarketOf(TradePlanLine line, InstrumentTradingMeta? meta);

    Task<VenueMarket> MarketAsync(VenueSession session, ulong userId, TradePlanLine line, InstrumentTradingMeta meta, string instrumentId, DateTime nowUtc, CancellationToken ct);

    Task<GatewayOrderState> PostAsync(VenueSession session, ulong userId, TradePlanLine line, string instrumentId, TradeOrder order, CancellationToken ct);

    /// <summary>
    /// Состояние заявки у брокера; <paramref name="searchActive"/> — заявка ещё <c>New</c> (ответа не было): искать и в списке активных. <paramref name="line"/> — строка плана
    /// заявки (Freedom: реквизиты для сверки заявки без id брокера; null — такую заявку не найти). null — брокер не знает (или не удалось однозначно опознать).
    /// </summary>
    Task<GatewayOrderState?> StateAsync(VenueSession session, ulong userId, TradeOrder order, TradePlanLine? line, bool searchActive, CancellationToken ct);

    Task CancelAsync(VenueSession session, ulong userId, TradeOrder order, CancellationToken ct);

    /// <summary>
    /// Задача 67 (#17): брокер заявку не знает (состояние null после таймаута) — сколько по ней исполнено по ИСТОРИИ СДЕЛОК (T-Invest — журнал операций по
    /// инструменту и стороне с <c>created_at</c>, Freedom — <c>getTradesHistory</c> по <c>order_id</c>). null — сделок нет или сверить нечем. Сбой — исключение
    /// (движок логирует и закрывает заявку как раньше).
    /// </summary>
    Task<HistoryFill?> FillsFromHistoryAsync(VenueSession session, ulong userId, TradeOrder order, TradePlanLine line, DateTime nowUtc, CancellationToken ct);

    /// <summary>Перечитывает комиссию (и цену исполнения) исполненных заявок строки — меняет <paramref name="lineOrders"/>; вызывающий сохраняет.</summary>
    Task<VenueFees> RefreshFeesAsync(VenueSession session, ulong userId, TradePlanLine line, IReadOnlyList<TradeOrder> lineOrders, DateTime nowUtc, CancellationToken ct);

    /// <summary>Попытки, ожидание перевыставления и окно отложенной сверки комиссии этого брокера.</summary>
    VenueLimits Limits(ExecutionOptions options);
}

/// <summary>T-Invest — поведение задачи 65 без изменений (вынесено из движка).</summary>
public sealed class TInvestExecutionVenue : IExecutionVenue
{
    private readonly ITInvestOrderGateway _gateway;
    private readonly IExecutionGateResolver _gate;
    private readonly IAccountRepository _accounts;
    private readonly IUserCredentialRepository _credentialRecords;
    private readonly ILogger _logger;
    private readonly IInstrumentTradingMetaRepository? _meta;
    private readonly ITInvestFillHistory? _fills;

    public TInvestExecutionVenue(
        ITInvestOrderGateway gateway, IExecutionGateResolver gate, IAccountRepository accounts, IUserCredentialRepository credentialRecords, ILogger logger,
        IInstrumentTradingMetaRepository? meta = null, ITInvestFillHistory? fills = null)
    {
        _gateway = gateway;
        _gate = gate;
        _accounts = accounts;
        _credentialRecords = credentialRecords;
        _logger = logger;
        _meta = meta;
        _fills = fills;
    }

    /// <summary>Окно сверки по журналу: сделки не раньше <c>created_at − 30 с</c> (расхождение часов).</summary>
    public static readonly TimeSpan HistoryWindowBefore = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Задача 67 (#17): заявка у брокера не найдена — исполненные сделки ТОГО ЖЕ инструмента (uid или figi из метаданных торгов) и ТОЙ ЖЕ стороны с
    /// <c>created_at − 30 с</c> по журналу операций (клиент на чтение пользователя). Штуки → целые лоты, не больше запрошенного. Сделку по номеру заявки журнал
    /// не связывает — ручная сделка в том же инструменте в это окно тоже будет засчитана (консервативно: строка не докупит лишнего).
    /// </summary>
    public async Task<HistoryFill?> FillsFromHistoryAsync(
        VenueSession session, ulong userId, TradeOrder order, TradePlanLine line, DateTime nowUtc, CancellationToken ct)
    {
        if (_fills is null || _meta is null || !session.CanManageOrders || line.LotSize <= 0m) return null;
        var meta = (await _meta.GetByKeysAsync(Brokers.TInvest, [line.InstrumentKey]) ?? []).FirstOrDefault();
        if (meta is null || (string.IsNullOrWhiteSpace(meta.Uid) && string.IsNullOrWhiteSpace(meta.Figi))) return null;

        var since = order.CreatedAt - HistoryWindowBefore;
        var fills = (await _fills.GetFillsAsync(userId, session.BrokerAccountId, since, ct))
            .Where(f => f.Side == line.Side && f.AtUtc >= since
                        && ((!string.IsNullOrWhiteSpace(meta.Uid) && string.Equals(f.InstrumentUid, meta.Uid, StringComparison.OrdinalIgnoreCase))
                            || (!string.IsNullOrWhiteSpace(meta.Figi) && string.Equals(f.Figi, meta.Figi, StringComparison.OrdinalIgnoreCase))))
            .ToList();
        return ToHistoryFill(fills.Select(f => (f.Pieces, f.PricePerPiece)).ToList(), line.LotSize, order.LotsRequested);
    }

    /// <summary>Штуки сделок → целые лоты (не больше <paramref name="maxLots"/>) и средняя цена штуки (по сделкам с ценой); лотов 0 — null.</summary>
    internal static HistoryFill? ToHistoryFill(IReadOnlyList<(decimal Pieces, decimal? Price)> fills, decimal lotSize, long maxLots)
    {
        var pieces = fills.Sum(f => f.Pieces);
        var lots = Math.Min(maxLots, (long)Math.Floor(pieces / lotSize));
        if (lots <= 0) return null;
        var priced = fills.Where(f => f.Price is > 0m).ToList();
        var pricedPieces = priced.Sum(f => f.Pieces);
        decimal? avg = pricedPieces > 0m ? priced.Sum(f => f.Pieces * f.Price!.Value) / pricedPieces : null;
        return new HistoryFill(lots, avg);
    }

    public string Broker => Brokers.TInvest;
    public string Title => "T-Invest";
    public bool FeesFromStatement => false;
    public VenueLimits Limits(ExecutionOptions options) => new(options.MaxAttempts, options.RepriceAfter, options.MaxFeeRechecks);

    private async Task<Account?> PrimaryAsync(ulong userId) =>
        (await _accounts.GetByUserIdAndBrokerAsync(userId, Brokers.TInvest)).OrderBy(a => a.Id).FirstOrDefault();

    public async Task<VenueSession> OpenForManagementAsync(ulong userId)
    {
        var primary = await PrimaryAsync(userId);
        return new VenueSession { Broker = Broker, AccountId = primary?.Id ?? 0, BrokerAccountId = primary?.BrokerAccountId ?? string.Empty };
    }

    public async Task<VenueSession> OpenAsync(ulong userId, CancellationToken ct)
    {
        var primary = await PrimaryAsync(userId);
        VenueSession Halt(string reason) => new() { Broker = Broker, AccountId = primary?.Id ?? 0, BrokerAccountId = primary?.BrokerAccountId ?? string.Empty, HaltReason = reason };

        var gate = await _gate.ResolveAsync(userId);
        if (!gate.Live) return Halt(string.Join("; ", gate.Reasons));
        if (string.IsNullOrWhiteSpace(primary?.BrokerAccountId)) return Halt("Основной счёт T-Invest не привязан");

        // Сверка торгового токена у брокера на КАЖДОМ тике: токен мог быть перевыпущен на другой счёт/права, а read-токен — перепривязать основной счёт.
        IReadOnlyList<TInvestTradeAccount> tokenAccounts;
        try
        {
            tokenAccounts = await _gateway.GetAccountsAsync(userId, ct);
        }
        catch (TInvestGatewayException ex) when (ex.Kind == GatewayErrorKind.Transient)
        {
            _logger.LogWarning("Execution: T-Invest accounts check unavailable for user {UserId} ({Code}) — tick skipped", userId, ex.Code);
            return new VenueSession { Broker = Broker, AccountId = primary.Id, BrokerAccountId = primary.BrokerAccountId!, Unavailable = true };
        }
        catch (TInvestGatewayException ex)
        {
            tokenAccounts = [];
            _logger.LogWarning("Execution: trade token rejected for user {UserId} ({Code})", userId, ex.Code);
        }

        var check = TInvestTradeCredentialValidator.Evaluate(tokenAccounts, primary.BrokerAccountId!);
        if (!check.Ok)
        {
            await _credentialRecords.UpdateStatusAsync(userId, CredentialProviders.TInvestTrade, CredentialStatus.Invalid, Truncate(check.Message, 255));
            return Halt("Торговый токен не прошёл проверку: " + check.Message);
        }

        return new VenueSession { Broker = Broker, AccountId = primary.Id, BrokerAccountId = primary.BrokerAccountId! };
    }

    public Task<ExecutionGateResult> GateAsync(ulong userId) => _gate.ResolveAsync(userId);

    public string NewClientOrderId() => Guid.NewGuid().ToString("D");

    public string? LineRejection(TradePlan plan, TradePlanLine line) => null;

    public string? InstrumentId(TradePlanLine line, InstrumentTradingMeta meta) => string.IsNullOrWhiteSpace(meta.Uid) ? null : meta.Uid;

    public PolicyMarket MarketOf(TradePlanLine line, InstrumentTradingMeta? meta) => TradingHours.MarketOf(Brokers.TInvest, line.Ticker, meta?.ClassCode);

    public async Task<VenueMarket> MarketAsync(
        VenueSession session, ulong userId, TradePlanLine line, InstrumentTradingMeta meta, string instrumentId, DateTime nowUtc, CancellationToken ct)
    {
        var book = await _gateway.GetOrderBookAsync(userId, instrumentId, ct);
        var trading = await _gateway.GetTradingStatusAsync(userId, instrumentId, ct);
        var positions = await _gateway.GetPositionsAsync(userId, session.BrokerAccountId, ct);
        return new VenueMarket(
            book.Bid, book.Ask, book.TimestampUtc, trading.NormalTrading, trading.LimitOrderAvailable, trading.ApiTradeAvailable && meta.ApiTradeAvailable,
            positions.MoneyAvailable.TryGetValue(ExecutionText.NormalizeCurrency(line.Currency), out var money) ? money : null,
            positions.SecuritiesAvailablePieces.TryGetValue(instrumentId, out var pieces) ? pieces : null);
    }

    public Task<GatewayOrderState> PostAsync(VenueSession session, ulong userId, TradePlanLine line, string instrumentId, TradeOrder order, CancellationToken ct) =>
        _gateway.PostLimitOrderAsync(
            userId, new GatewayLimitOrder(session.BrokerAccountId, instrumentId, line.Side, order.LotsRequested, order.LimitPrice, order.PriceUnit, order.ClientOrderId), ct);

    public async Task<GatewayOrderState?> StateAsync(VenueSession session, ulong userId, TradeOrder order, TradePlanLine? line, bool searchActive, CancellationToken ct)
    {
        var state = await _gateway.GetOrderStateAsync(userId, session.BrokerAccountId, order.ClientOrderId, ct);
        if (state is null && searchActive)
            state = (await _gateway.GetActiveOrdersAsync(userId, session.BrokerAccountId, ct))
                .FirstOrDefault(o => string.Equals(o.ClientOrderId, order.ClientOrderId, StringComparison.OrdinalIgnoreCase));
        return state;
    }

    public Task CancelAsync(VenueSession session, ulong userId, TradeOrder order, CancellationToken ct) =>
        _gateway.CancelOrderAsync(userId, session.BrokerAccountId, order.ClientOrderId, ct);

    public async Task<VenueFees> RefreshFeesAsync(
        VenueSession session, ulong userId, TradePlanLine line, IReadOnlyList<TradeOrder> lineOrders, DateTime nowUtc, CancellationToken ct)
    {
        if (!session.CanManageOrders) return new VenueFees(false, null, null);
        foreach (var o in lineOrders.Where(o => o.LotsFilled > 0 && (o.ExecutedCommission ?? 0m) == 0m))
        {
            try
            {
                if (await _gateway.GetOrderStateAsync(userId, session.BrokerAccountId, o.ClientOrderId, ct) is { ExecutedCommission: { } fee } && fee != 0m)
                {
                    o.ExecutedCommission = fee;
                    o.UpdatedAt = nowUtc;
                }
            }
            catch (TInvestGatewayException ex)
            {
                _logger.LogInformation("Execution: commission re-read of {ClientOrderId} unavailable ({Code})", o.ClientOrderId, ex.Code);
            }
        }

        return new VenueFees(true, null, null);
    }

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}

/// <summary>
/// Freedom (задача 66). Ворота — <see cref="IExecutionGateResolver.ResolveForBrokerAsync"/> (ключ <c>freedom_trade</c>); на каждом тике торговый ключ сверяется у брокера:
/// его <c>trader_systems_id</c> = счёт Freedom пользователя (привязан синком по ключу на чтение). Отказы ДО сети: <c>.ITS</c> и любой тикер, кроме <c>.US</c>/<c>.EU</c>;
/// лотность ≠ 1; облигационные цены (пункты); план, подтверждённый автоматически (Freedom исполняется только после РУЧНОГО подтверждения, <c>FreedomAutoUsd = 0</c>).
/// <para>
/// Цены — валюта бумаги за штуку (<c>limit_price</c>), сетка — <c>min_step</c> из торговых метаданных. Свежесть котировки — по меткам брокера
/// (<see cref="FreedomMarketQuote.QuoteTimeUtc"/>: <c>ltt</c>/<c>bbt</c>/<c>bat</c>, МСК) против <c>Execution:MaxQuoteAge</c>; «обычные торги» — <c>marketStatus = OPEN</c> бумаги.
/// Деньги: остаток валюты из портфеля МИНУС все активные заявки Freedom пользователя в этой валюте (брокер может не вычитать заблокированное — консервативно,
/// продажи тоже вычитаются).
/// </para>
/// <para>
/// Идемпотентность: <c>client_order_id</c> = числовой <c>user_order_id</c> (9 цифр, 7xxxxxxxx — вне диапазона авто-id брокера <c>api:&lt;unix-секунда&gt;</c>); ответа нет —
/// см. <see cref="StateAsync"/>. Комиссия — из выписки: сделки заявки (<c>getTradesHistory</c>, связь по <c>order_id</c>) → строки <c>commission_for_trades</c> «(Trade N …)»
/// → факт, ВАЛЮТА комиссии и маршрут → <see cref="FreedomFeeReconciliation"/> по фактическому маршруту.
/// </para>
/// </summary>
public sealed class FreedomExecutionVenue : IExecutionVenue
{
    /// <summary>Сколько последних движений денег читать при сверке комиссии.</summary>
    public const int CashFlowsTake = 200;

    private readonly IFreedomOrderGateway _gateway;
    private readonly IExecutionGateResolver _gate;
    private readonly IAccountRepository _accounts;
    private readonly IUserCredentialRepository _credentialRecords;
    private readonly ITradeOrderRepository _orders;
    private readonly ILogger _logger;

    public FreedomExecutionVenue(
        IFreedomOrderGateway gateway, IExecutionGateResolver gate, IAccountRepository accounts, IUserCredentialRepository credentialRecords, ITradeOrderRepository orders,
        ILogger logger)
    {
        _gateway = gateway;
        _gate = gate;
        _accounts = accounts;
        _credentialRecords = credentialRecords;
        _orders = orders;
        _logger = logger;
    }

    public string Broker => Brokers.Freedom;
    public string Title => "Freedom";
    public bool FeesFromStatement => true;
    public VenueLimits Limits(ExecutionOptions options) => new(options.FreedomMaxAttempts, options.FreedomRepriceAfter, options.FreedomMaxFeeRechecks);

    private async Task<Account?> AccountAsync(ulong userId) =>
        (await _accounts.GetByUserIdAndBrokerAsync(userId, Brokers.Freedom)).OrderBy(a => a.Id).FirstOrDefault();

    public async Task<VenueSession> OpenForManagementAsync(ulong userId)
    {
        var account = await AccountAsync(userId);
        return new VenueSession { Broker = Broker, AccountId = account?.Id ?? 0, BrokerAccountId = account?.BrokerAccountId ?? string.Empty };
    }

    public async Task<VenueSession> OpenAsync(ulong userId, CancellationToken ct)
    {
        var account = await AccountAsync(userId);
        VenueSession Halt(string reason) => new() { Broker = Broker, AccountId = account?.Id ?? 0, BrokerAccountId = account?.BrokerAccountId ?? string.Empty, HaltReason = reason };

        var gate = await _gate.ResolveForBrokerAsync(userId, Brokers.Freedom);
        if (!gate.Live) return Halt(string.Join("; ", gate.Reasons));
        if (string.IsNullOrWhiteSpace(account?.BrokerAccountId)) return Halt("Счёт Freedom не привязан (нужен синк по ключу на чтение)");

        FreedomTradeAccountInfo info;
        try
        {
            info = await _gateway.GetAccountInfoAsync(userId, ct);
        }
        catch (ExecutionGatewayException ex) when (ex.Kind == GatewayErrorKind.Transient)
        {
            _logger.LogWarning("Execution: Freedom trade key check unavailable for user {UserId} ({Code}) — Freedom lines wait", userId, ex.Code);
            return new VenueSession { Broker = Broker, AccountId = account.Id, BrokerAccountId = account.BrokerAccountId!, Unavailable = true };
        }
        catch (ExecutionGatewayException ex)
        {
            _logger.LogWarning("Execution: Freedom trade key rejected for user {UserId} ({Code})", userId, ex.Code);
            await _credentialRecords.UpdateStatusAsync(userId, CredentialProviders.FreedomTrade, CredentialStatus.Invalid, "Freedom не принял торговый ключ");
            return Halt("Торговый ключ Freedom не прошёл проверку: брокер не принял ключ");
        }

        if (!string.Equals(info.AccountNumber?.Trim(), account.BrokerAccountId!.Trim(), StringComparison.Ordinal))
        {
            const string message = "Торговый ключ Freedom от другого счёта";
            await _credentialRecords.UpdateStatusAsync(userId, CredentialProviders.FreedomTrade, CredentialStatus.Invalid, message);
            return Halt("Торговый ключ Freedom не прошёл проверку: " + message);
        }

        return new VenueSession { Broker = Broker, AccountId = account.Id, BrokerAccountId = account.BrokerAccountId!, TariffId = info.TariffId };
    }

    public Task<ExecutionGateResult> GateAsync(ulong userId) => _gate.ResolveForBrokerAsync(userId, Brokers.Freedom);

    public string NewClientOrderId() => FreedomOrderRules.NewUserOrderId();

    public string? LineRejection(TradePlan plan, TradePlanLine line)
    {
        if (plan.Status == PlanStatuses.AutoApproved || plan.DecidedVia == DecidedVia.Auto)
            return "Freedom исполняется только после ручного подтверждения (авто-исполнение Freedom выключено)";
        if (FreedomOrderRules.TickerRejection(line.Ticker) is { } ticker) return ticker;
        if (line.LotSize != 1m) return "лотность бумаги Freedom должна быть 1";
        if (OrderPricing.IsPointsPriced(line)) return "цена в пунктах не поддерживается для Freedom";
        return null;
    }

    public string? InstrumentId(TradePlanLine line, InstrumentTradingMeta meta) =>
        FreedomOrderRules.IsTradableTicker(line.Ticker) ? line.Ticker.Trim().ToUpperInvariant() : null;

    public PolicyMarket MarketOf(TradePlanLine line, InstrumentTradingMeta? meta) => TradingHours.MarketOf(Brokers.Freedom, line.Ticker, meta?.ClassCode);

    public async Task<VenueMarket> MarketAsync(
        VenueSession session, ulong userId, TradePlanLine line, InstrumentTradingMeta meta, string instrumentId, DateTime nowUtc, CancellationToken ct)
    {
        var quote = await _gateway.GetQuoteAsync(userId, instrumentId, ct);
        var snapshot = await _gateway.GetAccountSnapshotAsync(userId, ct);
        var currency = ExecutionText.NormalizeCurrency(line.Currency);

        // Валюта котировки должна совпадать с валютой строки (цена плана — в валюте бумаги), иначе цены несравнимы — как «нет котировки».
        var sameCurrency = quote.Currency is null || string.Equals(quote.Currency, currency, StringComparison.OrdinalIgnoreCase);
        var bid = sameCurrency ? quote.Bid : null;
        var ask = sameCurrency ? quote.Ask : null;

        decimal? money = null;
        if (snapshot.Cash.TryGetValue(currency, out var cash))
        {
            var reserved = (await _orders.ListActiveByUserAsync(userId, Brokers.Freedom))
                .Where(o => string.Equals(o.Currency, currency, StringComparison.OrdinalIgnoreCase))
                .Sum(o => Math.Max(0, o.LotsRequested - o.LotsFilled) * o.LimitPrice);
            money = cash - reserved;
        }

        // Момент котировки — метка брокера (ltt/bbt/bat), не момент запроса; обычные торги — marketStatus бумаги = OPEN (нет поля — не подтверждено → preflight откажет).
        return new VenueMarket(
            bid, ask, quote.QuoteTimeUtc, quote.MarketOpen == true, true,
            meta.ApiTradeAvailable && FreedomOrderRules.IsTradableTicker(instrumentId),
            money, snapshot.PiecesByTicker.TryGetValue(instrumentId, out var pieces) ? pieces : null);
    }

    public Task<GatewayOrderState> PostAsync(VenueSession session, ulong userId, TradePlanLine line, string instrumentId, TradeOrder order, CancellationToken ct) =>
        _gateway.PostLimitOrderAsync(userId, new FreedomLimitOrder(instrumentId, line.Side, order.LotsRequested, order.LimitPrice, order.ClientOrderId), ct);

    /// <summary>Окно сверки по реквизитам: размещение у брокера не раньше <c>created_at − 30 с</c> (расхождение часов) …</summary>
    public static readonly TimeSpan ReconcileWindowBefore = TimeSpan.FromSeconds(30);

    /// <summary>… и не позже <c>created_at + 2 мин</c> (запрос уходит сразу после записи строки).</summary>
    public static readonly TimeSpan ReconcileWindowAfter = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Заявка известна по id брокера — ищется в списке ВСЕХ заявок брокера (<see cref="IFreedomOrderGateway.GetOrdersAsync"/>, activeOnly = false) по id. Ответа на размещение не
    /// было (<c>New</c> без id):
    /// <list type="number">
    /// <item>точное совпадение номера: <c>userOrderId</c> = <c>N</c> или <c>api:N</c> (<see cref="FreedomOrderRules.UserOrderIdMatches"/>) — ровно одна непривязанная заявка;</item>
    /// <item>иначе — по реквизитам (<see cref="Candidates"/>), только среди заявок с пустым <c>userOrderId</c> или в форме API (ручные заявки терминала/приложения —
    /// <c>i-6:ios:…</c> — исключены, как и заявки с ДРУГИМ нашим номером); результат помечается <see cref="GatewayOrderState.MatchedByAttributes"/> — исполнитель шлёт
    /// уведомление в бот.</item>
    /// </list>
    /// Кандидаты, уже привязанные к другой строке <c>trade_orders</c>, исключаются. Не опознана однозначно — null (движок по таймауту ставит <c>unknown_state</c>,
    /// повторно НЕ отправляет).
    /// </summary>
    public async Task<GatewayOrderState?> StateAsync(VenueSession session, ulong userId, TradeOrder order, TradePlanLine? line, bool searchActive, CancellationToken ct)
    {
        var orders = await _gateway.GetOrdersAsync(userId, activeOnly: false, ct);
        if (!string.IsNullOrEmpty(order.BrokerOrderId))
            return orders.FirstOrDefault(o => string.Equals(o.BrokerOrderId, order.BrokerOrderId, StringComparison.Ordinal));

        var exact = await UnlinkedAsync(orders.Where(o => FreedomOrderRules.UserOrderIdMatches(o.ClientOrderId, order.ClientOrderId)), order);
        if (exact.Count == 1)
        {
            _logger.LogInformation("Execution: Freedom order {ClientOrderId} reconciled to broker order {BrokerOrderId} by userOrderId", order.ClientOrderId, exact[0].BrokerOrderId);
            return exact[0];
        }

        if (exact.Count > 1)
        {
            _logger.LogWarning("Execution: Freedom order {ClientOrderId} is ambiguous ({Count} broker orders with our userOrderId) — not adopted", order.ClientOrderId, exact.Count);
            return null;
        }

        if (line is null || InstrumentIdOf(line) is not { } ticker) return null;

        // Шаг цены — из котировки (min_step); нет — сравнение цены точное (строже — безопаснее).
        var step = (await _gateway.GetQuoteAsync(userId, ticker, ct)).MinStep;
        var unlinked = await UnlinkedAsync(Candidates(orders, order, ticker, line.Side, step), order);
        if (unlinked.Count == 1)
        {
            _logger.LogWarning("Execution: Freedom order {ClientOrderId} adopted broker order {BrokerOrderId} BY ATTRIBUTES", order.ClientOrderId, unlinked[0].BrokerOrderId);
            return unlinked[0] with { MatchedByAttributes = true };
        }

        if (unlinked.Count > 1)
            _logger.LogWarning("Execution: Freedom order {ClientOrderId} is ambiguous ({Count} matching broker orders) — not adopted", order.ClientOrderId, unlinked.Count);
        return null;
    }

    private async Task<List<GatewayOrderState>> UnlinkedAsync(IEnumerable<GatewayOrderState> candidates, TradeOrder order)
    {
        var result = new List<GatewayOrderState>();
        foreach (var c in candidates)
            if (!await _orders.IsBrokerOrderLinkedAsync(Brokers.Freedom, c.BrokerOrderId, order.Id)) result.Add(c);
        return result;
    }

    private static string? InstrumentIdOf(TradePlanLine line) =>
        FreedomOrderRules.IsTradableTicker(line.Ticker) ? line.Ticker.Trim().ToUpperInvariant() : null;

    /// <summary>
    /// Кандидаты по реквизитам для заявки без id брокера (чистая функция): <c>userOrderId</c> пуст или в форме API и не чужой наш номер
    /// (<see cref="FreedomOrderRules.MayBeOursByAttributes"/>); тот же тикер (<c>instr</c>), та же сторона (<c>oper</c> 1/3), то же количество (<c>q</c>), лимит (<c>p</c>)
    /// в пределах половины шага <paramref name="step"/> (нет шага — точно), время размещения у брокера в окне [<c>created_at</c> − <see cref="ReconcileWindowBefore"/>,
    /// <c>created_at</c> + <see cref="ReconcileWindowAfter"/>] (нет времени — не кандидат). Привязку к другим строкам проверяет вызывающий.
    /// </summary>
    internal static IReadOnlyList<GatewayOrderState> Candidates(
        IReadOnlyList<GatewayOrderState> orders, TradeOrder order, string ticker, string side, decimal? step)
    {
        var tolerance = step is > 0m ? step.Value / 2m : 0m;
        return orders.Where(o =>
                FreedomOrderRules.MayBeOursByAttributes(o.ClientOrderId, order.ClientOrderId)
                && string.Equals(o.Ticker, ticker, StringComparison.OrdinalIgnoreCase)
                && string.Equals(o.Side, side, StringComparison.Ordinal)
                && o.LotsRequested == order.LotsRequested
                && o.LimitPrice is { } p && Math.Abs(p - order.LimitPrice) <= tolerance
                && o.PlacedAtUtc is { } at && at >= order.CreatedAt - ReconcileWindowBefore && at <= order.CreatedAt + ReconcileWindowAfter)
            .ToList();
    }

    /// <summary>
    /// Задача 67 (#17): заявка с известным id брокера пропала из списка заявок — исполненное по <c>getTradesHistory</c> (связь сделки с заявкой по
    /// <c>order_id</c>, как при сверке комиссии). Без id брокера сделки к заявке не привязать — null.
    /// </summary>
    public async Task<HistoryFill?> FillsFromHistoryAsync(
        VenueSession session, ulong userId, TradeOrder order, TradePlanLine line, DateTime nowUtc, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(order.BrokerOrderId)) return null;
        var from = DateOnly.FromDateTime(order.CreatedAt).AddDays(-1);
        var to = DateOnly.FromDateTime(nowUtc).AddDays(1);
        var trades = (await _gateway.GetTradesAsync(userId, from, to, ct))
            .Where(t => string.Equals(t.OrderId, order.BrokerOrderId, StringComparison.Ordinal))
            .Select(t => (t.Quantity, (decimal?)t.Price))
            .ToList();
        return TInvestExecutionVenue.ToHistoryFill(trades, line.LotSize > 0m ? line.LotSize : 1m, order.LotsRequested);
    }

    public Task CancelAsync(VenueSession session, ulong userId, TradeOrder order, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(order.BrokerOrderId))
            throw new FreedomGatewayException(GatewayErrorKind.Rejected, "no_broker_order_id", "id заявки у брокера ещё неизвестен — снять нельзя до сверки");
        return _gateway.CancelOrderAsync(userId, order.BrokerOrderId, ct);
    }

    public async Task<VenueFees> RefreshFeesAsync(
        VenueSession session, ulong userId, TradePlanLine line, IReadOnlyList<TradeOrder> lineOrders, DateTime nowUtc, CancellationToken ct)
    {
        var filled = lineOrders.Where(o => o.LotsFilled > 0).ToList();
        if (filled.Count == 0) return new VenueFees(true, null, null);
        if (filled.Any(o => string.IsNullOrEmpty(o.BrokerOrderId))) return new VenueFees(false, null, null);

        IReadOnlyList<Bonds.Infrastructure.Connectors.Freedom.Models.FreedomTrade> trades;
        IReadOnlyList<Bonds.Infrastructure.Connectors.Freedom.Models.FreedomCashFlow> flows;
        try
        {
            var from = DateOnly.FromDateTime(filled.Min(o => o.CreatedAt)).AddDays(-1);
            var to = DateOnly.FromDateTime(nowUtc).AddDays(1);
            trades = await _gateway.GetTradesAsync(userId, from, to, ct);
            flows = await _gateway.GetCashFlowsAsync(userId, CashFlowsTake, ct);
        }
        catch (ExecutionGatewayException ex)
        {
            _logger.LogInformation("Execution: Freedom fee statement unavailable ({Code})", ex.Code);
            return new VenueFees(false, null, null);
        }

        var commissions = new Dictionary<string, (decimal Fee, FreedomCommissionComment Comment, string Currency)>(StringComparer.Ordinal);
        foreach (var f in flows.Where(f => string.Equals(f.TypeCode, "commission_for_trades", StringComparison.OrdinalIgnoreCase)))
        {
            if (FreedomCommissionParser.Parse(f.Comment) is not { } c) continue;
            var prev = commissions.TryGetValue(c.TradeId, out var p) ? p.Fee : 0m;
            commissions[c.TradeId] = (prev + Math.Abs(f.Amount), c, ExecutionText.NormalizeCurrency(f.Currency));
        }

        var tariff = session.TariffId ?? 0;
        var complete = true;
        decimal? expectedTotal = 0m;
        var routes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var order in filled)
        {
            var orderTrades = trades.Where(t => string.Equals(t.OrderId, order.BrokerOrderId, StringComparison.Ordinal)).OrderBy(t => t.Date).ToList();
            if (orderTrades.Count == 0)
            {
                complete = false;
                continue;
            }

            // Сверка исполненного с историей сделок (по order_id): история показала больше, чем список заявок, — берём историю (лоты = штуки, лот 1).
            var qty = orderTrades.Sum(t => t.Quantity);
            var historyLots = (long)Math.Floor(qty);
            if (historyLots != order.LotsFilled)
            {
                _logger.LogWarning(
                    "Execution: Freedom order {BrokerOrderId}: filled {Filled} by order list, {History} by trades history", order.BrokerOrderId, order.LotsFilled, historyLots);
                if (historyLots > order.LotsFilled && historyLots <= order.LotsRequested) order.LotsFilled = historyLots;
            }

            if (qty > 0m) order.AvgFillPrice = orderTrades.Sum(t => t.Price * t.Quantity) / qty;
            var feeCurrencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var items = orderTrades.Select(t =>
            {
                var has = commissions.TryGetValue(t.Id, out var c);
                if (has) feeCurrencies.Add(c.Currency);
                return new FreedomFilledTrade(t.Id, t.Quantity, t.Amount, has ? c.Fee : null, has ? c.Comment.Route : null);
            }).ToList();
            foreach (var t in orderTrades)
                if (commissions.TryGetValue(t.Id, out var c)) routes.Add(c.Comment.MarketCode + (c.Comment.SecurityType is { } st ? " " + st : string.Empty));

            var check = FreedomFeeReconciliation.Check(tariff, order.Currency, items);
            if (!check.Complete)
            {
                complete = false;
                continue;
            }

            // Валюта комиссии — своя (у Freedom обычно USD даже для сделки в EUR): хранится отдельно (commission_currency), в валюту строки НЕ пересчитывается.
            // Несколько валют комиссии у одной заявки — валюта не задана (сумма разнородна): движок не сравнивает её с моделью.
            order.ExecutedCommission = check.ActualFee;
            order.CommissionCurrency = feeCurrencies.Count == 1 ? feeCurrencies.First() : TradeOrder.MixedCommissionCurrency;
            order.UpdatedAt = nowUtc;
            var sameCurrency = feeCurrencies.Count == 1 && string.Equals(feeCurrencies.First(), order.Currency, StringComparison.OrdinalIgnoreCase);
            expectedTotal = sameCurrency && check.ExpectedFee is { } e && expectedTotal is { } acc ? acc + e : null;
        }

        var note = routes.Count == 0 ? null : "маршрут " + string.Join(", ", routes);
        return new VenueFees(complete, complete ? expectedTotal : null, note);
    }
}

/// <summary>Общие текстовые нормализации исполнителя.</summary>
public static class ExecutionText
{
    public static string NormalizeCurrency(string currency)
    {
        var upper = currency.Trim().ToUpperInvariant();
        return upper == "RUR" ? "RUB" : upper;
    }

    public static string NormalizeBroker(string broker) => broker.Trim().ToLowerInvariant();
}
