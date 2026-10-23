using Bonds.Core.Models;

namespace Bonds.Core.Interfaces.Repositories;

/// <summary>Позиции не-T-Invest счетов (задача 60). Цены/суммы — в валюте позиции.</summary>
public interface IBrokerPositionRepository
{
    Task<IReadOnlyList<BrokerPosition>> GetByAccountAsync(ulong accountId);

    /// <summary>Upsert по (account_id, ticker).</summary>
    Task UpsertAsync(BrokerPosition position);

    /// <summary>Сверка: удаляет позиции счёта, чьих тикеров НЕТ в <paramref name="keepTickers"/>. Возвращает число удалённых.</summary>
    Task<int> DeleteAllExceptAsync(ulong accountId, IReadOnlyCollection<string> keepTickers);
}

public interface IBrokerCashRepository
{
    Task<IReadOnlyList<BrokerCash>> GetByAccountAsync(ulong accountId);

    /// <summary>Upsert по (account_id, currency).</summary>
    Task UpsertAsync(BrokerCash cash);

    /// <summary>Удаляет остатки счёта в валютах, которых НЕТ в <paramref name="keepCurrencies"/>. Возвращает число удалённых.</summary>
    Task<int> DeleteAllExceptAsync(ulong accountId, IReadOnlyCollection<string> keepCurrencies);
}

public interface IBrokerOperationRepository
{
    /// <summary>Идемпотентный upsert по (account_id, external_id). Возвращает число обработанных.</summary>
    Task<int> UpsertManyAsync(IReadOnlyCollection<BrokerOperation> operations);

    /// <summary>Журнал счёта за период (границы включительно, по дате; null — без границы), новые сверху.</summary>
    Task<IReadOnlyList<BrokerOperation>> GetByAccountAsync(ulong accountId, DateOnly? from, DateOnly? to);

    /// <summary>Операции одного типа за всё время (для итога комиссий).</summary>
    Task<IReadOnlyList<BrokerOperation>> GetByTypeAsync(ulong accountId, BrokerOperationType type);
}
