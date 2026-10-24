using Bonds.Core.Credentials;
using Bonds.Core.Execution;
using Bonds.Core.Policy;
using Bonds.Infrastructure.Connectors.TInvest;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tinkoff.InvestApi;
using Tinkoff.InvestApi.V1;

namespace Bonds.Infrastructure.Execution;

/// <summary>
/// Реализация <see cref="ITInvestOrderGateway"/> поверх официального SDK (<c>Tinkoff.InvestApi</c> 0.6.22.1, контракт сверен отражением сборки — см. README коннектора).
/// Клиент строится по торговому токену пользователя (<c>tinvest_trade</c> из <see cref="IUserCredentialStore"/>) и кэшируется на время scope; конфиг
/// <c>TInvest:Sandbox</c> переключает контур, как у синка. Токен не логируется и не попадает в исключения. Scoped.
/// <para>
/// <b>Цена заявки:</b> <see cref="GatewayLimitOrder.Price"/> переводится в <see cref="Quotation"/> ровно один раз — <see cref="TInvestNumeric.ToQuotation"/>; единицы задаёт
/// <c>PriceType</c>: облигации — <see cref="PriceType.Point"/> (пункты, процент номинала), остальное — <see cref="PriceType.Currency"/> (валюта за штуку).
/// </para>
/// </summary>
public sealed class TInvestOrderGateway : ITInvestOrderGateway
{
    private const string AppName = "bonds-portfolio-analytics";

    private readonly IUserCredentialStore _credentials;
    private readonly IExecutionGateResolver _gate;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TInvestOrderGateway> _logger;
    private readonly Dictionary<ulong, InvestApiClient> _clients = new();

    public TInvestOrderGateway(IUserCredentialStore credentials, IExecutionGateResolver gate, IConfiguration configuration, ILogger<TInvestOrderGateway> logger)
    {
        _credentials = credentials;
        _gate = gate;
        _configuration = configuration;
        _logger = logger;
    }

    private InvestApiClient CreateClient(string token) => InvestApiClientFactory.Create(new InvestApiSettings
    {
        AccessToken = token,
        AppName = AppName,
        Sandbox = _configuration.GetValue<bool?>("TInvest:Sandbox") ?? false,
    });

    private async Task<InvestApiClient> ClientForAsync(ulong userId)
    {
        if (_clients.TryGetValue(userId, out var cached)) return cached;
        var secret = await _credentials.GetSecretResultAsync(userId, CredentialProviders.TInvestTrade);
        if (secret.Outcome != CredentialReadOutcome.Ok || secret.Fields is null || !secret.Fields.TryGetValue("token", out var token) || string.IsNullOrWhiteSpace(token))
            throw new TInvestGatewayException(GatewayErrorKind.Rejected, "no_trade_token", "Торговый токен T-Invest не задан или не расшифровался");
        var client = CreateClient(token);
        _clients[userId] = client;
        return client;
    }

    public async Task<IReadOnlyList<TInvestTradeAccount>> ProbeAccountsAsync(string token, CancellationToken ct = default) =>
        await CallAsync(async () => MapAccounts(await CreateClient(token).Users.GetAccountsAsync(new GetAccountsRequest(), cancellationToken: ct)), "GetAccounts");

    public async Task<IReadOnlyList<TInvestTradeAccount>> GetAccountsAsync(ulong userId, CancellationToken ct = default)
    {
        var client = await ClientForAsync(userId);
        return await CallAsync(async () => MapAccounts(await client.Users.GetAccountsAsync(new GetAccountsRequest(), cancellationToken: ct)), "GetAccounts");
    }

    private static IReadOnlyList<TInvestTradeAccount> MapAccounts(GetAccountsResponse response) =>
        response.Accounts
            .Select(a => new TInvestTradeAccount(a.Id, a.AccessLevel == AccessLevel.AccountAccessLevelFullAccess, a.Status == AccountStatus.Open))
            .ToList();

