using Bonds.Core.Models;

namespace Bonds.Core.Interfaces.Repositories;

/// <summary>Задача 46: рейтинги эмитентов, введённые владельцем вручную (<c>issuer_ratings</c>).</summary>
public interface IIssuerRatingRepository
{
    /// <summary>Рейтинги одного эмитента (все агентства), по агентству.</summary>
    Task<IReadOnlyList<IssuerRating>> GetByInnAsync(string inn, CancellationToken ct = default);

    /// <summary>Рейтинги списка эмитентов одним запросом; ИНН без рейтингов в результат не попадают.</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<IssuerRating>>> GetByInnsAsync(
        IReadOnlyCollection<string> inns, CancellationToken ct = default);

    /// <summary>Идемпотентный upsert по (inn, agency): у агентства одна актуальная запись.</summary>
    Task UpsertAsync(IssuerRating rating, CancellationToken ct = default);

    /// <summary>Удаляет рейтинг агентства; false — записи не было.</summary>
    Task<bool> DeleteAsync(string inn, string agency, CancellationToken ct = default);
}
