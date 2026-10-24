using System.Globalization;
using System.Net;
using System.Text.Json;
using Bonds.Core.Brokers;
using Bonds.Core.Credentials;
using Bonds.Core.Execution;
using Bonds.Core.Models;
using Bonds.Infrastructure.Connectors.Freedom;
using Bonds.Infrastructure.Connectors.Freedom.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bonds.Infrastructure.Execution;

/// <summary>
/// Реализация <see cref="IFreedomOrderGateway"/> (задача 66) — ЕДИНСТВЕННЫЙ файл, где есть команды заявок Tradernet: <c>putTradeOrder</c> (размещение), <c>delTradeOrder</c>
/// (снятие), <c>getNotifyOrderJson</c> (список заявок). Чтение (профиль, котировки, портфель, сделки, движения денег) — через <see cref="IFreedomApiClient"/> с ТОРГОВЫМ ключом.
/// Подписанный транспорт общий с клиентом на чтение (<see cref="FreedomSignedHttp"/>, HttpClient <c>freedom</c>, без ретраев). Scoped: ключ читается один раз на scope.
/// <para>
/// <b>Классификация ответа на размещение</b> (ошибка в сторону «неизвестно» безопасна — исполнитель только сверяет и НЕ отправляет повторно):
/// сеть/таймаут, HTTP 5xx/408/429, не-JSON, ответ без <c>order_id</c> — <see cref="GatewayErrorKind.Transient"/> (заявка МОЖЕТ существовать); поле <c>error</c>/<c>errMsg</c>
/// в ответе или HTTP 4xx — <see cref="GatewayErrorKind.Rejected"/> (заявки нет).
/// </para>
/// <para>
/// <b>Тело размещения:</b> <c>{instr_name, action_id, order_type_id: 2, qty, limit_price, expiration_id: 1, user_order_id}</c>; <c>action_id</c> — только из
/// <see cref="FreedomOrderRules.AllowedActionIds"/> (1 покупка, 3 продажа); <c>limit_price</c> — валюта бумаги за штуку; <c>qty</c> — штуки.
/// </para>
/// </summary>
public sealed class FreedomOrderGateway : IFreedomOrderGateway
{
    internal const string PutCommand = "putTradeOrder";
    internal const string CancelCommand = "delTradeOrder";
    internal const string OrdersCommand = "getNotifyOrderJson";

    private readonly IUserCredentialStore _credentials;
    private readonly IExecutionGateResolver _gate;
    private readonly IFreedomApiClient _client;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly ILogger<FreedomOrderGateway> _logger;
    private readonly TimeSpan _orderTimeOffset;
    private readonly Dictionary<ulong, FreedomCredentials> _scopeKeys = new();

    public FreedomOrderGateway(
        IUserCredentialStore credentials, IExecutionGateResolver gate, IFreedomApiClient client, IHttpClientFactory httpClientFactory, TimeProvider time,
        ILogger<FreedomOrderGateway> logger, IOptions<FreedomOptions>? options = null)
    {
        _orderTimeOffset = TimeSpan.FromHours((options?.Value ?? new FreedomOptions()).OrderTimeUtcOffsetHours);
        _credentials = credentials;
        _gate = gate;
        _client = client;
        _http = httpClientFactory.CreateClient(FreedomApiClient.HttpClientName);
        _time = time;
        _logger = logger;
    }

    private async Task<FreedomCredentials> KeysForAsync(ulong userId)
    {
        if (_scopeKeys.TryGetValue(userId, out var cached)) return cached;
        var secret = await _credentials.GetSecretResultAsync(userId, CredentialProviders.FreedomTrade);
        if (secret.Outcome != CredentialReadOutcome.Ok || secret.Fields is null
            || !secret.Fields.TryGetValue("publicKey", out var pub) || string.IsNullOrWhiteSpace(pub)
            || !secret.Fields.TryGetValue("privateKey", out var priv) || string.IsNullOrWhiteSpace(priv))
        {
            throw new FreedomGatewayException(GatewayErrorKind.Rejected, "no_trade_key", "Торговый ключ Freedom не задан или не расшифровался");
        }

        var keys = new FreedomCredentials(pub.Trim(), priv.Trim());
        _scopeKeys[userId] = keys;
        return keys;
    }