    public async Task<GatewayOrderState> PostLimitOrderAsync(ulong userId, GatewayLimitOrder order, CancellationToken ct = default)
    {
        // Последний рубеж: ворота перепроверяются здесь же, непосредственно перед PostOrder (env, режим, токен, пауза).
        var gate = await _gate.ResolveAsync(userId);
        if (!gate.Live)
            throw new TInvestGatewayException(GatewayErrorKind.Rejected, "gate_closed", "Исполнение закрыто: " + string.Join("; ", gate.Reasons));
        if (order.Lots <= 0 || order.Price <= 0m || !Guid.TryParse(order.ClientOrderId, out _))
            throw new TInvestGatewayException(GatewayErrorKind.Rejected, "invalid_order", "Некорректная заявка");

        var client = await ClientForAsync(userId);
        var request = new PostOrderRequest
        {
            AccountId = order.AccountId,
            InstrumentId = order.InstrumentUid,
            Quantity = order.Lots,
            Price = TInvestNumeric.ToQuotation(order.Price),
            PriceType = order.PriceUnit == OrderPriceUnits.Points ? PriceType.Point : PriceType.Currency,
            Direction = order.Side == PlanSides.Sell ? OrderDirection.Sell : OrderDirection.Buy,
            OrderType = OrderType.Limit,
            OrderId = order.ClientOrderId,
            TimeInForce = TimeInForceType.TimeInForceDay,
        };
        _logger.LogInformation(
            "Posting limit order {ClientOrderId}: {Side} {Lots} lot(s) of {InstrumentUid} at {Price} {Unit}", order.ClientOrderId, order.Side, order.Lots, order.InstrumentUid, order.Price, order.PriceUnit);
        var response = await CallAsync(async () => await client.Orders.PostOrderAsync(request, cancellationToken: ct), "PostOrder");
        return new GatewayOrderState(
            response.OrderId, order.ClientOrderId, MapStatus(response.ExecutionReportStatus), response.LotsRequested, response.LotsExecuted,
            AvgPrice(response.ExecutedOrderPrice, response.LotsExecuted), response.ExecutedCommission.ToNullableDecimal(), CurrencyOf(response.ExecutedOrderPrice ?? response.InitialOrderPrice),
            Trim(response.Message));
    }

    public async Task<GatewayOrderState?> GetOrderStateAsync(ulong userId, string accountId, string clientOrderId, CancellationToken ct = default)
    {
        var client = await ClientForAsync(userId);
        try
        {
            var state = await client.Orders.GetOrderStateAsync(
                new GetOrderStateRequest { AccountId = accountId, OrderId = clientOrderId, OrderIdType = OrderIdType.Request }, cancellationToken: ct);
            return Map(state);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return null;
        }
        catch (RpcException ex)
        {
            throw Translate(ex, "GetOrderState");
        }
    }

    public async Task<IReadOnlyList<GatewayOrderState>> GetActiveOrdersAsync(ulong userId, string accountId, CancellationToken ct = default)
    {
        var client = await ClientForAsync(userId);
        var response = await CallAsync(async () => await client.Orders.GetOrdersAsync(new GetOrdersRequest { AccountId = accountId }, cancellationToken: ct), "GetOrders");
        return response.Orders.Select(Map).ToList();
    }

    public async Task CancelOrderAsync(ulong userId, string accountId, string clientOrderId, CancellationToken ct = default)
    {
        var client = await ClientForAsync(userId);
        _logger.LogInformation("Cancelling order {ClientOrderId}", clientOrderId);
        await CallAsync(
            async () => await client.Orders.CancelOrderAsync(
                new CancelOrderRequest { AccountId = accountId, OrderId = clientOrderId, OrderIdType = OrderIdType.Request }, cancellationToken: ct),
            "CancelOrder");
    }

    public async Task<GatewayTradingStatus> GetTradingStatusAsync(ulong userId, string instrumentUid, CancellationToken ct = default)
    {
        var client = await ClientForAsync(userId);
        var r = await CallAsync(async () => await client.MarketData.GetTradingStatusAsync(new GetTradingStatusRequest { InstrumentId = instrumentUid }, cancellationToken: ct), "GetTradingStatus");
        return new GatewayTradingStatus(r.TradingStatus == SecurityTradingStatus.NormalTrading, r.LimitOrderAvailableFlag, r.ApiTradeAvailableFlag);
    }

    public async Task<GatewayOrderBook> GetOrderBookAsync(ulong userId, string instrumentUid, CancellationToken ct = default)
    {
        var client = await ClientForAsync(userId);
        var r = await CallAsync(async () => await client.MarketData.GetOrderBookAsync(new GetOrderBookRequest { InstrumentId = instrumentUid, Depth = 1 }, cancellationToken: ct), "GetOrderBook");
        // Цены стакана — в единицах брокера (облигации — пункты), конверсии нет.
        var bid = r.Bids.Count > 0 ? r.Bids[0].Price.ToNullableDecimal() : null;
        var ask = r.Asks.Count > 0 ? r.Asks[0].Price.ToNullableDecimal() : null;
        return new GatewayOrderBook(bid, ask, r.OrderbookTs?.ToDateTime());
    }

