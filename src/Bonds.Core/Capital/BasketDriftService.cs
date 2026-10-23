namespace Bonds.Core.Capital;

/// <summary>Состояние корзины относительно цели и полос.</summary>
public enum BasketState
{
    /// <summary>|drift| ≤ band.</summary>
    Ok,

    /// <summary>drift &lt; −band, но не дальше −hardBand.</summary>
    Below,

    /// <summary>drift &gt; band, но не дальше hardBand.</summary>
    Above,

    /// <summary>drift &lt; −hardBand.</summary>
    HardBelow,

    /// <summary>drift &gt; hardBand.</summary>
    HardAbove,

    /// <summary>У корзины нет цели (<c>target_weight = null</c>): отклонение не считается.</summary>
    NoTarget,

    /// <summary>У корзины есть цель, но база доли портфеля ≤ 0 (нет данных/пустой портфель): отклонение и дефицит не считаются.</summary>
    NoData,
}

/// <summary>
/// Позиция пользователя, оценённая в ВАЛЮТЕ ПОРТФЕЛЯ (rub — рубли, usd — доллары по курсу bonds).
/// <paramref name="Key"/> — ISIN или тикер (для T-Invest фонды — тикер, облигации — ISIN), <paramref name="AssetClass"/> —
/// <see cref="CapitalAssetClasses"/> либо <see cref="BrokerAssetClass"/> (Freedom).
/// </summary>
public sealed record CapitalPosition(string Broker, string Key, string AssetClass, string Name, decimal Value);

/// <summary>
/// Строка отклонения. <b>Единицы:</b> <see cref="Value"/>/<see cref="DeficitToTarget"/> — валюта портфеля; <see cref="Weight"/>,
/// <see cref="Target"/>, <see cref="Drift"/>, <see cref="ShareOfTotal"/> — ДОЛИ.
/// </summary>
/// <param name="BasketId">null — системная (виртуальная) строка: cash/unassigned/fx.</param>
/// <param name="Value">Стоимость позиций корзины.</param>
/// <param name="Weight">Value ÷ база доли (база = портфель минус корзины без цели; свободные деньги в базе). У корзин без цели — то же деление (справочно, может быть &gt; 1).</param>
/// <param name="ShareOfTotal">Value ÷ ВЕСЬ портфель (с деньгами).</param>
/// <param name="Target">Цель; null у корзин без цели.</param>
/// <param name="Drift">Weight − Target (отрицательное — недобор); null без цели.</param>
/// <param name="DeficitToTarget">Target × база − Value: сколько не хватает до цели (положительное — недобор, отрицательное — избыток); null без цели.</param>
public sealed record BasketDriftRow(
    ulong? BasketId,
    string Code,
    string Title,
    bool IsSystem,
    decimal Value,
    decimal Weight,
    decimal ShareOfTotal,
    decimal? Target,
    decimal? Drift,
    BasketState State,
    decimal? DeficitToTarget,
    IReadOnlyList<CapitalPosition> Positions);

/// <param name="TotalValue">Σ позиций + свободные деньги, валюта портфеля.</param>
/// <param name="BaseValue">База доли: <paramref name="TotalValue"/> минус стоимость корзин без цели (в т. ч. системных unassigned/fx).</param>
public sealed record BasketDriftResult(IReadOnlyList<BasketDriftRow> Rows, decimal TotalValue, decimal BaseValue);

/// <summary>
/// Задача 62: чистый расчёт отклонений корзин портфеля от целей. Вход — позиции ОДНОГО портфеля в его валюте, свободные деньги и корзины
/// этого портфеля. Инвариант: каждая позиция попадает ровно в одну корзину (<see cref="Assign"/>): явный член → правило класса →
/// системная корзина (<see cref="SystemBaskets.Fx"/> для валюты, иначе <see cref="SystemBaskets.Unassigned"/>).
/// </summary>
public static class BasketDriftService
{
    public static BasketDriftResult Calculate(IReadOnlyList<CapitalBasket> baskets, IReadOnlyList<CapitalPosition> positions, decimal freeCash)
    {
        var ordered = baskets.OrderBy(b => b.SortOrder).ThenBy(b => b.Id).ToList();
        var assignment = Assign(ordered, positions);

        var contents = ordered.ToDictionary(b => b.Code, _ => new List<CapitalPosition>(), StringComparer.Ordinal);
        var unassigned = new List<CapitalPosition>();
        var fx = new List<CapitalPosition>();
        for (var i = 0; i < positions.Count; i++)
        {
            switch (assignment[i])
            {
                case SystemBaskets.Unassigned: unassigned.Add(positions[i]); break;
                case SystemBaskets.Fx: fx.Add(positions[i]); break;
                default: contents[assignment[i]].Add(positions[i]); break;
            }
        }

        var cash = Math.Max(0m, freeCash);
        var total = positions.Sum(p => p.Value) + cash;
        var excluded = ordered.Where(b => b.TargetWeight is null).Sum(b => contents[b.Code].Sum(p => p.Value))
            + unassigned.Sum(p => p.Value) + fx.Sum(p => p.Value);
        var baseValue = total - excluded;

        var rows = new List<BasketDriftRow>();
        foreach (var basket in ordered)
        {
            var members = contents[basket.Code];
            rows.Add(BuildRow(basket.Id, basket.Code, basket.Title, false, basket.TargetWeight, basket.Band, basket.HardBand, members, total, baseValue));
        }

        if (unassigned.Count > 0)
            rows.Add(BuildRow(null, SystemBaskets.Unassigned, "Вне корзин", true, null, 0m, 0m, unassigned, total, baseValue));
        if (fx.Count > 0)
            rows.Add(BuildRow(null, SystemBaskets.Fx, "Валюта", true, null, 0m, 0m, fx, total, baseValue));

        // Свободные деньги — в базе доли, но цели у них нет.
        var cashRow = new BasketDriftRow(
            null, SystemBaskets.Cash, "Свободные деньги", true, cash, Ratio(cash, baseValue), Ratio(cash, total),
            null, null, BasketState.NoTarget, null, []);
        rows.Add(cashRow);

        return new BasketDriftResult(rows, total, baseValue);
    }

