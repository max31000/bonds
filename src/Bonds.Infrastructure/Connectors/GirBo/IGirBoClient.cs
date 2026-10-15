using Bonds.Core.Models;

namespace Bonds.Infrastructure.Connectors.GirBo;

/// <summary>
/// Клиент ГИР БО ФНС (bo.nalog.gov.ru) — открытая бухгалтерская отчётность организаций по ИНН, без
/// авторизации (нужен User-Agent). Семантика результатов: «не найдено» (404/пустая выдача/пустой список)
/// — НОРМАЛЬНЫЙ результат (null/пустой список), а недоступность источника (сеть, таймаут, 5xx/429,
/// нечитаемый ответ) — <see cref="GirBoUnavailableException"/>. Так вызывающий код отличает «эмитента там
/// нет» от «сервис лёг».
/// </summary>
public interface IGirBoClient
{
    /// <summary>Организация по ИНН (<c>advanced-search</c>); null — не найдена.</summary>
    Task<GirBoOrganization?> FindOrganizationAsync(string inn, CancellationToken ct = default);

    /// <summary>Подачи отчётности организации; пустой список — подач нет (отчётность скрыта).</summary>
    Task<IReadOnlyList<GirBoFiling>> GetFilingsAsync(long organizationId, CancellationToken ct = default);

    /// <summary>
    /// Годовые записи (до трёх лет, РУБЛИ) из подачи <paramref name="bfoId"/> за отчётный год
    /// <paramref name="period"/>; пустой список — деталей/годовых данных нет. См. <see cref="GirBoParser"/>.
    /// </summary>
    Task<IReadOnlyList<IssuerFinancials>> GetAnnualFinancialsAsync(
        long bfoId, string inn, int period, DateTime fetchedAtUtc, CancellationToken ct = default);
}
