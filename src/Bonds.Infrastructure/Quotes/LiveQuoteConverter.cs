namespace Bonds.Infrastructure.Quotes;

/// <summary>
/// Чистая функция преобразования котировки T-Invest marketdata (<see cref="Connectors.TInvest.TInvestQuote.LastPrice"/>)
/// в рублёвую грязную цену за облигацию — вынесена из <see cref="LiveQuotesPollingService"/>, чтобы
/// её можно было покрыть юнит-тестами без мока всего тика поллинга.
/// <para>
/// <b>Единицы (источник бага, см. историю правки).</b> T-Invest marketdata (<c>GetLastPrices</c>,
/// откуда приходит <c>TInvestQuote.LastPrice</c>) отдаёт цену облигации в ПУНКТАХ (% от номинала),
/// а не в рублях — в отличие от <c>GetPortfolio.CurrentPrice</c>, который брокер уже конвертирует в
/// рубли. Рублёвая чистая цена = <c>LastPrice / 100 × FaceValue</c>. До этой правки пункты писались
/// в <c>intraday_quotes.dirty_price_rub</c> как есть, занижая стоимость облигации в ~10 раз
/// (при цене ~96.4% и номинале 1000 ₽ — 96.4 ₽ вместо ~964 ₽).
/// </para>
/// <para>
/// <b>Валютные бумаги.</b> Инструменты с <see cref="Core.Models.Instrument.IsOutOfScopeCurrency"/> имеют
/// номинал в иностранной валюте — конвертация в рубли без курса невозможна (spec §11 — вне
/// рублёвого MVP-скоупа), поэтому такие позиции пропускаются лёгким контуром целиком (честно падают
/// в fallback "последний известный тик"/статичная цена полного синка, а не считаются неверно).
/// </para>
/// </summary>
public static class LiveQuoteConverter
{
    /// <summary>
    /// Считает грязную цену облигации в рублях по пунктовой котировке T-Invest marketdata.
    /// Возвращает null, если инструмент вне рублёвого скоупа (валютный номинал) — вызывающий
    /// код должен пропустить такую позицию, а не писать в неё нулевую/некорректную цену.
    /// </summary>
    /// <param name="lastPricePoints">Котировка в пунктах (% от номинала), см. doc-comment класса.</param>
    /// <param name="faceValue">Номинал облигации, в валюте инструмента.</param>
    /// <param name="accruedRub">НКД по позиции, в рублях.</param>
    /// <param name="isOutOfScopeCurrency">Признак валютного (не рублёвого) номинала.</param>
    public static decimal? TryComputeDirtyPriceRub(
        decimal lastPricePoints,
        decimal faceValue,
        decimal accruedRub,
        bool isOutOfScopeCurrency)
    {
        if (isOutOfScopeCurrency) return null;

        var cleanPriceRub = lastPricePoints / 100m * faceValue;
        return cleanPriceRub + accruedRub;
    }

    /// <summary>
    /// Считает рублёвую чистую цену облигации по пунктовой котировке (без НКД) — используется
    /// <c>BondSyncService</c> часть 3 (fallback, когда портфель T-Invest не дал <c>CurrentPrice</c>).
    /// Возвращает null для валютных инструментов по тем же причинам, что и
    /// <see cref="TryComputeDirtyPriceRub"/>.
    /// </summary>
    public static decimal? TryComputeCleanPriceRub(
        decimal lastPricePoints,
        decimal faceValue,
        bool isOutOfScopeCurrency)
    {
        if (isOutOfScopeCurrency) return null;

        return lastPricePoints / 100m * faceValue;
    }
}
