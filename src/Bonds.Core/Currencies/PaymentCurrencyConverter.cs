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
    /// <summary>
    /// Единицы валюты операции. T-Invest <c>OperationItem.Quantity</c> — «количество единиц инструмента» (для валюты — единицы,
    /// а не лоты; подтвердить на реальных данных, см. README коннектора) и берётся как есть. Если оно не отдано (0/null), а
    /// известна цена за единицу, количество восстанавливается как <c>|платёж| ÷ цена</c>. Иначе null (журнал пометит неизвестный остаток).
    /// </summary>
    public static decimal? Resolve(decimal? quantity, decimal paymentRub, decimal? unitPriceRub)
    {
        if (quantity is > 0m) return quantity;
        return unitPriceRub is > 0m && paymentRub != 0m ? Math.Abs(paymentRub) / unitPriceRub.Value : null;
    }
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
