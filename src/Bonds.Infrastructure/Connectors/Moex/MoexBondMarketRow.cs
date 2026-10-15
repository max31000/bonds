namespace Bonds.Infrastructure.Connectors.Moex;

/// <summary>
/// Одна строка снимка всей рыночной вселенной облигаций MOEX —
/// <c>/iss/engines/stock/markets/bonds/securities.json?iss.only=securities,marketdata,marketdata_yields</c>,
/// слитые по (SECID, BOARDID) строки блоков <c>securities</c> (справочные параметры выпуска),
/// <c>marketdata</c> (текущие котировки/доходность/дюрация) и <c>marketdata_yields</c> (расчёты
/// самой биржи: эффективная доходность, G-/Z-спред, тип даты доходности — задача 42). Единицы — КАК ПРИШЛИ ОТ MOEX
/// (ещё не переведены в доли/годы этого сервиса) — конвертация в <see cref="BondUniverseEntry"/>
/// происходит на уровне <c>BondUniverseRefreshService</c>/маппера репозитория, см. doc-comment там
/// про YIELD (%→доля) и DURATION (дни→годы).
/// <para>
/// Один SECID может встретиться несколько раз (разные BOARDID) — дедупликация по максимальному
/// <see cref="TurnoverRub"/> (оборот) выполняется ПОСЛЕ парсинга, в <see cref="MoexBondUniverseParser"/>,
/// не здесь: этот тип — сырая строка "как есть" без выбора "лучшего" board.
/// </para>
/// </summary>
public sealed class MoexBondMarketRow
{
    public required string Secid { get; init; }
    public required string BoardId { get; init; }

    public string? Isin { get; init; }
    public string? ShortName { get; init; }
    public string? SecName { get; init; }

    /// <summary>Номинал, в валюте FaceUnit (обычно RUB) — НЕ переводить, хранится как есть.</summary>
    public decimal? FaceValue { get; init; }

    /// <summary>Стоимость лота — используется только справочно, единицы MOEX как есть.</summary>
    public decimal? LotValue { get; init; }

    /// <summary>Валюта номинала (колонка FACEUNIT) — "SUR"/"RUB" = рублёвая бумага, прочее — не рублёвая, исключается (план часть A.2).</summary>
    public string? FaceUnit { get; init; }

    /// <summary>Ставка купона в % (как в MoexSecurityInfo.CouponPercent) — НЕ доля.</summary>
    public decimal? CouponPercent { get; init; }

    public int? CouponPeriod { get; init; }
    public DateOnly? MatDate { get; init; }
    public DateOnly? OfferDate { get; init; }

    /// <summary>Уровень листинга MOEX (1/2/3) — колонка LISTLEVEL.</summary>
    public int? ListLevel { get; init; }

    /// <summary>Код типа бумаги ISS (колонка SECTYPE) — числовой код, НЕ то же самое что MoexSecuritySearch.TypeCode
    /// ("ofz_bond" и т.п. из другого эндпоинта поиска). Маппинг в сектор — простая классификация
    /// по этому коду, см. <c>BondUniverseSectorMapper</c>.</summary>
    public string? SecType { get; init; }

    /// <summary>Человекочитаемая классификация ISS (колонка BONDTYPE, например "Флоатер",
    /// "Фикс с известным купоном") — та же эвристика флоатера, что MoexSecurityInfo.LooksLikeFloater.</summary>
    public string? BondType { get; init; }

    /// <summary>"A" = торгуется, иные значения — не активна/погашается (колонка STATUS).</summary>
    public string? Status { get; init; }

    /// <summary>НКД в РУБЛЯХ на ОДНУ бумагу (колонка ACCRUEDINT блока securities) — НЕ в процентах и не на
    /// лот: у бумаги с номиналом 1000 и купоном ~35 ₽ значение 0–35 (проверено по живому ответу
    /// 2026-10-15: SU26238RMFS4 → 26.26). Для валютных бумаг приходит в валюте номинала, но они
    /// отфильтровываются выше по стеку (FACEUNIT).</summary>
    public decimal? AccruedRub { get; init; }

    /// <summary>Объём выпуска, штук бумаг (колонка ISSUESIZE блока securities) — целое, может
    /// превышать int32, поэтому long.</summary>
    public long? IssueSize { get; init; }

    // --- marketdata (может отсутствовать целиком, если строка нашлась только в securities) ---

    /// <summary>Доходность в ПРОЦЕНТАХ, как отдаёт MOEX (колонка YIELD) — НЕ доля. Null, если сделок не было.</summary>
    public decimal? YieldPercent { get; init; }

    /// <summary>Дюрация в ДНЯХ, как отдаёт MOEX (колонка DURATION) — НЕ годы.</summary>
    public int? DurationDays { get; init; }

    /// <summary>Цена последней сделки, % от номинала (колонка LAST, приоритет) с фолбэком на MARKETPRICE.</summary>
    public decimal? PricePercent { get; init; }

    /// <summary>Оборот в рублях за сегодня (колонка VALTODAY).</summary>
    public decimal? TurnoverRub { get; init; }

    public decimal? BidPercent { get; init; }
    public decimal? OfferPricePercent { get; init; }
    public int? NumTrades { get; init; }

    // --- marketdata_yields (расчёты биржи; нет строки для (SECID, BOARDID) — все поля null) ---

    /// <summary>Эффективная доходность в ПРОЦЕНТАХ, как отдаёт MOEX (колонка EFFECTIVEYIELD) — НЕ доля.</summary>
    public decimal? EffectiveYieldPercent { get; init; }

    /// <summary>Доходность к оферте в ПРОЦЕНТАХ (колонка YIELDTOOFFER блока marketdata_yields) — НЕ доля.</summary>
    public decimal? YieldToOfferPercent { get; init; }

    /// <summary>К какой дате биржа считает доходность: "MATDATE" (к погашению), "OFFER" (к оферте),
    /// "MBS" и т.п. — строка как есть (колонка YIELDDATETYPE).</summary>
    public string? YieldDateType { get; init; }

    /// <summary>G-спред биржи в БАЗИСНЫХ ПУНКТАХ (колонка GSPREADBP): 35 = 0.35 п.п. = 35 б.п. — НЕ доля.</summary>
    public decimal? GspreadBp { get; init; }

    /// <summary>Z-спред биржи в БАЗИСНЫХ ПУНКТАХ (колонка ZSPREADBP) — НЕ доля.</summary>
    public decimal? ZspreadBp { get; init; }
}
