using Bonds.Core.Models;

namespace Bonds.Core.Interfaces.Repositories;

/// <summary>Хранилище тиков "лёгкого контура котировок" (plan/16 часть A). См. doc-comment IntradayQuote.</summary>
public interface IIntradayQuoteRepository
{
    /// <summary>Пишет тик и в той же операции удаляет строки старше <paramref name="retentionCutoffUtc"/> (retention 8 дней, plan/16).</summary>
    Task InsertAndPruneAsync(IntradayQuote quote, DateTime retentionCutoffUtc);

    /// <summary>Последний известный тик по инструменту (fallback-цена для GET /api/live/positions).</summary>
    Task<IntradayQuote?> GetLatestAsync(ulong instrumentId);

    /// <summary>Все тики нескольких инструментов в окне [from, to] UTC, для сборки интрадей-ряда (IntradaySeriesBuilder).</summary>
    Task<IReadOnlyList<IntradayQuote>> GetRangeAsync(IReadOnlyCollection<ulong> instrumentIds, DateTime fromUtc, DateTime toUtc);
}
