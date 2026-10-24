using Bonds.Core.Capital;
using BrokerIds = Bonds.Core.Models.Brokers;

namespace Bonds.Core.Policy;

/// <summary>
/// Белый список авто-исполнения — В КОДЕ, не в БД (задача 67, аудит #1; дизайн §5: авто — только фонды корзины «Ликвидность»). Флаги <c>auto_buy</c> корзин
/// лишь СУЖАЮТ его: авто разрешено только для покупки бумаги, которая (1) — член (правило «instrument», T-Invest) корзины с кодом <see cref="BasketCode"/>,
/// (2) — класса «фонд» (не облигация, не акция) и (3) без выплат за 12 месяцев (<c>pays_distributions = false</c>) либо её тикер в
/// <see cref="PolicyOptions.AutoFundWhitelist"/>. Проверяется дважды: правилами (<see cref="PolicyKit.DecideStatus"/>, статус <c>AutoApproved</c>) и
/// исполнителем перед стартом (<c>AutoExecutionWhitelist</c>) — подделанный в БД статус не исполнится. Freedom в авто не бывает никогда.
/// </summary>
public static class AutoWhitelist
{
    /// <summary>Единственная корзина, покупки которой могут исполняться без подтверждения.</summary>
    public const string BasketCode = "liquidity";

    /// <summary>Тикер в конфиг-списке <see cref="PolicyOptions.AutoFundWhitelist"/> (без «@», регистр не важен).</summary>
    public static bool InConfigList(string ticker, PolicyOptions options)
    {
        var key = BasketDriftService.NormalizeKey(ticker);
        return options.AutoFundWhitelist.Any(t => BasketDriftService.NormalizeKey(t) == key);
    }

    /// <summary>Причина, по которой фонд не проходит условие «без выплат» (null — проходит): <paramref name="paysDistributions"/> = false либо тикер в конфиг-списке.</summary>
    public static string? DistributionsRejection(string ticker, bool? paysDistributions, PolicyOptions options)
    {
        if (paysDistributions == false || InConfigList(ticker, options)) return null;
        return paysDistributions == true
            ? $"фонд {ticker} платит выплаты — авто-исполнение только для фондов без выплат"
            : $"у фонда {ticker} неизвестно, есть ли выплаты — авто-исполнение только для фондов без выплат";
    }

    /// <summary>Бумага — член (instrument, T-Invest) корзины «liquidity» с включённой авто-покупкой.</summary>
    public static bool IsLiquidityMember(IEnumerable<CapitalBasket> baskets, string instrumentKey, string ticker)
    {
        var keys = new[] { BasketDriftService.NormalizeKey(instrumentKey), BasketDriftService.NormalizeKey(ticker) };
        return baskets
            .Where(b => b.AutoBuy && string.Equals(b.Code, BasketCode, StringComparison.Ordinal))
            .SelectMany(b => b.Members)
            .Where(m => m.MatchRule == BasketMatchRules.Instrument && string.Equals(m.Broker, BrokerIds.TInvest, StringComparison.OrdinalIgnoreCase))
            .Any(m => keys.Any(k => BasketDriftService.KeyMatches(m.InstrumentKey, k)));
    }

    /// <summary>Причина, по которой строка черновика не может быть авто-подтверждена (null — может). Котировка строки ищется во входе правил.</summary>
    public static string? LineRejection(PolicyInput input, TradePlanLine line)
    {
        if (line.Side != PlanSides.Buy) return $"авто — только покупки ({line.Ticker})";
        if (!string.Equals(line.Broker, BrokerIds.TInvest, StringComparison.OrdinalIgnoreCase)) return $"авто — только T-Invest ({line.Ticker})";
        if (!IsLiquidityMember(input.Baskets, line.InstrumentKey, line.Ticker)) return $"бумага {line.Ticker} не входит в корзину «{BasketCode}» с авто-покупкой";

        var quote = input.Quotes.FirstOrDefault(q => q.Broker == line.Broker && q.InstrumentKey == line.InstrumentKey);
        if (quote is null || quote.Kind != PolicyAssetKind.Fund) return $"бумага {line.Ticker} не фонд — авто только для фондов";
        return DistributionsRejection(line.Ticker, quote.PaysDistributions, input.Options);
    }
}
