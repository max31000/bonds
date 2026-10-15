namespace Bonds.Core.Models;

/// <summary>Источники записей <c>issuer_financials</c>: автозагрузка ГИР БО и ручной ввод владельца.</summary>
public static class IssuerFinancialsSources
{
    public const string GirBo = "girbo";
    public const string Manual = "manual";
}

/// <summary>Статусы последней проверки эмитента в ГИР БО (<c>issuer_financials_status.status</c>).</summary>
public static class IssuerFinancialsStatuses
{
    /// <summary>Отчётность найдена и сохранена.</summary>
    public const string Ok = "ok";

    /// <summary>ИНН не найден в реестре ГИР БО.</summary>
    public const string NotFound = "not_found";

    /// <summary>Организация есть, но годовых подач/деталей нет (отчётность скрыта, пост. №1624, либо пустая).</summary>
    public const string Hidden = "hidden";

    /// <summary>Кредитная организация (<c>isCb</c>): отчитывается в ЦБ, в ГИР БО РСБУ нет.</summary>
    public const string Cb = "cb";

    /// <summary>Сбой обработки конкретного ИНН (повторить раньше, чем через 30 дней).</summary>
    public const string Error = "error";
}

/// <summary>
/// Задача 45: годовая отчётность эмитента (РСБУ, соло — не консолидация) за один год. Ключ —
/// (<see cref="Inn"/>, <see cref="Period"/>, <see cref="Source"/>), связь с инструментами по ИНН-строке
/// (как watchlist по ISIN), без FK.
/// <para>
/// <b>Единицы: ВСЕ денежные поля — РУБЛИ.</b> ГИР БО отдаёт тысячи рублей, конверсия ×1000 выполняется
/// ровно один раз на границе парсинга (<c>GirBoParser.ThousandsToRubles</c>); дальше по системе только
/// рубли. Любое поле nullable: <c>null</c> = «строка в форме не заполнена/неизвестна», НЕ ноль.
/// <see cref="FetchedAt"/> — UTC.
/// </para>
/// </summary>
public sealed class IssuerFinancials
{
    public string Inn { get; set; } = string.Empty;

    /// <summary>Отчётный год (SMALLINT в БД).</summary>
    public int Period { get; set; }

    /// <summary>См. <see cref="IssuerFinancialsSources"/>.</summary>
    public string Source { get; set; } = IssuerFinancialsSources.GirBo;

    public DateTime FetchedAt { get; set; }

    /// <summary>Выручка (форма 2, код 2110), ₽.</summary>
    public decimal? Revenue { get; set; }

    /// <summary>Прибыль (убыток) от продаж (2200), ₽.</summary>
    public decimal? OperatingProfit { get; set; }

    /// <summary>Проценты к уплате (2330), ₽.</summary>
    public decimal? InterestPaid { get; set; }

    /// <summary>Чистая прибыль (убыток) (2400), ₽.</summary>
    public decimal? NetProfit { get; set; }

    /// <summary>Капитал и резервы (баланс, 1300), ₽.</summary>
    public decimal? Equity { get; set; }

    /// <summary>Баланс — активы всего (1600), ₽.</summary>
    public decimal? TotalAssets { get; set; }

    /// <summary>Оборотные активы (1200), ₽.</summary>
    public decimal? CurrentAssets { get; set; }

    /// <summary>Краткосрочные обязательства (1500), ₽.</summary>
    public decimal? CurrentLiabilities { get; set; }

    /// <summary>Денежные средства и эквиваленты (1250), ₽.</summary>
    public decimal? Cash { get; set; }

    /// <summary>Долгосрочные заёмные средства (1410), ₽.</summary>
    public decimal? LongDebt { get; set; }

    /// <summary>Краткосрочные заёмные средства (1510), ₽.</summary>
    public decimal? ShortDebt { get; set; }

    /// <summary>Нераспределённая прибыль (убыток) (1370), ₽.</summary>
    public decimal? RetainedEarnings { get; set; }

    /// <summary>
    /// Есть аудиторское заключение. Известно только для ОТЧЁТНОГО года подачи (заключение относится к
    /// нему); для предыдущих лет той же подачи — <c>null</c> (неизвестно).
    /// </summary>
    public bool? HasAudit { get; set; }
}

/// <summary>Результат последней проверки эмитента в ГИР БО. <see cref="CheckedAt"/> — UTC.</summary>
public sealed record IssuerFinancialsStatus(string Inn, string Status, DateTime CheckedAt);
