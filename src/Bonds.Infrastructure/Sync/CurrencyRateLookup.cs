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

    /// <summary>Почему курса нет (вызывать, когда <see cref="GetRubPerUnitAsync"/> вернул null). Дефолт (0) — принципиально не конвертируется.</summary>
    Task<RateMissKind> ClassifyMissAsync(string isoCode, CancellationToken ct = default);
}

/// <summary>Причина отсутствия курса на дату.</summary>
public enum RateMissKind
{
    /// <summary>Валюты нет в справочнике <c>currencies</c> — курс не появится сам.</summary>
    NotInDirectory = 0,

    /// <summary>Валюта есть, но история курса не покрывает дату (операция старше окна истории) — не конвертируется.</summary>
    OutsideHistory = 1,

    /// <summary>Валюта есть, истории курса ещё нет (первый цикл) — самолечится суточным шагом рефрешера.</summary>
    NoHistoryYet = 2,
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

    /// <inheritdoc />
    public async Task<RateMissKind> ClassifyMissAsync(string isoCode, CancellationToken ct = default)
    {
        // Прогрев кэша (если курс на эту валюту ещё не запрашивали).
        await GetRubPerUnitAsync(isoCode, DateOnly.MinValue, ct);
        var currency = await _currencies.GetByIsoAsync(isoCode);
        if (currency is null) return RateMissKind.NotInDirectory;
        if (_cache[isoCode].Count > 0) return RateMissKind.OutsideHistory;
        // Истории нет, но рефрешер её уже запрашивал и MOEX ничего не отдал (EUR_RUB__TOM: сделок нет с 2024, CLOSE = 0) — сама
        // не появится. Иначе «самолечащийся» пропуск навсегда держал JournalSynced = false и блокировал бэкфилл истории.
        return CurrencyRateRefresher.EmptyAttemptedOn.ContainsKey(currency.Id) ? RateMissKind.OutsideHistory : RateMissKind.NoHistoryYet;
    }
}
