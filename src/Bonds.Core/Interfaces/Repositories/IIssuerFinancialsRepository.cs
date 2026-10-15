using Bonds.Core.Models;

namespace Bonds.Core.Interfaces.Repositories;

/// <summary>
/// Задача 45: хранилище отчётности эмитентов (<c>issuer_financials</c>) и статусов проверки ГИР БО
/// (<c>issuer_financials_status</c>). Потребитель кредитного слоя скоркарты — задача 46.
/// </summary>
public interface IIssuerFinancialsRepository
{
    /// <summary>
    /// Идемпотентный батч-upsert по (inn, period, source): повтор обновляет строку, не дублирует.
    /// Записи другого <c>source</c> (например, 'manual') не затрагиваются.
    /// </summary>
    Task UpsertAsync(IReadOnlyList<IssuerFinancials> items, CancellationToken ct = default);

    /// <summary>
    /// Последние <paramref name="yearsPerInn"/> отчётных лет по каждому ИНН одним запросом, по убыванию
    /// года. Если за год есть и 'manual', и 'girbo' — возвращается ОДНА запись, ручная приоритетнее
    /// (владелец поправил → его значение главное). ИНН без данных в результат не попадает.
    /// </summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<IssuerFinancials>>> GetLatestByInnsAsync(
        IReadOnlyCollection<string> inns, int yearsPerInn = 3, CancellationToken ct = default);

    /// <summary>Идемпотентный upsert статуса проверки эмитента.</summary>
    Task UpsertStatusAsync(IssuerFinancialsStatus status, CancellationToken ct = default);

    /// <summary>Статусы по списку ИНН (отсутствующие не возвращаются).</summary>
    Task<IReadOnlyDictionary<string, IssuerFinancialsStatus>> GetStatusesAsync(
        IReadOnlyCollection<string> inns, CancellationToken ct = default);

    /// <summary>
    /// Очередь обновления: до <paramref name="limit"/> ИНН, которым нужна проверка (статуса нет, ИЛИ статус
    /// 'error' старше <paramref name="errorRetryBeforeUtc"/>, ИЛИ любой другой статус старше
    /// <paramref name="staleBeforeUtc"/>; ИНН не из 10/12 цифр отбрасываются). Источники — через
    /// <c>bond_universe.emitent_inn</c> по ISIN, три приоритета: (1) эмитенты позиций портфеля (quantity &gt; 0) и
    /// watchlist — без статуса, затем самые давно проверенные; (2) все остальные уникальные ИНН банка
    /// (<c>bond_universe</c>) без статуса — прогрев покрытия скоркарты/скринера; (3) ИНН банка с протухшим статусом,
    /// самые давно проверенные первыми; ИНН — детерминированный хвост. Портфель/watchlist, нуждающиеся в проверке,
    /// всегда идут перед банком, чтобы многонедельный прогрев банка не оставлял портфельную отчётность устаревшей.
    /// </summary>
    Task<IReadOnlyList<string>> GetRefreshQueueAsync(
        int limit, DateTime staleBeforeUtc, DateTime errorRetryBeforeUtc, CancellationToken ct = default);
}
