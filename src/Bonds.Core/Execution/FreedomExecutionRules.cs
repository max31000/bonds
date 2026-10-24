using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Bonds.Core.Fees;
using Bonds.Core.Policy;

namespace Bonds.Core.Execution;

/// <summary>
/// Правила заявок Freedom (Tradernet, задача 66) — чистые функции, проверяются ДО любого сетевого вызова (и ещё раз внутри шлюза).
/// <list type="bullet">
/// <item><c>action_id</c> — ТОЛЬКО из белого списка <see cref="AllowedActionIds"/> {1 покупка, 3 продажа}; 2 и 4 — маржинальные варианты (покупка в кредит,
/// короткая продажа), отправить их нельзя никаким путём.</item>
/// <item>Тикер — только <c>.US</c>/<c>.EU</c>; <c>.ITS</c> — котировочный двойник, торговля по нему брокером отклоняется (маршрут на ITS брокер выбирает сам).</item>
/// <item>Тип заявки — только лимитная (<see cref="LimitOrderTypeId"/> = 2), срок — день (<see cref="DayExpirationId"/> = 1).</item>
/// </list>
/// </summary>
public static class FreedomOrderRules
{
    public const int BuyActionId = 1;
    public const int SellActionId = 3;

    /// <summary>Разрешённые <c>action_id</c>. 2 (покупка с маржой) и 4 (продажа в шорт) НЕ входят и не войдут: маржа запрещена дизайном §5.</summary>
    public static readonly IReadOnlySet<int> AllowedActionIds = new HashSet<int> { BuyActionId, SellActionId };

    /// <summary><c>order_type_id</c> = 2 — лимитная заявка (рыночных нет).</summary>
    public const int LimitOrderTypeId = 2;

    /// <summary><c>expiration_id</c> = 1 — до конца торгового дня.</summary>
    public const int DayExpirationId = 1;

    /// <summary>Сторона строки → <c>action_id</c> (только 1/3). Неизвестная сторона — исключение (не угадываем).</summary>
    public static int ActionIdFor(string side) => side switch
    {
        PlanSides.Buy => BuyActionId,
        PlanSides.Sell => SellActionId,
        _ => throw new ArgumentOutOfRangeException(nameof(side), side, "Неизвестная сторона заявки"),
    };

    public static bool IsAllowedActionId(int actionId) => AllowedActionIds.Contains(actionId);

