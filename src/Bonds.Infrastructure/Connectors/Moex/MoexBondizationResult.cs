using Bonds.Core.Models;

namespace Bonds.Infrastructure.Connectors.Moex;

/// <summary>
/// Результат разбора MOEX ISS bondization-ответа (купоны + амортизации + оферты) для одного SECID.
/// <see cref="DataIncomplete"/> отражает риск §4.4: часть купонов могла не вернуться от MOEX —
/// в этом случае сохраняем то, что есть, не подставляем нули и помечаем флагом, не падаем.
/// </summary>
public sealed class MoexBondizationResult
{
    public required string Secid { get; init; }

    public List<CouponSchedule> Coupons { get; init; } = [];
    public List<AmortizationSchedule> Amortizations { get; init; } = [];
    public List<OfferSchedule> Offers { get; init; } = [];

    /// <summary>
    /// True, если в купонном графике обнаружен разрыв (пропущенный период) до даты погашения,
    /// либо ISS вернул пустой блок купонов для бумаги, которая по справочнику должна иметь купоны.
    /// См. plan/04 Часть A п.5, spec §4.4 "Неполные купоны в bondization".
    /// </summary>
    public bool DataIncomplete { get; init; }
}
