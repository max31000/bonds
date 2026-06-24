using Bonds.Core.Models;

namespace Bonds.Core.Interfaces.Repositories;

public interface IMarketQuoteRepository
{
    Task<MarketQuote?> GetLatestAsync(ulong instrumentId);
    Task<IEnumerable<MarketQuote>> GetHistoryAsync(ulong instrumentId, DateOnly from, DateOnly to);

    /// <summary>Upsert по (InstrumentId, AsOf, Source) — повторная загрузка котировки на ту же дату не дублирует строку.</summary>
    Task UpsertAsync(MarketQuote quote);
}
