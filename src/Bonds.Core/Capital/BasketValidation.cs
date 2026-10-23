using System.Text.RegularExpressions;
using Bonds.Core.Models;

namespace Bonds.Core.Capital;

/// <summary>
/// Задача 62: проверки редактируемых полей корзин (граница API). Все веса — ДОЛИ. Возвращают текст ошибки (→ 422) или null.
/// </summary>
public static partial class BasketValidation
{
    /// <summary>Допуск суммы целей портфеля: |Σ target_weight − 1| ≤ 0.0001.</summary>
    public const decimal TargetsSumTolerance = 0.0001m;

    public const int MaxTitleLength = 128;

    [GeneratedRegex("^[A-Za-z0-9.@_\\-]{1,64}$")]
    private static partial Regex InstrumentKeyRegex();

    [GeneratedRegex("^[a-z_]{2,16}$")]
    private static partial Regex AssetClassRegex();

    /// <summary>Поля одной корзины: название, цель 0..1, 0 ≤ band &lt; hard_band ≤ 1, тир 0..3, правило, минимальная сделка ≥ 0.</summary>
    public static string? ValidateFields(CapitalBasket b)
    {
        if (string.IsNullOrWhiteSpace(b.Title) || b.Title.Trim().Length > MaxTitleLength)
            return $"title обязателен и не длиннее {MaxTitleLength} символов.";
        if (b.TargetWeight is < 0m or > 1m)
            return "targetWeight — доля 0..1 (0.15 = 15%) или null.";
        if (b.Band < 0m || b.Band > 1m)
            return "band — доля 0..1.";
        if (b.HardBand > 1m || b.Band >= b.HardBand)
            return "band должен быть строго меньше hardBand (обе — доли 0..1).";
        if (b.LiquidityTier is < 0 or > 3)
            return "liquidityTier — 0..3.";
        if (!BasketFillRules.All.Contains(b.FillRule))
            return "fillRule: fixed, engine или hold.";
        if (b.MinTrade < 0m)
            return "minTrade не может быть отрицательным.";
        return null;
    }

    /// <summary>
    /// Σ целей портфеля = 1 ± <see cref="TargetsSumTolerance"/>. Корзины без цели не считаются; если целей нет вовсе — проверка пропускается.
    /// </summary>
    public static string? ValidateTargetsSum(string portfolio, IEnumerable<CapitalBasket> portfolioBaskets)
    {
        var targets = portfolioBaskets.Where(b => b.TargetWeight is not null).Select(b => b.TargetWeight!.Value).ToList();
        if (targets.Count == 0) return null;
        var sum = targets.Sum();
        return Math.Abs(sum - 1m) <= TargetsSumTolerance
            ? null
            : $"Сумма целей портфеля {portfolio} = {sum:0.####} (доля), должна быть 1 ± {TargetsSumTolerance}.";
    }

    /// <summary>Член корзины: брокер портфеля, правило, ключ (ISIN/тикер либо класс актива), sub_weight 0..1.</summary>
    public static string? ValidateMember(string portfolio, CapitalBasketMember m)
    {
        if (!string.Equals(m.Broker, CapitalPortfolios.BrokerOf(portfolio), StringComparison.Ordinal))
            return $"broker для портфеля {portfolio} — {CapitalPortfolios.BrokerOf(portfolio)}.";
        if (!BasketMatchRules.All.Contains(m.MatchRule))
            return "matchRule: instrument или asset_class.";
        if (m.MatchRule == BasketMatchRules.Instrument && !InstrumentKeyRegex().IsMatch(m.InstrumentKey))
            return "instrumentKey: ISIN или тикер (буквы, цифры, . @ _ -), до 64 символов.";
        if (m.MatchRule == BasketMatchRules.AssetClass && !AssetClassRegex().IsMatch(m.InstrumentKey))
            return "instrumentKey для asset_class — класс актива (bond, fund, currency, etf, stock…).";
        if (m.SubWeight is < 0m or > 1m)
            return "subWeight — доля 0..1 или null.";
        return null;
    }

    /// <summary>Ключ члена для проверки уникальности «один член — одна корзина портфеля».</summary>
    public static string MemberIdentity(CapitalBasketMember m) =>
        m.MatchRule == BasketMatchRules.AssetClass
            ? $"{m.Broker}|class|{m.InstrumentKey.Trim().ToLowerInvariant()}"
            : $"{m.Broker}|inst|{BasketDriftService.NormalizeKey(m.InstrumentKey)}";
}
