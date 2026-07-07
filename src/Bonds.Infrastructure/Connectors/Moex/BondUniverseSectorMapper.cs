namespace Bonds.Infrastructure.Connectors.Moex;

/// <summary>
/// Задача 26 часть B — простая классификация "Гособлигации"/"Муниципальные"/"Корпоративные" для
/// снимка вселенной облигаций по коду MOEX ISS SECTYPE (колонка <c>securities.SECTYPE</c> в ответе
/// <c>/iss/engines/stock/markets/bonds/securities.json</c> — числовой код, НЕ строковый
/// <c>MoexSecuritySearch.TypeCode</c> вида "ofz_bond" из другого эндпоинта поиска, для которого уже
/// существует <see cref="MoexSegmentMapper"/>). Коды проверены эмпирически по реальному ответу ISS
/// на дату внедрения (2026-07): 3 = ОФЗ, 4 и C = субфедеральные/муниципальные облигации регионов,
/// 6/8 = прочие/корпоративные (включая облигации иностранных государств, торгуемые в РФ — грубо
/// относим к "Корпоративные", т.к. не являются облигациями РФ). MOEX не документирует этот
/// маппинг публично — при появлении новых кодов они попадают в fallback "Корпоративные" (не null),
/// чтобы сектор в UI не пустовал молча.
/// </summary>
public static class BondUniverseSectorMapper
{
    private const string Government = "Гособлигации";
    private const string Municipal = "Муниципальные";
    private const string Corporate = "Корпоративные";

    public static string MapSecTypeToSector(string? secType) => secType switch
    {
        "3" => Government,
        "4" or "C" => Municipal,
        _ => Corporate,
    };
}
