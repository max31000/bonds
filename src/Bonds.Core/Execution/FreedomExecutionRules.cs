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

    /// <summary>
    /// Нижняя граница <c>user_order_id</c>: 9 цифр с фиксированной первой цифрой 7 — [700 000 000, 800 000 000). Авто-id брокера для заявок через API — <c>api:&lt;unix-секунда
    /// запроса&gt;</c> (живой счёт: <c>api:1792763201</c> ≈ 1.79·10⁹, растёт со временем); наш диапазон соответствует unix-секундам 1992 года, поэтому с авто-id брокера
    /// НЕ пересекается никогда (ни сейчас, ни в будущем). Тип параметра у SDK — int, держим число &lt; 10⁹.
    /// </summary>
    public const long MinUserOrderId = 700_000_000L;

    /// <summary>Верхняя граница (исключительно) — см. <see cref="MinUserOrderId"/>.</summary>
    public const long MaxUserOrderIdExclusive = 800_000_000L;

    /// <summary>
    /// Новый <c>user_order_id</c> — случайное число в [<see cref="MinUserOrderId"/>, <see cref="MaxUserOrderIdExclusive"/>) (криптографический ГСЧ). Записывается в
    /// <c>trade_orders.client_order_id</c> ДО отправки (UNIQUE — совпадение даст исключение вставки, и заявка не уйдёт).
    /// </summary>
    public static string NewUserOrderId() =>
        RandomNumberGenerator.GetInt32((int)MinUserOrderId, (int)MaxUserOrderIdExclusive).ToString(CultureInfo.InvariantCulture);

    /// <summary><c>user_order_id</c> корректен: только цифры, в допустимом диапазоне (наш).</summary>
    public static bool IsValidUserOrderId(string? id) =>
        id is not null && id.All(char.IsAsciiDigit) && long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
        && n >= MinUserOrderId && n < MaxUserOrderIdExclusive;

    /// <summary>Префикс <c>userOrderId</c> заявок, поданных через API (<c>api:N</c>).</summary>
    public const string ApiPrefix = "api:";

    /// <summary>
    /// Поле <c>userOrderId</c> заявки брокера (список заявок брокера, camelCase; в ответе на размещение — также <c>user_order_id</c>) соответствует нашему номеру: ровно
    /// <c>N</c> или <c>api:N</c>. Любая другая форма (<c>i-6:ios:terminal_exchange_currency</c> — ручная заявка из приложения/терминала, <c>api:&lt;другое&gt;</c>) — не совпадает.
    /// Пусто — не совпадает никогда.
    /// </summary>
    public static bool UserOrderIdMatches(string? brokerValue, string ours)
    {
        if (string.IsNullOrWhiteSpace(brokerValue) || string.IsNullOrWhiteSpace(ours)) return false;
        var v = brokerValue.Trim();
        var o = ours.Trim();
        return string.Equals(v, o, StringComparison.Ordinal)
               || (v.StartsWith(ApiPrefix, StringComparison.OrdinalIgnoreCase) && string.Equals(v[ApiPrefix.Length..], o, StringComparison.Ordinal));
    }

    /// <summary>
    /// Заявка брокера МОЖЕТ быть нашей при сверке по реквизитам: <c>userOrderId</c> пуст или в форме API (<c>api:N</c>/просто число), причём N — не другой НАШ номер (наш
    /// диапазон <see cref="MinUserOrderId"/>…, отличный от <paramref name="ours"/>, — это другая строка <c>trade_orders</c>). Любой иной формат (ручные заявки терминала/приложения:
    /// <c>i-6:ios:…</c>, <c>web:…</c>) — чужая, НИКОГДА не усыновляется.
    /// </summary>
    public static bool MayBeOursByAttributes(string? brokerValue, string ours)
    {
        if (string.IsNullOrWhiteSpace(brokerValue)) return true;
        var v = brokerValue.Trim();
        var digits = v.StartsWith(ApiPrefix, StringComparison.OrdinalIgnoreCase) ? v[ApiPrefix.Length..] : v;
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit)) return false;
        return !IsValidUserOrderId(digits) || string.Equals(digits, ours?.Trim(), StringComparison.Ordinal);
    }
}

/// <summary>Класс кода статуса заявки Tradernet (<c>stat</c>).</summary>
public enum FreedomStatClass
{
    /// <summary>Код не из официального списка (или его нет) — считается активной, исполнитель логирует предупреждение.</summary>
    Unknown,

    /// <summary>Активна, ничего не исполнено (1 получен, 2 в процессе отмены, 10 активен, 11 отправлен, 75 ошибка отмены).</summary>
    Active,

    /// <summary>Частично исполнена и ещё активна (12 частично заполнен, 20 частично исполнен).</summary>
    ActivePartial,

    /// <summary>Исполнена полностью (21) — окончательный.</summary>
    Filled,

    /// <summary>Снята/истекла, возможно частичное исполнение (30, 31, 71, 72) — окончательный; исполнено = Σ <c>trade[].q</c> (НЕ <c>q − leaves_qty</c>).</summary>
    Closed,

    /// <summary>Отклонена/ошибка отправки (0 проигнорирован, 70 отклонен, 74 ошибка отправки) — окончательный; исполнено — только по сделкам <c>trade[]</c>.</summary>
    Rejected,
}

/// <summary>Итог разбора статуса заявки Freedom: статус исполнителя, исполненные ШТУКИ (= лоты, лот 1) и признак, что код официальный.</summary>
public sealed record FreedomOrderResolution(string Status, long Executed, bool KnownCode);