    /// <summary>Котировочный тикер ITS (<c>*.ITS</c>) — торговать нельзя.</summary>
    public static bool IsItsTicker(string? ticker) => ticker?.Trim().EndsWith(".ITS", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Торгуемый тикер: <c>*.US</c> или <c>*.EU</c> (непустая база, без пробелов).</summary>
    public static bool IsTradableTicker(string? ticker)
    {
        if (string.IsNullOrWhiteSpace(ticker)) return false;
        var t = ticker.Trim().ToUpperInvariant();
        if (t.Any(char.IsWhiteSpace) || IsItsTicker(t)) return false;
        return (t.EndsWith(".US", StringComparison.Ordinal) && t.Length > 3) || (t.EndsWith(".EU", StringComparison.Ordinal) && t.Length > 3);
    }

    /// <summary>Причина, по которой тикер нельзя отправить (null — можно).</summary>
    public static string? TickerRejection(string? ticker) =>
        IsItsTicker(ticker) ? $"тикер {ticker} — котировочный двойник ITS, торговать нужно .US (маршрут на ITS брокер выбирает сам)"
        : IsTradableTicker(ticker) ? null
        : $"тикер {ticker} не торгуется исполнителем (только .US/.EU)";

    /// <summary>Нижняя граница <c>user_order_id</c> (10 цифр — не путается с авто-id брокера вида <c>api:…</c> по форме и не начинается с нуля).</summary>
    public const long MinUserOrderId = 1_000_000_000L;

    /// <summary>Верхняя граница (исключительно): <c>user_order_id</c> держим в int32 — тип поля у брокера не документирован, авто-id брокера ~1.79·10⁹.</summary>
    public const long MaxUserOrderIdExclusive = int.MaxValue;

    /// <summary>
    /// Новый <c>user_order_id</c> — случайное число в [<see cref="MinUserOrderId"/>, <see cref="MaxUserOrderIdExclusive"/>) (криптографический ГСЧ). Записывается в
    /// <c>trade_orders.client_order_id</c> ДО отправки (UNIQUE — совпадение даст исключение вставки, и заявка не уйдёт).
    /// </summary>
    public static string NewUserOrderId() =>
        RandomNumberGenerator.GetInt32((int)MinUserOrderId, int.MaxValue).ToString(CultureInfo.InvariantCulture);

    /// <summary><c>user_order_id</c> корректен: только цифры, в допустимом диапазоне.</summary>
    public static bool IsValidUserOrderId(string? id) =>
        id is not null && id.All(char.IsAsciiDigit) && long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
        && n >= MinUserOrderId && n < MaxUserOrderIdExclusive;

    /// <summary>
    /// Значение <c>user_order_id</c> из ответа брокера соответствует нашему: точное совпадение либо форма <c>префикс:число</c> (брокер показывает авто-id как
    /// <c>api:1792764255</c>; как он покажет переданный нами — проверяется на первой живой заявке, см. CODEBASE-GUIDE). Пусто — не совпадает никогда.
    /// </summary>
    public static bool UserOrderIdMatches(string? brokerValue, string ours)
    {
        if (string.IsNullOrWhiteSpace(brokerValue) || string.IsNullOrWhiteSpace(ours)) return false;
        var v = brokerValue.Trim();
        if (string.Equals(v, ours, StringComparison.Ordinal)) return true;
        var colon = v.LastIndexOf(':');
        return colon >= 0 && string.Equals(v[(colon + 1)..], ours, StringComparison.Ordinal);
    }
}

/// <summary>
/// Статусы заявки Tradernet (<c>stat</c>) → <see cref="TradeOrderStatuses"/>. <b>Консервативно:</b> окончательными считаются только хорошо известные коды
/// (3 исполнена, 4 снята, 7 отклонена); 2 — частично исполнена; ЛЮБОЙ другой код (1 принята, 10 активна, 6 отменяется и неизвестные) — <c>Submitted</c>, т.е.
/// «может исполняться»: исполнитель продолжит сверку и снятие, но не перевыставит строку, пока брокер не подтвердит окончательный статус. Ошибка в сторону
/// «активна» безопасна (лишняя сверка), в сторону «окончательна» — нет (новая попытка при живой старой).
/// </summary>
public static class FreedomOrderStatusMap
{
    public const int PartiallyFilled = 2;
    public const int Filled = 3;
    public const int Cancelled = 4;
    public const int Rejected = 7;

    public static string Map(int? stat) => stat switch
    {
        Filled => TradeOrderStatuses.Filled,
        Cancelled => TradeOrderStatuses.Cancelled,
        Rejected => TradeOrderStatuses.Rejected,
        PartiallyFilled => TradeOrderStatuses.PartiallyFilled,
        _ => TradeOrderStatuses.Submitted,
    };