    public async Task<GatewayPositions> GetPositionsAsync(ulong userId, string accountId, CancellationToken ct = default)
    {
        var client = await ClientForAsync(userId);
        var r = await CallAsync(async () => await client.Operations.GetPositionsAsync(new PositionsRequest { AccountId = accountId }, cancellationToken: ct), "GetPositions");
        var money = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in r.Money)
        {
            var currency = NormalizeCurrency(m.Currency);
            money[currency] = money.GetValueOrDefault(currency) + m.ToDecimal();
        }

        var securities = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var s in r.Securities.Where(s => !string.IsNullOrEmpty(s.InstrumentUid)))
            securities[s.InstrumentUid] = securities.GetValueOrDefault(s.InstrumentUid) + s.Balance;
        return new GatewayPositions(money, securities);
    }

    // ─── Маппинг и ошибки ──────────────────────────────────────────────────────────────────

    private static GatewayOrderState Map(OrderState s) => new(
        s.OrderId, string.IsNullOrEmpty(s.OrderRequestId) ? null : s.OrderRequestId, MapStatus(s.ExecutionReportStatus), s.LotsRequested, s.LotsExecuted,
        AvgPrice(s.ExecutedOrderPrice, s.LotsExecuted), s.ExecutedCommission.ToNullableDecimal(), CurrencyOf(s.ExecutedOrderPrice ?? s.InitialOrderPrice), null);

    /// <summary>
    /// <c>ExecutedOrderPrice</c> — средняя цена исполнения ОДНОГО инструмента (документация T-Invest), MoneyValue в валюте (облигации — уже рубли с учётом номинала,
    /// не пункты). Нет исполнения — null.
    /// </summary>
    private static decimal? AvgPrice(MoneyValue? executed, long lotsExecuted) => lotsExecuted > 0 ? executed.ToNullableDecimal() : null;

    private static string? CurrencyOf(MoneyValue? value) => string.IsNullOrEmpty(value?.Currency) ? null : NormalizeCurrency(value.Currency);

    private static string NormalizeCurrency(string currency)
    {
        var upper = currency.Trim().ToUpperInvariant();
        return upper == "RUR" ? "RUB" : upper;
    }

    internal static string MapStatus(OrderExecutionReportStatus status) => status switch
    {
        OrderExecutionReportStatus.ExecutionReportStatusFill => TradeOrderStatuses.Filled,
        OrderExecutionReportStatus.ExecutionReportStatusPartiallyfill => TradeOrderStatuses.PartiallyFilled,
        OrderExecutionReportStatus.ExecutionReportStatusRejected => TradeOrderStatuses.Rejected,
        OrderExecutionReportStatus.ExecutionReportStatusCancelled => TradeOrderStatuses.Cancelled,
        _ => TradeOrderStatuses.Submitted,
    };

    private static string? Trim(string? message) => string.IsNullOrWhiteSpace(message) ? null : message.Length <= 200 ? message : message[..200];

    private async Task<T> CallAsync<T>(Func<Task<T>> call, string method)
    {
        try
        {
            return await call();
        }
        catch (RpcException ex)
        {
            throw Translate(ex, method);
        }
    }

    /// <summary>
    /// gRPC-ошибка → <see cref="TInvestGatewayException"/>. Исход неизвестен (сеть/таймаут/перегрузка/внутренняя) — <see cref="GatewayErrorKind.Transient"/>; прочее —
    /// определённый отказ. В сообщение идёт только код и текст брокера (в нём нет токена — токен в заголовке запроса, не в ответе).
    /// </summary>
    private TInvestGatewayException Translate(RpcException ex, string method)
    {
        var transient = ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded or StatusCode.Internal or StatusCode.Unknown
            or StatusCode.ResourceExhausted or StatusCode.Cancelled or StatusCode.Aborted;
        _logger.LogWarning("T-Invest {Method} failed: {StatusCode}", method, ex.StatusCode);
        return new TInvestGatewayException(
            transient ? GatewayErrorKind.Transient : GatewayErrorKind.Rejected,
            ex.StatusCode.ToString(),
            $"T-Invest {method}: {ex.StatusCode} {Trim(ex.Status.Detail)}".Trim());
    }
}
