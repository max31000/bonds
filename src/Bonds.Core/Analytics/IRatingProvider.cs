namespace Bonds.Core.Analytics;

/// <summary>
/// Точка расширения (plan/00 §11, spec §3 «Вне скоупа», spec §4.3 «платные зоны») — кредитные
/// рейтинги облигаций. Структурированная выгрузка рейтингов платная у всех агентств (АКРА,
/// Эксперт РА, НКР, НРА) и у агрегаторов (Cbonds/Investfunds) — полностью вне MVP, ни один модуль
/// не зависит от этого интерфейса. НЕ РЕАЛИЗОВАНО и не зарегистрировано в DI; зафиксирован как
/// контракт на случай, если в будущем появится платный фид и понадобится сигнал «снижение
/// рейтинга» (spec §8, явно вынесен из MVP) или колонка рейтинга в сравнении позиций (<see
/// cref="PositionComparisonService"/>) — чтобы вызывающий код мог опереться на готовую форму API,
/// не переписывая существующие сервисы.
/// </summary>
public interface IRatingProvider
{
    /// <summary>
    /// Текущий кредитный рейтинг инструмента по ISIN, если доступен у подключённого провайдера.
    /// НЕ РЕАЛИЗОВАНО на MVP — нет подключённого платного источника рейтингов.
    /// </summary>
    Task<RatingInfo?> GetRatingAsync(string isin, CancellationToken ct = default);
}

/// <summary>Снимок кредитного рейтинга инструмента от одного агентства (точка расширения, не используется на MVP).</summary>
public sealed record RatingInfo
{
    public required string Isin { get; init; }
    public required string Agency { get; init; }
    public required string RatingCode { get; init; }
    public required DateOnly AsOf { get; init; }
}