    /// <summary>
    /// Статус с учётом исполненного количества: <c>leaves_qty = 0</c> при полном исполнении — Filled даже при незнакомом коде; активная заявка с исполнением —
    /// PartiallyFilled. Штуки = лоты (у Freedom лот = 1).
    /// </summary>
    public static string Resolve(int? stat, long requested, long executed)
    {
        var mapped = Map(stat);
        if (requested > 0 && executed >= requested) return TradeOrderStatuses.Filled;
        if (mapped == TradeOrderStatuses.Submitted && executed > 0) return TradeOrderStatuses.PartiallyFilled;
        return mapped;
    }
}

/// <summary>Строка комиссии из <c>getUserCashFlows</c> (<c>commission_for_trades</c>): номер сделки, сторона, тикер и ФАКТИЧЕСКИЙ маршрут из комментария.</summary>
public sealed record FreedomCommissionComment(string TradeId, string Side, string Ticker, string? MarketCode, string? SecurityType)
{
    /// <summary>Маршрут для <see cref="FreedomFeeModel"/>; null — рынок в комментарии не распознан.</summary>
    public FreedomMarket? Route => FreedomCommissionParser.RouteOf(MarketCode, SecurityType);
}

/// <summary>
/// Разбор комментария комиссии: <c>(Trade 900000101 buy SPYM.US ) Рынок: kazakhstan_its, тип бумаги: funds, ...</c>. Рынок → маршрут модели:
/// <c>europe</c> → Europe; <c>kazakhstan_its</c> → ITS (фонды — <c>funds</c> — отдельная ставка); <c>usa</c>/<c>us</c> → Usa; <c>kase</c>/<c>kazakhstan</c> →
/// Kazakhstan; <c>aix</c> → AIX; <c>otc</c> → OTC; прочее — неизвестно (null).
/// </summary>
public static class FreedomCommissionParser
{
    private static readonly Regex TradeRx = new(@"\(\s*Trade\s+(\d+)\s+(buy|sell)\s+([^\s\)]+)\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex MarketRx = new(@"(?:Рынок|Market)\s*:\s*([A-Za-z_]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex TypeRx = new(@"(?:тип бумаги|security type|instrument type)\s*:\s*([A-Za-z_]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static FreedomCommissionComment? Parse(string? comment)
    {
        if (string.IsNullOrWhiteSpace(comment)) return null;
        var m = TradeRx.Match(comment);
        if (!m.Success) return null;
        var market = MarketRx.Match(comment);
        var type = TypeRx.Match(comment);
        return new FreedomCommissionComment(
            m.Groups[1].Value,
            m.Groups[2].Value.Equals("sell", StringComparison.OrdinalIgnoreCase) ? PlanSides.Sell : PlanSides.Buy,
            m.Groups[3].Value.ToUpperInvariant(),
            market.Success ? market.Groups[1].Value.ToLowerInvariant() : null,
            type.Success ? type.Groups[1].Value.ToLowerInvariant() : null);
    }

    public static FreedomMarket? RouteOf(string? marketCode, string? securityType)
    {
        var isFund = string.Equals(securityType, "funds", StringComparison.OrdinalIgnoreCase) || string.Equals(securityType, "fund", StringComparison.OrdinalIgnoreCase);
        return marketCode?.ToLowerInvariant() switch
        {
            "europe" or "eu" => FreedomMarket.Europe,
            "kazakhstan_its" or "its" => isFund ? FreedomMarket.KazakhstanItsFunds : FreedomMarket.KazakhstanIts,
            "usa" or "us" => FreedomMarket.Usa,
            "kase" or "kazakhstan" => FreedomMarket.Kazakhstan,
            "aix" or "kazakhstan_aix" => FreedomMarket.KazakhstanAix,
            "otc" => FreedomMarket.Otc,
            _ => null,
        };
    }
}

/// <summary>Сделка заявки для сверки комиссии: штуки, сумма в валюте сделки, фактическая комиссия (по модулю) и маршрут из комментария комиссии.</summary>
public sealed record FreedomFilledTrade(string TradeId, decimal Quantity, decimal Amount, decimal? Commission, FreedomMarket? Route);

/// <summary>Итог сверки комиссии заявки Freedom по выписке.</summary>
public sealed record FreedomFeeCheck(decimal ActualFee, decimal? ExpectedFee, FreedomMarket? Route, bool Complete);

/// <summary>
/// Сверка комиссии Freedom (задача 66): ФАКТ — Σ строк <c>commission_for_trades</c> по сделкам заявки; МОДЕЛЬ — <see cref="FreedomFeeModel.Fee"/> по ФАКТИЧЕСКОМУ маршруту
/// из комментария каждой строки, фикс за заявку (<c>perOrder</c>, $1.2 на US/EU) — один раз на заявку (в первой сделке). <b>Единицы:</b> суммы и комиссии — доллары
/// США (модель — в USD; для заявок не в USD модель не применяется — <see cref="FreedomFeeCheck.ExpectedFee"/> = null, сверка идёт по оценке плана).
/// </summary>
public static class FreedomFeeReconciliation
{
    public static FreedomFeeCheck Check(int tariffId, string currency, IReadOnlyList<FreedomFilledTrade> trades)
    {
        var complete = trades.Count > 0 && trades.All(t => t.Commission is not null);
        var actual = trades.Sum(t => t.Commission ?? 0m);
        var routes = trades.Select(t => t.Route).Distinct().ToList();
        var route = routes.Count == 1 ? routes[0] : null;
        decimal? expected = null;
        if (complete && trades.All(t => t.Route is not null)
            && string.Equals(currency?.Trim(), "USD", StringComparison.OrdinalIgnoreCase)
            && tariffId is FreedomFeeModel.InvestorTariffId or FreedomFeeModel.StandardTariffId)
        {
            expected = trades.Select((t, i) => FreedomFeeModel.Fee(tariffId, t.Route!.Value, t.Amount, t.Quantity, includePerOrder: i == 0)).Sum();
        }

        return new FreedomFeeCheck(actual, expected, route, complete);
    }
}
