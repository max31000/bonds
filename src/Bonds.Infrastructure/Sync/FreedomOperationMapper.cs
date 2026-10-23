using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Bonds.Core.Brokers;
using Bonds.Core.Models;
using Bonds.Infrastructure.Connectors.Freedom.Models;

namespace Bonds.Infrastructure.Sync;

/// <summary>
/// Задача 60: маппинг сделок и движений денег Freedom в журнал <c>broker_operations</c>. Чистые функции.
/// <para>
/// <b>Единицы.</b> <c>amount</c> — в валюте операции СО ЗНАКОМ (плюс — деньги пришли на счёт, минус — ушли); числа брокера
/// (<see cref="FreedomTrade.Amount"/>, <see cref="FreedomCashFlow.Amount"/>) уже в валюте записи, пересчёта нет. <c>RUR</c> → <c>RUB</c>.
/// </para>
/// <para>
/// <b>Идемпотентность.</b> Сделка — <c>trade:{id}</c>. Движение денег — <c>cf:{sha256(datetime|type|sum|comment)[..32]}</c>; одинаковые по
/// всем четырём полям строки одного ответа различаются суффиксом <c>#2</c>, <c>#3</c>… (порядок среди ИДЕНТИЧНЫХ строк не важен).
/// </para>
/// <para>
/// <b>Комиссия сделки.</b> Поле <c>commission</c> сделки в журнал НЕ пишется: комиссия приходит отдельной строкой движения денег
/// <c>commission_for_trades</c> (иначе двойной счёт). Движения с нулевой суммой пропускаются (информации не несут).
/// </para>
/// </summary>
public static partial class FreedomOperationMapper
{
    public const int MaxCommentLength = 512;
    public const int MaxRawTypeLength = 48;
    public const int MaxTickerLength = 32;

    /// <summary>Тикер комиссии из комментария: <c>(Trade 900000101 buy SPYM.US )</c> → <c>SPYM.US</c>.</summary>
    [GeneratedRegex(@"\(Trade \d+ (?:buy|sell) ([A-Z0-9.\-]+) \)")]
    private static partial Regex FeeTickerRegex();

    /// <summary>Тикер дивиденда/налога из комментария: <c>(Procure Space ETF (UFO.US))</c> → <c>UFO.US</c> (биржевой суффикс обязателен).</summary>
    [GeneratedRegex(@"\(([A-Z0-9][A-Z0-9\-]*\.[A-Z]{2,3})\)")]
    private static partial Regex SecurityTickerRegex();

    public static string? ExtractFeeTicker(string? comment)
    {
        var m = comment is null ? null : FeeTickerRegex().Match(comment);
        return m is { Success: true } ? m.Groups[1].Value : null;
    }

    public static string? ExtractSecurityTicker(string? comment)
    {
        var m = comment is null ? null : SecurityTickerRegex().Match(comment);
        return m is { Success: true } ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// Сделка → Buy (amount −|summ|) / Sell (+|summ|), либо конвертация валют (<c>instr_nm</c> вида <c>EUR/USD</c>) → Other с
    /// <c>raw_type = "fx"</c> и парой в комментарии (знак по стороне сделки, валюта — <c>curr_c</c>; точная семантика ног конверсии у брокера
    /// не документирована, поэтому в выручку/комиссии/пополнения такая запись не входит).
    /// </summary>
    public static BrokerOperation MapTrade(ulong accountId, FreedomTrade t)
    {
        var amountAbs = Math.Abs(t.Amount) > 0m ? Math.Abs(t.Amount) : Math.Abs(t.Price * t.Quantity);
        var amount = t.Side == FreedomTradeSide.Buy ? -amountAbs : amountAbs;
        var isFx = t.Ticker.Contains('/');

        return new BrokerOperation
        {
            AccountId = accountId,
            ExternalId = "trade:" + t.Id,
            Date = t.Date,
            Type = isFx ? BrokerOperationType.Other : t.Side == FreedomTradeSide.Buy ? BrokerOperationType.Buy : BrokerOperationType.Sell,
            Ticker = isFx ? null : Truncate(t.Ticker, MaxTickerLength),
            Quantity = t.Quantity,
            Price = t.Price,
            Amount = amount,
            Currency = BrokerAssetClassifier.NormalizeCurrency(t.Currency) ?? string.Empty,
            RawType = isFx ? "fx" : "trade",
            Comment = isFx ? Truncate($"{t.Ticker} {(t.Side == FreedomTradeSide.Buy ? "buy" : "sell")}", MaxCommentLength) : null,
        };
    }

    /// <summary>
    /// Движение денег → журнал: <c>bank</c>/<c>card</c> — Deposit (сумма &gt; 0) либо Withdrawal (&lt; 0); <c>commission_for_trades</c> — Fee
    /// (тикер из комментария); <c>dividend</c> — Dividend; <c>tax</c> — Tax; прочее — Other (исходный код в <c>raw_type</c>). Нулевые суммы пропускаются.
    /// </summary>
    public static IReadOnlyList<BrokerOperation> MapCashFlows(ulong accountId, IReadOnlyList<FreedomCashFlow> flows)
    {
        var result = new List<BrokerOperation>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var f in flows)
        {
            if (f.Amount == 0m) continue;

            var hash = CashFlowHash(f);
            seen[hash] = seen.TryGetValue(hash, out var n) ? n + 1 : 1;
            var externalId = "cf:" + hash + (seen[hash] > 1 ? "#" + seen[hash] : string.Empty);

            var code = (f.TypeCode ?? string.Empty).Trim();
            var (type, ticker) = code switch
            {
                "bank" or "card" => (f.Amount > 0m ? BrokerOperationType.Deposit : BrokerOperationType.Withdrawal, (string?)null),
                "commission_for_trades" => (BrokerOperationType.Fee, ExtractFeeTicker(f.Comment)),
                "dividend" => (BrokerOperationType.Dividend, ExtractSecurityTicker(f.Comment)),
                "tax" => (BrokerOperationType.Tax, ExtractSecurityTicker(f.Comment)),
                _ => (BrokerOperationType.Other, (string?)null),
            };

            result.Add(new BrokerOperation
            {
                AccountId = accountId,
                ExternalId = externalId,
                Date = f.Date,
                Type = type,
                Ticker = ticker is null ? null : Truncate(ticker, MaxTickerLength),
                Amount = f.Amount,
                Currency = BrokerAssetClassifier.NormalizeCurrency(f.Currency) ?? string.Empty,
                RawType = Truncate(code, MaxRawTypeLength),
                Comment = string.IsNullOrWhiteSpace(f.Comment) ? null : Truncate(f.Comment, MaxCommentLength),
            });
        }

        return result;
    }

    /// <summary>Хэш ключа идемпотентности движения денег: первые 32 hex-символа SHA-256 от <c>datetime|type|sum|comment</c>.</summary>
    public static string CashFlowHash(FreedomCashFlow f)
    {
        var key = string.Join('|',
            f.Date.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            f.TypeCode,
            f.Amount.ToString("0.########", CultureInfo.InvariantCulture),
            f.Comment);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32].ToLowerInvariant();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
