using Bonds.Core.Currencies;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;

namespace Bonds.Infrastructure.Sync;

/// <summary>Курс валюты на дату (РУБЛИ за 1 единицу) — для пересчёта платежей операций не в рублях (задача 55).</summary>
public interface ICurrencyRateLookup
{
    /// <summary>
    /// Курс <paramref name="isoCode"/> на <paramref name="date"/>: последнее закрытие истории не позже даты (forward fill, не старше
    /// <see cref="PaymentCurrencyConverter.MaxRateStaleDays"/> дней). Null — валюты нет в справочнике или подходящей точки нет.
    /// </summary>
    Task<decimal?> GetRubPerUnitAsync(string isoCode, DateOnly date, CancellationToken ct = default);
}

/// <summary>Реализация на <c>currencies</c> + <c>currency_rate_history</c>; история каждой валюты грузится один раз на время жизни (scoped = один синк).</summary>
public sealed class CurrencyRateLookup : ICurrencyRateLookup
{
    private readonly ICurrencyRepository _currencies;
    private readonly ICurrencyMarketDataRepository _market;
    private readonly Dictionary<string, IReadOnlyList<CurrencyRatePoint>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public CurrencyRateLookup(ICurrencyRepository currencies, ICurrencyMarketDataRepository market)
    {
        _currencies = currencies;
        _market = market;
    }

    public async Task<decimal?> GetRubPerUnitAsync(string isoCode, DateOnly date, CancellationToken ct = default)
    {
        if (!_cache.TryGetValue(isoCode, out var history))
        {
            var currency = await _currencies.GetByIsoAsync(isoCode);
            history = currency is null
                ? []
                : await _market.GetRatesAsync(currency.Id, DateOnly.MinValue, DateOnly.MaxValue, ct);
            _cache[isoCode] = history;
        }

        return CurrencyRateUnits.RateOnOrBefore(history, date);
    }
}