/// <summary>
/// Статусы заявки Tradernet (<c>stat</c>, официальный список tradernet.global/tradernet-api/order-statuses) → <see cref="TradeOrderStatuses"/>.
/// <b>Исполненное количество</b> — Σ <c>q</c> сделок заявки (<c>trade[]</c> в списке заявок брокера). <c>leaves_qty</c> для окончательных кодов НЕ используется:
/// по семантике FIX у снятой заявки остаток = 0 (живой счёт: <c>leaves_qty = 0</c> и у исполненных, и у снятых), и <c>q − leaves_qty</c> выдал бы ложное «исполнено всё».
/// <list type="bullet">
/// <item>21 исполнен — <c>Filled</c>, исполнено = <c>q</c> (код окончательный и однозначный).</item>
/// <item>30 частично отменен, 31 отменен, 71 истек, 72 частично исполнен и истек — <c>Cancelled</c> (снята не нами: движок перевыставит остаток или закроет строку),
/// исполнено = Σ <c>trade[].q</c> (нет сделок — 0); сделки покрыли всё — <c>Filled</c>.</item>
/// <item>0 проигнорирован, 70 отклонен, 74 ошибка отправки — <c>Rejected</c>, исполнено 0; если сделки всё же есть — <c>Cancelled</c> с их количеством (окончательно,
/// исполненное учитывается). «Rejected → Filled по <c>leaves_qty</c>» больше нет.</item>
/// <item>1 получен, 2 в процессе отмены, 10 активен, 11 отправлен, 75 ошибка отмены — <c>Submitted</c> (с исполнением — <c>PartiallyFilled</c>).</item>
/// <item>12 частично заполнен, 20 частично исполнен — <c>PartiallyFilled</c>.</item>
/// <item>Активные коды: исполнено по сделкам; ТОЛЬКО если массива <c>trade</c> в ответе нет вовсе — запасной вариант <c>q − leaves_qty</c>.</item>
/// <item><b>Неизвестный код или его нет — активна, НИКОГДА не окончательна</b>: исполнитель продолжит сверку и снятие, но не перевыставит строку, пока брокер не покажет
/// официальный окончательный код. Ошибка в сторону «активна» безопасна (лишняя сверка), в сторону «окончательна» — нет.</item>
/// </list>
/// </summary>
public static class FreedomOrderStatusMap
{
    public const int Ignored = 0;
    public const int Received = 1;
    public const int Cancelling = 2;
    public const int Active = 10;
    public const int Sent = 11;
    public const int PartiallyPlaced = 12;
    public const int PartiallyFilled = 20;
    public const int Filled = 21;
    public const int PartiallyCancelled = 30;
    public const int Cancelled = 31;
    public const int Rejected = 70;
    public const int Expired = 71;
    public const int PartiallyFilledExpired = 72;
    public const int SendError = 74;
    public const int CancelError = 75;

    public static FreedomStatClass Classify(int? stat) => stat switch
    {
        Filled => FreedomStatClass.Filled,
        PartiallyCancelled or Cancelled or Expired or PartiallyFilledExpired => FreedomStatClass.Closed,
        Ignored or Rejected or SendError => FreedomStatClass.Rejected,
        Received or Cancelling or Active or Sent or CancelError => FreedomStatClass.Active,
        PartiallyPlaced or PartiallyFilled => FreedomStatClass.ActivePartial,
        _ => FreedomStatClass.Unknown,
    };

    public static bool IsKnown(int? stat) => Classify(stat) != FreedomStatClass.Unknown;

    /// <summary>
    /// Статус и исполненное по <c>stat</c>, запрошенным <paramref name="requested"/> (<c>q</c>), остатку <paramref name="leaves"/> (<c>leaves_qty</c>; null — поля нет) и
    /// Σ <c>trade[].q</c> <paramref name="tradedQty"/> (null — массива <c>trade</c> в ответе нет; пустой массив — 0). Штуки = лоты (у Freedom лот = 1); дробные — вниз.
    /// </summary>
    public static FreedomOrderResolution Resolve(int? stat, long requested, decimal? leaves, decimal? tradedQty)
    {
        var cls = Classify(stat);
        var max = Math.Max(0, requested);
        long Clamp(decimal n) => Math.Clamp((long)Math.Floor(n), 0, max);
        var traded = tradedQty is { } t ? Clamp(t) : 0;

        // Только для активных кодов и только без массива trade: q − leaves_qty.
        long ActiveExecuted() => tradedQty is not null ? traded : leaves is { } l ? Math.Clamp(requested - (long)Math.Ceiling(l), 0, max) : 0;
        var known = cls != FreedomStatClass.Unknown;
        switch (cls)
        {
            case FreedomStatClass.Filled:
                return new(TradeOrderStatuses.Filled, requested, true);
            case FreedomStatClass.Closed:
                return new(requested > 0 && traded >= requested ? TradeOrderStatuses.Filled : TradeOrderStatuses.Cancelled, traded, true);
            case FreedomStatClass.Rejected:
                return traded > 0 ? new(TradeOrderStatuses.Cancelled, traded, true) : new(TradeOrderStatuses.Rejected, 0, true);
            case FreedomStatClass.ActivePartial:
                return new(TradeOrderStatuses.PartiallyFilled, ActiveExecuted(), true);
            default:
            {
                // Active и Unknown: активна; исполнение (если есть) — PartiallyFilled, но НЕ Filled — окончательность только по официальному коду.
                var executed = ActiveExecuted();
                return new(executed > 0 ? TradeOrderStatuses.PartiallyFilled : TradeOrderStatuses.Submitted, executed, known);
            }
        }
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
