using Bonds.Core.Models;

namespace Bonds.Core.Interfaces.Repositories;

/// <summary>Справочник валют (задача 55).</summary>
public interface ICurrencyRepository
{
    Task<Currency?> GetByIdAsync(ulong id);
    Task<Currency?> GetByFigiAsync(string figi);

    /// <summary>По ISO-коду (регистр не важен).</summary>
    Task<Currency?> GetByIsoAsync(string isoCode);

    Task<IReadOnlyList<Currency>> GetAllAsync();

    /// <summary>Upsert по FIGI (уникален). Возвращает id (и при update).</summary>
    Task<ulong> UpsertAsync(Currency currency);
}

public interface ICurrencyPositionRepository
{
    Task<IReadOnlyList<CurrencyPosition>> GetByAccountAsync(ulong accountId);

    /// <summary>Upsert по (account_id, currency_id).</summary>
    Task<ulong> UpsertAsync(CurrencyPosition position);

    /// <summary>Сверка: удаляет позиции счёта, чьих валют НЕТ в <paramref name="keepCurrencyIds"/>. Возвращает число удалённых.</summary>
    Task<int> DeleteAllExceptAsync(ulong accountId, IReadOnlyCollection<ulong> keepCurrencyIds);
}

public interface ICurrencyOperationRepository
{
    /// <summary>Идемпотентный upsert по <see cref="CurrencyOperation.ExternalId"/>. Возвращает число обработанных.</summary>
    Task<int> UpsertManyByExternalIdAsync(IEnumerable<CurrencyOperation> operations);

    Task<IReadOnlyList<CurrencyOperation>> GetByAccountAsync(ulong accountId);
}

/// <summary>История курса валют (РУБЛИ за 1 единицу).</summary>
public interface ICurrencyMarketDataRepository
{
    /// <summary>Батчевый upsert дневных закрытий (рубли за 1 единицу) по (currency_id, trade_date).</summary>
    Task UpsertRatesAsync(ulong currencyId, IReadOnlyList<CurrencyRatePoint> points, CancellationToken ct = default);

    Task<IReadOnlyList<CurrencyRatePoint>> GetRatesAsync(ulong currencyId, DateOnly from, DateOnly to, CancellationToken ct = default);

    /// <summary>Последний известный курс; null — истории нет.</summary>
    Task<CurrencyRatePoint?> GetLatestRateAsync(ulong currencyId, CancellationToken ct = default);

    Task<DateOnly?> GetLastRateDateAsync(ulong currencyId, CancellationToken ct = default);
}