    // ─── Чтение (через клиент на чтение, торговым ключом) ─────────────────────────────────

    public async Task<FreedomTradeAccountInfo> GetAccountInfoAsync(ulong userId, CancellationToken ct = default)
    {
        var keys = await KeysForAsync(userId);
        var info = await ReadAsync(() => _client.GetUserInfoAsync(keys, ct), "GetAllUserTexInfo");
        return new FreedomTradeAccountInfo(info.AccountNumber, info.TariffId);
    }

    public async Task<FreedomMarketQuote> GetQuoteAsync(ulong userId, string ticker, CancellationToken ct = default)
    {
        var keys = await KeysForAsync(userId);
        var quotes = await ReadAsync(() => _client.GetQuotesAsync(keys, [ticker], ct), "getStockQuotesJson");
        var q = quotes.FirstOrDefault(x => string.Equals(x.Ticker, ticker, StringComparison.OrdinalIgnoreCase));
        return q is null
            ? new FreedomMarketQuote(null, null, null, null)
            : new FreedomMarketQuote(
                q.Bid is > 0m ? q.Bid : null, q.Ask is > 0m ? q.Ask : null, q.MinStep, BrokerAssetClassifier.NormalizeCurrency(q.Currency),
                QuoteTimeUtc(q, _orderTimeOffset, _time.GetUtcNow().UtcDateTime),
                q.MarketStatus is null ? null : string.Equals(q.MarketStatus.Trim(), "OPEN", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Момент котировки (UTC) для <c>Execution:MaxQuoteAge</c>: самая поздняя из <c>ltt</c>/<c>bbt</c>/<c>bat</c> (МСК без пояса − <paramref name="offset"/>), отбрасывая
    /// метки позже <paramref name="nowUtc"/> + 1 мин (мусор/расхождение часов). Нет ни одной — null (preflight: «несвежая котировка»). Застывшие <c>bbt</c>/<c>bat</c>
    /// (у валют — 2023 год) на результат не влияют — берётся максимум.
    /// </summary>
    internal static DateTime? QuoteTimeUtc(FreedomQuote q, TimeSpan offset, DateTime nowUtc)
    {
        DateTime? best = null;
        foreach (var local in new[] { q.LastTradeLocal, q.BidTimeLocal, q.AskTimeLocal })
        {
            if (local is not { } l) continue;
            var utc = DateTime.SpecifyKind(l - offset, DateTimeKind.Utc);
            if (utc > nowUtc.AddMinutes(1)) continue;
            if (best is null || utc > best) best = utc;
        }

        return best;
    }

    public async Task<FreedomAccountSnapshot> GetAccountSnapshotAsync(ulong userId, CancellationToken ct = default)
    {
        var keys = await KeysForAsync(userId);
        var p = await ReadAsync(() => _client.GetPortfolioAsync(keys, ct), "getPositionJson");
        var cash = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in p.Cash)
        {
            var currency = BrokerAssetClassifier.NormalizeCurrency(c.Currency);
            if (currency is null) continue;
            cash[currency] = cash.GetValueOrDefault(currency) + c.Amount;
        }

        var pieces = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var pos in p.Positions.Where(x => !string.IsNullOrWhiteSpace(x.Ticker)))
            pieces[pos.Ticker.Trim().ToUpperInvariant()] = pieces.GetValueOrDefault(pos.Ticker.Trim().ToUpperInvariant()) + pos.Quantity;
        return new FreedomAccountSnapshot(cash, pieces);
    }

    public async Task<IReadOnlyList<FreedomTrade>> GetTradesAsync(ulong userId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var keys = await KeysForAsync(userId);
        return await ReadAsync(() => _client.GetTradesAsync(keys, from, to, ct), "getTradesHistory");
    }

    public async Task<IReadOnlyList<FreedomCashFlow>> GetCashFlowsAsync(ulong userId, int take, CancellationToken ct = default)
    {
        var keys = await KeysForAsync(userId);
        return await ReadAsync(() => _client.GetCashFlowsAsync(keys, take, ct), "getUserCashFlows");
    }

    private static async Task<T> ReadAsync<T>(Func<Task<T>> call, string cmd)
    {
        try
        {
            return await call();
        }
        catch (FreedomApiException ex)
        {
            // Сеть/таймаут/5xx/408/429/«too many requests» — повтор на следующем тике (ключ НЕ инвалидируется); прочее (отказ брокера, неверный ключ, формат) — отказ.
            var transient = ex.IsTransient;
            throw new FreedomGatewayException(transient ? GatewayErrorKind.Transient : GatewayErrorKind.Rejected, transient ? "unavailable" : "rejected", $"Freedom {cmd}: недоступно или отказ брокера");
        }
    }

    // ─── Заявки ───────────────────────────────────────────────────────────────────────────

    /// <summary>Параметры размещения (порядок ключей фиксирован — он входит в подпись). <c>action_id</c> — только 1/3, иначе исключение до сети.</summary>
    internal static IReadOnlyList<KeyValuePair<string, object>> BuildPutParams(FreedomLimitOrder order, long userOrderId)
    {
        var actionId = FreedomOrderRules.ActionIdFor(order.Side);
        if (!FreedomOrderRules.IsAllowedActionId(actionId))
            throw new FreedomGatewayException(GatewayErrorKind.Rejected, "margin_forbidden", "Маржинальные заявки запрещены");
        return
        [
            new("instr_name", order.Ticker.Trim().ToUpperInvariant()),
            new("action_id", actionId),
            new("order_type_id", FreedomOrderRules.LimitOrderTypeId),
            new("qty", order.Quantity),
            new("limit_price", order.LimitPrice),
            new("expiration_id", FreedomOrderRules.DayExpirationId),
            new("user_order_id", userOrderId),
        ];
    }

    public async Task<GatewayOrderState> PostLimitOrderAsync(ulong userId, FreedomLimitOrder order, CancellationToken ct = default)
    {
        // Последний рубеж — всё до любого сетевого вызова: ворота Freedom, сторона (белый список action_id), тикер, корректность.
        var gate = await _gate.ResolveForBrokerAsync(userId, Brokers.Freedom);
        if (!gate.Live)
            throw new FreedomGatewayException(GatewayErrorKind.Rejected, "gate_closed", "Исполнение Freedom закрыто: " + string.Join("; ", gate.Reasons));
        if (order.Side is not (Bonds.Core.Policy.PlanSides.Buy or Bonds.Core.Policy.PlanSides.Sell))
            throw new FreedomGatewayException(GatewayErrorKind.Rejected, "invalid_order", "Неизвестная сторона заявки");
        if (FreedomOrderRules.TickerRejection(order.Ticker) is { } tickerError)
            throw new FreedomGatewayException(GatewayErrorKind.Rejected, FreedomOrderRules.IsItsTicker(order.Ticker) ? "its_forbidden" : "ticker_forbidden", tickerError);
        if (order.Quantity <= 0 || order.LimitPrice <= 0m || !FreedomOrderRules.IsValidUserOrderId(order.UserOrderId))
            throw new FreedomGatewayException(GatewayErrorKind.Rejected, "invalid_order", "Некорректная заявка (количество, цена или user_order_id)");

        var parameters = BuildPutParams(order, long.Parse(order.UserOrderId, NumberStyles.None, CultureInfo.InvariantCulture));
        var keys = await KeysForAsync(userId);
        _logger.LogInformation(
            "Posting Freedom limit order {UserOrderId}: {Side} {Qty} pc of {Ticker} at {Price}", order.UserOrderId, order.Side, order.Quantity, order.Ticker, order.LimitPrice);

        using var doc = await SendOrderCommandAsync(keys, PutCommand, parameters, ct);
        var root = doc.RootElement;
        ThrowIfBrokerError(root, PutCommand, keys);

        var brokerId = Str(root, "order_id") ?? (Obj(root, "order") is { } o1 ? Str(o1, "order_id") : null);
        if (string.IsNullOrWhiteSpace(brokerId))
            throw new FreedomGatewayException(GatewayErrorKind.Transient, "no_order_id", "Freedom: в ответе на заявку нет order_id — исход неизвестен, будет сверка");

        var state = Obj(root, "order") is { } o ? ParseOrder(o, _orderTimeOffset, WarnUnknownStat) : null;

        // Эхо user_order_id в ответе: наш номер как есть или «api:N». Иное (авто-id брокера — брокер не принял наш) — не ошибка: заявка привязана по order_id ответа.
        var echoed = state?.ClientOrderId;
        if (!string.IsNullOrWhiteSpace(echoed) && !FreedomOrderRules.UserOrderIdMatches(echoed, order.UserOrderId))
            _logger.LogWarning("Freedom putTradeOrder: broker echoed another user_order_id for {UserOrderId} — order linked by order_id {BrokerOrderId}", order.UserOrderId, brokerId);

        return state is null
            ? new GatewayOrderState(brokerId, order.UserOrderId, TradeOrderStatuses.Submitted, order.Quantity, 0, null, null, null, null)
            : state with { BrokerOrderId = brokerId, ClientOrderId = order.UserOrderId };
    }

    private void WarnUnknownStat(string brokerOrderId, string? stat) =>
        _logger.LogWarning("Freedom order {BrokerOrderId}: unknown stat {Stat} — treated as active (never final)", brokerOrderId, stat ?? "null");

    public async Task<IReadOnlyList<GatewayOrderState>> GetOrdersAsync(ulong userId, bool activeOnly, CancellationToken ct = default)
    {
        var keys = await KeysForAsync(userId);
        using var doc = await SendOrderCommandAsync(keys, OrdersCommand, [new("active_only", activeOnly ? 1 : 0)], ct);
        ThrowIfBrokerError(doc.RootElement, OrdersCommand, keys);
        return ParseOrders(doc.RootElement, _orderTimeOffset, WarnUnknownStat);
    }

    public async Task CancelOrderAsync(ulong userId, string brokerOrderId, CancellationToken ct = default)
    {
        if (!long.TryParse(brokerOrderId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            throw new FreedomGatewayException(GatewayErrorKind.Rejected, "invalid_order", "Некорректный id заявки брокера");
        var keys = await KeysForAsync(userId);
        _logger.LogInformation("Cancelling Freedom order {BrokerOrderId}", brokerOrderId);
        using var doc = await SendOrderCommandAsync(keys, CancelCommand, [new("order_id", id)], ct);
        ThrowIfBrokerError(doc.RootElement, CancelCommand, keys);
    }

    private async Task<JsonDocument> SendOrderCommandAsync(
        FreedomCredentials keys, string cmd, IReadOnlyList<KeyValuePair<string, object>> parameters, CancellationToken ct)
    {
        HttpStatusCode status;
        string body;
        try
        {
            (status, body) = await FreedomSignedHttp.SendAsync(_http, _time, keys, cmd, parameters, ct);
        }
        catch (HttpRequestException)
        {
            _logger.LogWarning("Freedom {Cmd}: network error", cmd);
            throw new FreedomGatewayException(GatewayErrorKind.Transient, "network", $"Freedom {cmd}: сетевая ошибка");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Freedom {Cmd}: timeout", cmd);
            throw new FreedomGatewayException(GatewayErrorKind.Transient, "timeout", $"Freedom {cmd}: таймаут");
        }

        var code = (int)status;
        if (code is < 200 or > 299)
        {
            _logger.LogWarning("Freedom {Cmd} failed: HTTP {Status}", cmd, code);
            var transient = code >= 500 || code is 408 or 429;
            throw new FreedomGatewayException(transient ? GatewayErrorKind.Transient : GatewayErrorKind.Rejected, "http_" + code, $"Freedom {cmd}: HTTP {code}");
        }

        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new FreedomGatewayException(GatewayErrorKind.Transient, "bad_response", $"Freedom {cmd}: ответ не JSON — исход неизвестен");
        }
    }

    /// <summary>Поле <c>error</c>/<c>errMsg</c> в ответе — определённый отказ брокера (текст маскируется и обрезается).</summary>
    private static void ThrowIfBrokerError(JsonElement root, string cmd, FreedomCredentials keys)
    {
        try
        {
            FreedomParser.ThrowIfError(cmd, root, keys.PublicKey, keys.PrivateKey);
        }
        catch (FreedomApiException ex) when (FreedomApiException.IsTransientText(ex.Message))
        {
            // Перегрузка/лимит частоты — не отказ: для размещения исход неизвестен (сверка), для чтения/снятия — повтор.
            throw new FreedomGatewayException(GatewayErrorKind.Transient, "rate_limited", ex.Message.Trim());
        }
        catch (FreedomApiException ex)
        {
            throw new FreedomGatewayException(GatewayErrorKind.Rejected, "broker_rejected", ex.Message.Trim());
        }
    }

    // ─── Разбор заявок ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Список заявок. Живой ответ подписанного v3 (<c>/api/getNotifyOrderJson</c>, проверено на счёте) — <c>{result: {orders: {key, order: [...]}}}</c> С обёрткой
    /// <c>result</c>; допускаются и ответ без обёртки (<c>{orders: …}</c>), <c>order</c> одним объектом и <c>orders</c>-массив. <paramref name="offset"/> — смещение
    /// локального времени брокера (МСК) от UTC; <paramref name="onUnknownStat"/> — вызывается для кода <c>stat</c> вне официального списка (заявка остаётся активной).
    /// </summary>
    internal static IReadOnlyList<GatewayOrderState> ParseOrders(JsonElement root, TimeSpan offset, Action<string, string?>? onUnknownStat = null)
    {
        var holder = Obj(root, "result") ?? root;
        if (!holder.TryGetProperty("orders", out var orders)) return [];
        IEnumerable<JsonElement> items = orders.ValueKind switch
        {
            JsonValueKind.Array => orders.EnumerateArray(),
            JsonValueKind.Object when orders.TryGetProperty("order", out var inner) => inner.ValueKind switch
            {
                JsonValueKind.Array => inner.EnumerateArray(),
                JsonValueKind.Object => [inner],
                _ => [],
            },
            _ => [],
        };
        return items.Where(e => e.ValueKind == JsonValueKind.Object).Select(e => ParseOrder(e, offset, onUnknownStat)).Where(s => s is not null).Select(s => s!).ToList();
    }

    /// <summary>
    /// Заявка Tradernet → <see cref="GatewayOrderState"/>. Id брокера — <c>order_id</c>, иначе <c>id</c> (на живом счёте совпадают). Номер клиента — <c>userOrderId</c>
    /// (camelCase, как отдаёт <c>getNotifyOrderJson</c>: <c>api:N</c> у заявок через API, <c>i-6:ios:…</c> у ручных из приложения/терминала), иначе <c>user_order_id</c>.
    /// Сделки — массив <c>trade: [{id, p, q, v, date}]</c>: исполнено = Σ <c>q</c>, средняя цена штуки = VWAP (Σ p·q / Σ q); статус —
    /// <see cref="FreedomOrderStatusMap.Resolve"/> по официальным кодам (<c>leaves_qty</c> — только запасной вариант для АКТИВНЫХ кодов без массива <c>trade</c>).
    /// Комиссии в заявке нет (null — берётся из выписки). Реквизиты для сверки без id: <c>instr</c>, <c>oper</c> (1 → Buy, 3 → Sell; маржинальные 2/4 и прочее — null),
    /// <c>p</c> (валюта бумаги за штуку), <c>date</c> (МСК без пояса: <c>2026-10-23T16:46:41.883</c> или <c>2026-10-23 17:04:15</c>) → UTC = date − <paramref name="offset"/>.
    /// null — у записи нет id.
    /// </summary>
    internal static GatewayOrderState? ParseOrder(JsonElement e, TimeSpan offset, Action<string, string?>? onUnknownStat = null)
    {
        var id = Str(e, "order_id") ?? Str(e, "id");
        if (string.IsNullOrWhiteSpace(id)) return null;
        var statRaw = Dec(e, "stat");
        var stat = statRaw is { } s && s == decimal.Truncate(s) && s is >= int.MinValue and <= int.MaxValue ? (int?)(int)s : null;
        var q = (long)Math.Floor(Dec(e, "q") ?? 0m);
        var fills = TradesOf(e);
        var resolution = FreedomOrderStatusMap.Resolve(stat, q, Dec(e, "leaves_qty"), fills?.Qty);
        if (!resolution.KnownCode) onUnknownStat?.Invoke(id.Trim(), statRaw?.ToString(CultureInfo.InvariantCulture));
        var status = resolution.Status;
        var executed = resolution.Executed;
        var error = Str(e, "error");
        var message = string.IsNullOrWhiteSpace(error) ? (status == TradeOrderStatuses.Rejected ? "Брокер отклонил заявку" : null) : Trim(error.Trim());
        var oper = Dec(e, "oper");
        var side = oper == FreedomOrderRules.BuyActionId ? Bonds.Core.Policy.PlanSides.Buy : oper == FreedomOrderRules.SellActionId ? Bonds.Core.Policy.PlanSides.Sell : null;
        DateTime? placedUtc = Str(e, "date") is { } raw
            && DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var local)
            && local.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(local - offset, DateTimeKind.Utc)
                : null;
        var userOrderId = Str(e, "userOrderId") ?? Str(e, "user_order_id");
        return new GatewayOrderState(
            id.Trim(), string.IsNullOrWhiteSpace(userOrderId) ? null : userOrderId.Trim(), status, q, executed, executed > 0 ? fills?.Vwap : null, null,
            BrokerAssetClassifier.NormalizeCurrency(Str(e, "cur")), message,
            Ticker: Str(e, "instr")?.Trim().ToUpperInvariant(), Side: side, LimitPrice: Dec(e, "p"), PlacedAtUtc: placedUtc);
    }

    /// <summary>
    /// Сделки заявки (<c>trade</c> — массив <c>{p, q}</c> или один объект): Σ <c>q</c> и VWAP цены штуки (null — нет сделок с ценой). Массива нет — null (тогда активные
    /// коды могут опереться на <c>leaves_qty</c>); пустой массив — (0, null).
    /// </summary>
    private static (decimal Qty, decimal? Vwap)? TradesOf(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("trade", out var arr)) return null;
        IEnumerable<JsonElement> items = arr.ValueKind switch
        {
            JsonValueKind.Array => arr.EnumerateArray(),
            JsonValueKind.Object => [arr],
            _ => [],
        };
        if (arr.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object)) return null;
        decimal qty = 0m, sum = 0m, priced = 0m;
        foreach (var t in items.Where(t => t.ValueKind == JsonValueKind.Object))
        {
            var n = Dec(t, "q");
            if (n is not > 0m) continue;
            qty += n.Value;
            var p = Dec(t, "p");
            if (p is > 0m)
            {
                sum += p.Value * n.Value;
                priced += n.Value;
            }
        }

        return (qty, priced > 0m ? sum / priced : null);
    }

    private static string? Trim(string? message) => string.IsNullOrWhiteSpace(message) ? null : message.Length <= 200 ? message : message[..200];

    private static JsonElement? Obj(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Object ? e : null;

    private static string? Str(JsonElement o, string name)
    {
        if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(name, out var e)) return null;
        return e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.GetRawText(),
            _ => null,
        };
    }

    private static decimal? Dec(JsonElement o, string name)
    {
        if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(name, out var e)) return null;
        return e.ValueKind switch
        {
            JsonValueKind.Number => e.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }
}
