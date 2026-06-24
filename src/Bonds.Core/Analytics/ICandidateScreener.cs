namespace Bonds.Core.Analytics;

/// <summary>
/// Точка расширения (plan/00 §11, plan/06 Часть C, spec §3 «Вне скоупа») — скринер кандидатов
/// на замену по ВСЕЙ вселенной бумаг (не только текущим позициям портфеля). На MVP анализ
/// замены ограничен текущими позициями (см. <see cref="SwitchAnalysisService"/>) — этот интерфейс
/// НЕ реализуется на этапе 06 и не имеет регистрации в DI; он зафиксирован как контракт для
/// будущей фичи, чтобы вызывающий код (этап 08/09) мог заранее знать форму API, если решит
/// строить экран "найти лучшую альтернативу на бирже", не переписывая <see cref="SwitchAnalysisService"/>.
/// </summary>
public interface ICandidateScreener
{
    /// <summary>
    /// Находит до <paramref name="limit"/> кандидатов из всей вселенной торгуемых бумаг (не только
    /// текущих позиций), отсортированных по эффективной доходности, сопоставимых по сроку/риску
    /// с переданным эталоном. НЕ РЕАЛИЗОВАНО на MVP — потребует скоринга по сектору/кредитному
    /// качеству, которого сейчас нет (рейтинги — вне MVP, см. <c>IRatingProvider</c> в plan/00 §11).
    /// </summary>
    Task<IReadOnlyList<SwitchCandidate>> FindCandidatesAsync(
        decimal targetDurationYears,
        int limit,
        CancellationToken ct = default);
}