    /// <summary>
    /// Код корзины для каждой позиции (по индексу). Явный член (брокер + ключ без регистра и хвостового «@») → правило класса актива того же
    /// брокера → <see cref="SystemBaskets.Fx"/> (класс «currency») или <see cref="SystemBaskets.Unassigned"/>. При конфликте (бумага — член
    /// нескольких корзин; валидация API этого не допускает) побеждает корзина с меньшим <c>sort_order</c>.
    /// </summary>
    public static IReadOnlyList<string> Assign(IReadOnlyList<CapitalBasket> baskets, IReadOnlyList<CapitalPosition> positions)
    {
        var ordered = baskets.OrderBy(b => b.SortOrder).ThenBy(b => b.Id).ToList();
        var result = new string[positions.Count];
        for (var i = 0; i < positions.Count; i++)
        {
            var p = positions[i];
            var key = NormalizeKey(p.Key);

            var explicitBasket = ordered.FirstOrDefault(b => b.Members.Any(m =>
                m.MatchRule == BasketMatchRules.Instrument && SameBroker(m.Broker, p.Broker) && KeyMatches(m.InstrumentKey, key)));
            var byClass = explicitBasket ?? ordered.FirstOrDefault(b => b.Members.Any(m =>
                m.MatchRule == BasketMatchRules.AssetClass && SameBroker(m.Broker, p.Broker)
                && string.Equals(m.InstrumentKey.Trim(), p.AssetClass, StringComparison.OrdinalIgnoreCase)));

            result[i] = byClass?.Code
                ?? (string.Equals(p.AssetClass, CapitalAssetClasses.Currency, StringComparison.OrdinalIgnoreCase) ? SystemBaskets.Fx : SystemBaskets.Unassigned);
        }

        return result;
    }

    /// <summary>
    /// Ключ бумаги для сравнения: без пробелов, ВЕРХНИЙ регистр, без хвостового «@» (T-Invest отдаёт тикеры Т-Капитала как «TMON@», а в
    /// других источниках бывает «TMON»).
    /// </summary>
    public static string NormalizeKey(string key) => key.Trim().ToUpperInvariant().TrimEnd('@');

    /// <summary>
    /// Ключ члена совпадает с ключом позиции (<paramref name="positionKey"/> уже НОРМАЛИЗОВАН <see cref="NormalizeKey"/>): точно, либо — если у члена нет суффикса биржи
    /// (нет «.») — по базовому тикеру Freedom: член «LUV» подхватывает «LUV.US», а «VUAA.EU» — только «VUAA.EU». Облигации (ISIN) и тикеры
    /// фондов T-Invest точек не содержат и сравниваются точно.
    /// </summary>
    public static bool KeyMatches(string memberKey, string positionKey)
    {
        var m = NormalizeKey(memberKey);
        return m == positionKey || (!m.Contains('.') && positionKey.StartsWith(m + ".", StringComparison.Ordinal));
    }

    private static bool SameBroker(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static BasketDriftRow BuildRow(
        ulong? id, string code, string title, bool isSystem, decimal? target, decimal band, decimal hardBand,
        IReadOnlyList<CapitalPosition> members, decimal total, decimal baseValue)
    {
        var value = members.Sum(p => p.Value);
        var weight = Ratio(value, baseValue);
        var noData = target is not null && baseValue <= 0m; // доля от нулевой/отрицательной базы бессмысленна — не рисуем «недобор»
        decimal? drift = target is { } t && !noData ? weight - t : null;
        decimal? deficit = target is { } t2 && !noData ? t2 * baseValue - value : null;
        var state = noData ? BasketState.NoData : StateOf(drift, band, hardBand);
        return new BasketDriftRow(id, code, title, isSystem, value, weight, Ratio(value, total), target, drift, state, deficit, members);
    }

    /// <summary>Состояние по полосам: граница включена в «ближнюю» зону (|drift| = band — ещё Ok, |drift| = hardBand — ещё не Hard).</summary>
    public static BasketState StateOf(decimal? drift, decimal band, decimal hardBand)
    {
        if (drift is not { } d) return BasketState.NoTarget;
        if (d > hardBand) return BasketState.HardAbove;
        if (d < -hardBand) return BasketState.HardBelow;
        if (d > band) return BasketState.Above;
        if (d < -band) return BasketState.Below;
        return BasketState.Ok;
    }

    private static decimal Ratio(decimal value, decimal denominator) => denominator > 0m ? value / denominator : 0m;
}
