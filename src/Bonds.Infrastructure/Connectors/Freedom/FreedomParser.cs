using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Bonds.Infrastructure.Connectors.Freedom.Models;

namespace Bonds.Infrastructure.Connectors.Freedom;

/// <summary>
/// Разбор ответов Freedom API v3. Брокер смешивает числа и строки-числа ("1.27") — всё читается InvariantCulture.
/// Поле <c>currval</c> (рублей за единицу валюты) НЕ читается: у брокера оно бывает устаревшим.
/// </summary>
public static class FreedomParser
{
    /// <summary>Бросает <see cref="FreedomApiException"/>, если в корне есть непустое <c>errMsg</c>/<c>error</c>. Сообщение без секретов.</summary>
    public static void ThrowIfError(string cmd, JsonElement root, params string[] secrets)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var name in new[] { "errMsg", "error" })
        {
            if (!root.TryGetProperty(name, out var e)) continue;
            string? message = e.ValueKind switch
            {
                JsonValueKind.String when !string.IsNullOrWhiteSpace(e.GetString()) => e.GetString(),
                JsonValueKind.Number when e.GetDecimal() != 0 => "код ошибки " + e.GetRawText(),
                JsonValueKind.Object or JsonValueKind.Array when e.GetRawText() is not ("{}" or "[]") => e.GetRawText(),
                _ => null,
            };
            if (message is not null) throw new FreedomApiException(cmd, Scrub(message, secrets));
        }
    }

    /// <summary>Маскирует значения ключей, если брокер вдруг вернул их в тексте ошибки.</summary>
    public static string Scrub(string message, params string[] secrets)
    {
        foreach (var s in secrets.Where(s => !string.IsNullOrEmpty(s)))
        {
            message = message.Replace(s, "***", StringComparison.Ordinal);
        }

        return message.Length > 300 ? message[..300] : message;
    }

    public static FreedomUserInfo ParseUserInfo(JsonElement root)
    {
        var data = Obj(root, "data") ?? throw new FormatException("GetAllUserTexInfo: нет data");
        var tariff = Obj(data, "tariffDetails");
        var tariffId = tariff is { } t && Int(t, "id") is { } tid ? tid : (Int(data, "tariff") ?? 0);
        return new FreedomUserInfo(
            Str(data, "trader_systems_id") ?? Str(data, "id") ?? string.Empty,
            tariffId,
            tariff is { } tt ? Str(tt, "name") ?? string.Empty : string.Empty);
    }

    public static FreedomPortfolio ParsePortfolio(JsonElement root)
    {
        var ps = Obj(Obj(root, "result") ?? root, "ps") ?? throw new FormatException("getPositionJson: нет result.ps");
        var cash = Items(ps, "acc")
            .Select(a => new FreedomCashBalance(Str(a, "curr") ?? string.Empty, Dec(a, "s") ?? 0m))
            .ToList();
        var positions = Items(ps, "pos")
            .Select(p => new FreedomPosition(
                Str(p, "i") ?? string.Empty,
                Str(p, "name") ?? string.Empty,
                Dec(p, "q") ?? 0m,
                Str(p, "curr") ?? string.Empty,
                Dec(p, "bal_price_a") ?? 0m,
                Dec(p, "mkt_price") ?? 0m,
                Dec(p, "market_value") ?? 0m))
            .ToList();
        return new FreedomPortfolio(cash, positions);
    }

    public static IReadOnlyList<FreedomQuote> ParseQuotes(JsonElement root)
    {
        var holder = Obj(root, "result") ?? root;
        return Items(holder, "q")
            .Select(q => new FreedomQuote(
                Str(q, "c") ?? string.Empty, Dec(q, "bbp"), Dec(q, "bap"), Dec(q, "ltp"), Str(q, "x_curr"), Dec(q, "min_step"),
                LocalTime(q, "ltt"), LocalTime(q, "bbt"), LocalTime(q, "bat"), Str(q, "marketStatus")))
            .ToList();
    }

    /// <summary>Пустая история приходит не массивом (пустой объект/строка/отсутствует) — это пустой список, не ошибка.</summary>
    public static IReadOnlyList<FreedomTrade> ParseTrades(JsonElement root, ILogger? logger = null)
    {
        var trades = Obj(root, "trades");
        if (trades is null) return [];
        var result = new List<FreedomTrade>();
        foreach (var t in Items(trades.Value, "trade"))
        {
            // type: "1" = покупка, "2" = продажа; всё остальное пропускаем, а не угадываем сторону.
            var side = Str(t, "type") switch
            {
                "1" => FreedomTradeSide.Buy,
                "2" => FreedomTradeSide.Sell,
                _ => (FreedomTradeSide?)null,
            };
            if (side is null)
            {
                logger?.LogWarning("Freedom: сделка {TradeId} пропущена — неизвестный type {Type}", Str(t, "id"), Str(t, "type"));
                continue;
            }

            result.Add(new FreedomTrade(
                Str(t, "id") ?? string.Empty,
                Str(t, "order_id") ?? string.Empty,
                Date(t, "date"),
                Str(t, "instr_nm") ?? string.Empty,
                side.Value,
                Dec(t, "q") ?? 0m,
                Dec(t, "p") ?? 0m,
                Dec(t, "summ") ?? 0m,
                Str(t, "curr_c") ?? string.Empty,
                Dec(t, "commission") ?? 0m,
                Str(t, "commission_currency") ?? string.Empty));
        }

        return result;
    }

    public static IReadOnlyList<FreedomCashFlow> ParseCashFlows(JsonElement root) =>
        Items(root, "cashflow")
            .Select(c => new FreedomCashFlow(
                Str(c, "id") ?? string.Empty,
                Str(c, "type_code") ?? string.Empty,
                Date(c, "datetime", "date"),
                Str(c, "currency") ?? string.Empty,
                Dec(c, "sumRaw") ?? Dec(c, "sum") ?? 0m,
                Str(c, "comment") ?? string.Empty))
            .ToList();

    public static IReadOnlyList<FreedomInstrument> ParseFound(JsonElement root) =>
        Items(root, "found")
            .Select(f => new FreedomInstrument(
                Str(f, "t") ?? string.Empty, Str(f, "nm") ?? string.Empty, Str(f, "mkt"), Str(f, "codesub"), Str(f, "x_curr")))
            .ToList();

    // --- helpers ---------------------------------------------------------------------------------

    private static JsonElement? Obj(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Object ? e : null;

    /// <summary>Элементы массива <paramref name="name"/>; одиночный объект — как один элемент; всё остальное — пусто.</summary>
    private static IEnumerable<JsonElement> Items(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var e)) return [];
        return e.ValueKind switch
        {
            JsonValueKind.Array => e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).ToList(),
            JsonValueKind.Object when IsRealItem(e) => [e],
            _ => [],
        };
    }

    /// <summary>Объект — настоящий элемент, если в нём есть свойства, кроме XML-обёртки <c>@text</c> (пустой объект/обёртка = «нет данных»).</summary>
    private static bool IsRealItem(JsonElement e) => e.EnumerateObject().Any(p => p.Name != "@text");

    private static string? Str(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var e)) return null;
        return e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.GetRawText(),
            _ => null,
        };
    }

    private static decimal? Dec(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var e)) return null;
        return e.ValueKind switch
        {
            JsonValueKind.Number => e.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }

    private static int? Int(JsonElement o, string name) => Dec(o, name) is { } d ? (int)d : null;

    /// <summary>Метка времени брокера без пояса (Unspecified); нет/не разобралась/с поясом — null.</summary>
    private static DateTime? LocalTime(JsonElement o, string name) =>
        Str(o, name) is { } s && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dt) && dt.Kind == DateTimeKind.Unspecified
            ? dt
            : null;

    private static DateTime Date(JsonElement o, params string[] names)
    {
        foreach (var n in names)
        {
            if (Str(o, n) is { } s && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) return dt;
        }

        return default;
    }
}
