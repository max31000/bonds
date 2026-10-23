using Bonds.Core.Models;

namespace Bonds.Core.Interfaces.Repositories;

/// <summary>Пополнения и выводы счёта T-Invest (задача 61).</summary>
public interface IAccountCashMovementRepository
{
    /// <summary>Идемпотентный upsert по (account_id, external_id). Возвращает число обработанных.</summary>
    Task<int> UpsertManyAsync(IReadOnlyCollection<AccountCashMovement> movements);

    /// <summary>Последние <paramref name="limit"/> движений счёта, новые сверху.</summary>
    Task<IReadOnlyList<AccountCashMovement>> GetRecentAsync(ulong accountId, int limit);
}

/// <summary>Торговые метаданные инструментов (задача 61). Общие для пользователей.</summary>
public interface IInstrumentTradingMetaRepository
{
    /// <summary>Upsert по (broker, instrument_key); <c>updated_at</c> берётся из модели.</summary>
    Task UpsertAsync(InstrumentTradingMeta meta);

    /// <summary>Все записи брокера (таблица небольшая: позиции + вселенная фондов).</summary>
    Task<IReadOnlyList<InstrumentTradingMeta>> GetByBrokerAsync(string broker);

    /// <summary>Записи брокера по ключам (ISIN или тикер, регистр не важен).</summary>
    Task<IReadOnlyList<InstrumentTradingMeta>> GetByKeysAsync(string broker, IReadOnlyCollection<string> keys);
}
