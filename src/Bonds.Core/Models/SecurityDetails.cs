namespace Bonds.Core.Models;

/// <summary>
/// Задача 43: персистентный кэш карточки бумаги MOEX ISS (<c>/iss/securities/{secid}.json?iss.only=description</c>).
/// Хранится ОТДЕЛЬНО от <see cref="BondUniverseEntry"/>: банк перезаписывается каждый час, а карточки
/// тяжёлые (один запрос на бумагу) и обновляются медленно (портфель/watchlist и новые бумаги первыми,
/// остальные — раз в ~30 дней). Ключ — SECID (связь с банком/инструментами по строке, без FK).
/// <para>
/// Все nullable-флаги трёхзначные: <c>null</c> = «карточка поле не отдала / неизвестно», это НЕ то же
/// самое, что <c>false</c> (потребители не должны трактовать null как «дефолта нет»).
/// </para>
/// <para>
/// <b>Единицы:</b> <see cref="CouponBenchmarkSpreadFraction"/> — ДОЛЯ (MOEX отдаёт проценты, ÷100 при
/// разборе карточки: "1.90" → 0.019). <see cref="FetchedAt"/> — UTC.
/// </para>
/// </summary>
public sealed class SecurityDetails
{
    public string Secid { get; set; } = string.Empty;
    public string? Isin { get; set; }

    /// <summary>EMITTER_ID карточки — идентификатор эмитента MOEX (ключ дефолт-истории по всем выпускам эмитента).</summary>
    public int? EmitentId { get; set; }

    /// <summary>HASDEFAULT: по выпуску допущен дефолт.</summary>
    public bool? HasDefault { get; set; }

    /// <summary>HASTECHNICALDEFAULT: по выпуску допущен технический дефолт.</summary>
    public bool? HasTechnicalDefault { get; set; }

    /// <summary>ISQUALIFIEDINVESTORS: бумага только для квалифицированных инвесторов.</summary>
    public bool? IsQualifiedInvestors { get; set; }

    /// <summary>COUPON_BENCHMARK: бенчмарк плавающего купона (RUONIA, RREFKEYR, CPI…).</summary>
    public string? CouponBenchmark { get; set; }

    /// <summary>ДОЛЯ: надбавка к бенчмарку (0.019 = 1.90 п.п.).</summary>
    public decimal? CouponBenchmarkSpreadFraction { get; set; }

    /// <summary>EARLYREPAYMENT: предусмотрено досрочное погашение.</summary>
    public bool? EarlyRepayment { get; set; }

    /// <summary>Когда карточка была получена с MOEX, UTC.</summary>
    public DateTime FetchedAt { get; set; }
}

/// <summary>
/// Задача 43: агрегат дефолт-истории эмитента по ВСЕМ его выпускам в <c>security_details</c>
/// (<c>MAX(has_default)</c>, <c>MAX(has_technical_default)</c>). <c>true</c> — хотя бы один выпуск с
/// дефолтом; <c>false</c> — у всех известных выпусков флаг 0; <c>null</c> — флаг неизвестен ни по одному
/// выпуску (карточки ещё не загружены либо поле не отдано) — «неизвестно», не «чисто».
/// Потребители (светофор надёжности) подключаются задачей 46.
/// </summary>
public sealed record EmitentDefaultHistory(int EmitentId, bool? HasDefault, bool? HasTechnicalDefault);

/// <summary>
/// Задача 43: эмитент выпуска из bulk-справочника MOEX ISS для записи в <c>bond_universe</c>
/// (любое поле может быть null — старые/иностранные выпуски отдаются без ИНН/эмитента).
/// </summary>
public sealed record UniverseEmitent(string Secid, int? EmitentId, string? EmitentInn, string? EmitentTitle);
