namespace Bonds.Core.Signals;

/// <summary>
/// Настраиваемые параметры триггеров Signals Engine (plan/07 Часть A, spec §8). Дефолты —
/// самостоятельное решение имплементатора (задание явно допускает корректировку), подобраны как
/// разумные ориентиры для частного облигационного портфеля, не калибровка по историческим данным.
/// В будущем могут переехать в настройки пользователя/БД — на MVP это POCO с дефолтами,
/// конфигурируемый через стандартный <c>IOptions&lt;SignalEngineOptions&gt;</c> (секция конфига
/// "Signals"), либо передаваемый напрямую вызывающим кодом (Infrastructure) без DI, если так проще.
/// </summary>
public sealed class SignalEngineOptions
{
    /// <summary>
    /// За сколько дней до даты события (купон/амортизация/погашение/оферта/пересчёт ставки
    /// флоатера) генерировать сигнал "приближается". Один порог для всех типов событий —
    /// упрощение MVP, разные пороги под каждый тип события были бы избыточной конфигурацией
    /// без явного запроса от владельца продукта.
    /// </summary>
    public int UpcomingEventDaysThreshold { get; set; } = 14;

    /// <summary>
    /// Порог незаинвестированного кэша в рублях, выше которого генерируется напоминание
    /// о реинвесте (spec §8). Эвристика расчёта кэша — см. doc-comment
    /// <see cref="UninvestedCashRule"/>.
    /// </summary>
    public decimal UninvestedCashThresholdRub { get; set; } = 10_000m;

    /// <summary>
    /// За сколько последних дней суммировать поступления/покупки при оценке незаинвестированного
    /// кэша (скользящее окно, а не "с начала времён" — иначе сигнал не угасал бы после реинвеста
    /// старых поступлений, см. doc-comment <see cref="UninvestedCashRule"/>).
    /// </summary>
    public int UninvestedCashLookbackDays { get; set; } = 90;

    /// <summary>Порог расхождения доходности с сопоставимой по сроку альтернативой, в базисных пунктах (spec §8).</summary>
    public int YieldBelowAlternativeBpsThreshold { get; set; } = 50;

    /// <summary>
    /// Окно сопоставимости по сроку (±N дней вокруг HorizonDate/MaturityDate) при поиске
    /// "альтернативы из портфеля" для сигнала доходности (spec §8 "сопоставимая по сроку").
    /// </summary>
    public int MaturityWindowDaysForAlternativeComparison { get; set; } = 180;

    /// <summary>
    /// Дефолтный лимит концентрации по эмитенту в процентах — применяется, если для конкретного
    /// эмитента нет записи <see cref="Bonds.Core.Models.TargetAllocation.MaxConcentrationPercent"/>.
    /// 25% — разумный ориентир диверсификации для портфеля из единиц-десятков позиций (plan/00 §1).
    /// </summary>
    public decimal DefaultMaxConcentrationPercent { get; set; } = 25m;

    /// <summary>Допустимое отклонение модифицированной дюрации портфеля от целевой (в годах) до срабатывания сигнала дрейфа (spec §8).</summary>
    public decimal DurationDriftToleranceYears { get; set; } = 1.0m;
}
