namespace Bonds.Core.Currencies;

/// <summary>Результат пересчёта платежа операции: <see cref="AmountRub"/> = null — пересчитать нельзя (нет курса), операцию пропустить.</summary>
public readonly record struct PaymentConversion(decimal? AmountRub, bool WasConverted)
{
    public bool Skipped => AmountRub is null;
}

/// <summary>
/// Валюта платежа операции (задача 55). T-Invest <c>OperationItem.Payment</c> — <c>MoneyValue</c> с собственной валютой
/// (<c>MoneyValue.Currency</c>, ISO, нижний регистр). Раньше число писалось в рублёвые поля без проверки: комиссия в юанях
/// или купон валютной бумаги стали бы «рублями». Теперь не-рублёвый платёж пересчитывается по курсу дня или пропускается.
/// </summary>
public static class PaymentCurrencyConverter
{
    /// <summary>Курс считается применимым, если точка истории не старше этого числа дней (выходные/праздники).</summary>
    public const int MaxRateStaleDays = 10;

    /// <summary>Рубль (регистр не важен). Пустая/неизвестная валюта трактуется как рубль — прежнее поведение до появления поля.</summary>
    public static bool IsRub(string? iso) =>
        string.IsNullOrWhiteSpace(iso) || string.Equals(iso.Trim(), "rub", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Платёж в рубли: рубль — как есть; иная валюта — <c>amount × rubPerUnit</c> (РУБЛИ за 1 единицу валюты платежа, курс дня
    /// операции); курса нет (null или ≤ 0) — <see cref="PaymentConversion.Skipped"/>. Знак брокера сохраняется.
    /// </summary>
    public static PaymentConversion ToRub(decimal amount, string? currencyIso, decimal? rubPerUnit)
    {
        if (IsRub(currencyIso)) return new PaymentConversion(amount, false);
        return rubPerUnit is > 0m ? new PaymentConversion(amount * rubPerUnit.Value, true) : new PaymentConversion(null, false);
    }
}

/// <summary>Количество единиц валюты в операции.</summary>
public static class CurrencyOperationUnits
{
    /// <summary>Допустимое расхождение <c>quantity × цена</c> и <c>|платёж|</c> (доля от |платежа|), выше которого quantity считается лотами.</summary>
    public const decimal CrossCheckTolerance = 0.05m;

    /// <summary>
    /// Единицы валюты операции. T-Invest <c>OperationItem.Quantity</c> — «количество единиц инструмента», но для валюты не подтверждено,
    /// единицы это или ЛОТЫ (лот юаня = 1000 единиц). Кросс-проверка: если известна цена за единицу (<paramref name="unitPriceRub"/>,
    /// РУБЛИ) и <c>|quantity × цена − |платёж||</c> &gt; 5% от |платежа|, quantity считается лотами и берутся единицы
    /// <c>|платёж| ÷ цена</c> (независимо от лотности), <paramref name="mismatch"/> = true (вызывающий пишет LogWarning). Без цены —
    /// quantity как есть. Если quantity не отдано (0/null), единицы восстанавливаются как <c>|платёж| ÷ цена</c>. Иначе null.
    /// </summary>
    public static decimal? Resolve(decimal? quantity, decimal paymentRub, decimal? unitPriceRub, out bool mismatch)
    {
        mismatch = false;
        var fromPayment = unitPriceRub is > 0m && paymentRub != 0m ? Math.Abs(paymentRub) / unitPriceRub.Value : (decimal?)null;
        if (quantity is > 0m)
        {
            if (unitPriceRub is > 0m && paymentRub != 0m && fromPayment is > 0m
                && Math.Abs(quantity.Value * unitPriceRub.Value - Math.Abs(paymentRub)) > CrossCheckTolerance * Math.Abs(paymentRub))
            {
                mismatch = true;
                return fromPayment;
            }

            return quantity;
        }

        return fromPayment;
    }

    /// <inheritdoc cref="Resolve(decimal?, decimal, decimal?, out bool)"/>
    public static decimal? Resolve(decimal? quantity, decimal paymentRub, decimal? unitPriceRub) =>
        Resolve(quantity, paymentRub, unitPriceRub, out _);
}

/// <summary>Разбивка «денег на счёте» (задача 55).</summary>
public static class CashBreakdown
{
    /// <summary>
    /// Рубли без валюты: <c>cashRub − currencyValueRub</c>, не ниже 0. Брокерский <c>TotalAmountCurrencies</c> (хранится как
    /// <c>accounts.cash_rub</c>) УЖЕ ВКЛЮЧАЕТ валюту, пересчитанную в рубли, поэтому валюту нельзя прибавлять к <c>cashRub</c> повторно
    /// (двойной счёт) — она лишь выделяется из него. Сумма <c>cashRubOnly + currencyValueRub</c> равна <c>cashRub</c>, пока валюта ≤ cashRub.
    /// </summary>
    public static decimal CashRubOnly(decimal cashRub, decimal currencyValueRub) => Math.Max(0m, cashRub - currencyValueRub);
}
