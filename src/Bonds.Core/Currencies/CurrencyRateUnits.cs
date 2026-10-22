using Bonds.Core.Models;

namespace Bonds.Core.Currencies;

/// <summary>
/// Единицы курса валюты (задача 55). Чистые функции.
/// <para>
/// <b>Номинал MOEX.</b> История торгов CETS (<c>/iss/history/engines/currency/markets/selt/boards/CETS/securities/{secid}.json</c>)
/// отдаёт <c>CLOSE</c> в РУБЛЯХ ЗА <c>FACEVALUE</c> ЕДИНИЦ валюты (колонки FACEVALUE в самой истории НЕТ — он лежит в
/// <c>securities</c>): CNYRUB_TOM — FACEVALUE 1 (CLOSE ≈ 12.2 ₽ за юань), JPYRUB_TOM / KZTRUB_TOM — FACEVALUE 100 (CLOSE — рубли за 100
/// единиц). Во всём продукте курс хранится и считается в РУБЛЯХ ЗА 1 ЕДИНИЦУ: <c>RubPerUnit = CLOSE ÷ FACEVALUE</c>. Конверсия — ровно
/// один раз, при записи истории (<c>CurrencyRateRefresher</c>).
/// </para>
/// </summary>
public static class CurrencyRateUnits
{
    /// <summary>Рубли за 1 единицу валюты из <paramref name="closePerFaceValue"/> (рубли за <paramref name="faceValue"/> единиц).</summary>
    /// <exception cref="ArgumentOutOfRangeException">Номинал не положителен.</exception>
    public static decimal RubPerUnit(decimal closePerFaceValue, decimal faceValue)
    {
        if (faceValue <= 0m) throw new ArgumentOutOfRangeException(nameof(faceValue), faceValue, "FACEVALUE must be positive");
        return closePerFaceValue / faceValue;
    }

    /// <summary>
    /// Курс на дату: последняя точка истории НЕ ПОЗЖЕ <paramref name="date"/> (forward fill выходных/праздников), но не старше
    /// <paramref name="maxStaleDays"/> дней. Null — подходящей точки нет. <paramref name="history"/> может быть в любом порядке.
    /// </summary>
    public static decimal? RateOnOrBefore(IReadOnlyList<CurrencyRatePoint> history, DateOnly date, int maxStaleDays = PaymentCurrencyConverter.MaxRateStaleDays)
    {
        CurrencyRatePoint? best = null;
        foreach (var p in history)
        {
            if (p.TradeDate > date || p.CloseRub <= 0m) continue;
            if (best is null || p.TradeDate > best.TradeDate) best = p;
        }

        return best is not null && date.DayNumber - best.TradeDate.DayNumber <= maxStaleDays ? best.CloseRub : null;
    }
}

/// <summary>Изменение курса за окна 1/3/12 месяцев — ДОЛИ (0.05 = курс вырос на 5%); null — не хватает истории.</summary>
public sealed record CurrencyRateChange(decimal? M1, decimal? M3, decimal? M12);

public static class CurrencyRateChangeCalculator
{
    /// <summary>
    /// Изменение курса <c>current / rateAt(asOf − N мес.) − 1</c> (ДОЛЯ). Курс на начало окна — forward fill
    /// (<see cref="CurrencyRateUnits.RateOnOrBefore"/>); нет точки на начало окна (история короче окна) — окно null.
    /// <paramref name="current"/> — тот же курс, от которого посчитана стоимость позиции (единая точка цены).
    /// </summary>
    public static CurrencyRateChange Compute(IReadOnlyList<CurrencyRatePoint> history, decimal? current, DateOnly asOf)
    {
        decimal? Window(int months)
        {
            if (current is not { } cur || cur <= 0m) return null;
            var start = CurrencyRateUnits.RateOnOrBefore(history, asOf.AddMonths(-months));
            return start is { } s and > 0m ? cur / s - 1m : null;
        }

        return new CurrencyRateChange(Window(1), Window(3), Window(12));
    }
}
