using Bonds.Core.Models;

namespace Bonds.Core.Interfaces.Repositories;

/// <summary>Справочник фондов (задача 51).</summary>
public interface IFundRepository
{
    Task<Fund?> GetByIdAsync(ulong id);
    Task<Fund?> GetByFigiAsync(string figi);
    Task<Fund?> GetByIsinAsync(string isin);
    Task<IReadOnlyList<Fund>> GetAllAsync();

    /// <summary>
    /// Upsert по FIGI (уникален). Пользовательские настройки прогноза (<see cref="Fund.ForecastMethod"/>,
    /// <see cref="Fund.BenchmarkIndex"/>) при обновлении НЕ перезаписываются — их меняет только <see cref="UpdateSettingsAsync"/>.
    /// Возвращает id (и при update).
    /// </summary>
    Task<ulong> UpsertAsync(Fund fund);

    Task UpdateSettingsAsync(ulong fundId, FundForecastMethod forecastMethod, string? benchmarkIndex);
}

public interface IFundPositionRepository
{
    Task<IReadOnlyList<FundPosition>> GetByAccountAsync(ulong accountId);

    /// <summary>Upsert по (account_id, fund_id).</summary>
    Task<ulong> UpsertAsync(FundPosition position);

    /// <summary>Сверка: удаляет позиции счёта, чьих фондов НЕТ в <paramref name="keepFundIds"/>. Чужие счета не затрагиваются. Возвращает число удалённых.</summary>
    Task<int> DeleteAllExceptAsync(ulong accountId, IReadOnlyCollection<ulong> keepFundIds);
}

public interface IFundOperationRepository
{
    /// <summary>Идемпотентный upsert по <see cref="FundOperation.ExternalId"/>. Возвращает число обработанных.</summary>
    Task<int> UpsertManyByExternalIdAsync(IEnumerable<FundOperation> operations);

    Task<IReadOnlyList<FundOperation>> GetByAccountAsync(ulong accountId);
    Task<IReadOnlyList<FundOperation>> GetByFundAsync(ulong accountId, ulong fundId);
}

/// <summary>Рыночные данные фондов: история цен паёв, выплаты на пай, история облигационных индексов.</summary>
public interface IFundMarketDataRepository
{
    /// <summary>Батчевый upsert дневных закрытий (рубли за пай) по (fund_id, trade_date).</summary>
    Task UpsertPricesAsync(ulong fundId, IReadOnlyList<FundPricePoint> points, CancellationToken ct = default);

    Task<IReadOnlyList<FundPricePoint>> GetPricesAsync(ulong fundId, DateOnly from, DateOnly to, CancellationToken ct = default);
    /// <summary>Последнее закрытие пая (рубли) из истории; null — истории нет.</summary>
    Task<FundPricePoint?> GetLatestPriceAsync(ulong fundId, CancellationToken ct = default);

    Task<DateOnly?> GetLastPriceDateAsync(ulong fundId, CancellationToken ct = default);

    /// <summary>Батчевый upsert выплат на пай по (fund_id, record_date).</summary>
    Task UpsertDistributionsAsync(ulong fundId, IReadOnlyList<FundDistribution> items, CancellationToken ct = default);

    Task<IReadOnlyList<FundDistribution>> GetDistributionsAsync(ulong fundId, CancellationToken ct = default);

    /// <summary>Батчевый upsert истории индекса по (index_code, trade_date).</summary>
    Task UpsertIndexHistoryAsync(string indexCode, IReadOnlyList<MarketIndexPoint> points, CancellationToken ct = default);

    /// <summary>Последняя точка индекса, у которой известна доходность (<c>yield_fraction IS NOT NULL</c>); null — истории нет.</summary>
    Task<MarketIndexPoint?> GetLatestIndexPointAsync(string indexCode, CancellationToken ct = default);

    Task<DateOnly?> GetLastIndexDateAsync(string indexCode, CancellationToken ct = default);
}
