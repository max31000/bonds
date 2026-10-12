namespace Bonds.Core.Models;

/// <summary>
/// Событие портфеля, требующее внимания владельца (spec §8; plan/40). Это НЕ append-only лог, а
/// «текущее состояние»: каждый цикл Signals Engine пересчитывает полный набор активных событий и
/// синхронизирует его с таблицей (<see cref="Bonds.Core.Signals.SignalSetSynchronizer"/>):
/// новый ключ → вставка, прежний → обновление <see cref="LastSeenAt"/>, исчезнувший →
/// <see cref="ResolvedAt"/>. Подтверждение (<see cref="AcknowledgedAt"/>) переживает любые
/// пересчёты и не сбрасывается.
/// </summary>
public class Signal
{
    public ulong Id { get; set; }
    public ulong AccountId { get; set; }

    /// <summary>
    /// Ключ идентичности события в рамках счёта: тип + инструмент/эмитент + дата события (см.
    /// <see cref="BuildKey"/>). Уникален в БД вместе с account_id.
    /// </summary>
    public string EventKey { get; set; } = string.Empty;

    public SignalType Type { get; set; }
    public SignalSeverity Severity { get; set; }

    public ulong? InstrumentId { get; set; }

    /// <summary>Имя бумаги либо эмитента — то, что показывается на карточке события (не «позиция #id»).</summary>
    public string? Subject { get; set; }

    public string? SuggestedAction { get; set; }

    /// <summary>Дата события (оферта/погашение/пересчёт ставки). NULL — для «состояний» (концентрация, свободные деньги).</summary>
    public DateOnly? EventDate { get; set; }

    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }

    /// <summary>Момент (UTC), когда движок перестал производить это событие. NULL — событие активно.</summary>
    public DateTime? ResolvedAt { get; set; }

    /// <summary>Момент (UTC) подтверждения владельцем («прочитано»). NULL — не подтверждено.</summary>
    public DateTime? AcknowledgedAt { get; set; }

    public bool IsResolved => ResolvedAt is not null;
    public bool IsAcknowledged => AcknowledgedAt is not null;

    /// <summary>
    /// Ключ события: <c>Type|subjectKey|yyyy-MM-dd</c>. subjectKey — id инструмента для событий по
    /// бумаге либо имя эмитента для состояний по эмитенту; пусто для счётных состояний. Дата пустая
    /// для состояний (концентрация, свободные деньги) — их идентичность не зависит от дня.
    /// </summary>
    public static string BuildKey(SignalType type, string? subjectKey, DateOnly? eventDate)
    {
        // event_key — VARCHAR(255): длинное имя эмитента (ключ концентрации) иначе роняло бы вставку
        // и весь шаг событий. Обрезаем с хеш-суффиксом, чтобы разные длинные имена не слиплись.
        var subject = subjectKey ?? string.Empty;
        if (subject.Length > MaxSubjectKeyLength)
        {
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(subject)))[..12];
            subject = $"{subject[..(MaxSubjectKeyLength - 13)]}~{hash}";
        }

        return $"{type}|{subject}|{eventDate:yyyy-MM-dd}";
    }

    private const int MaxSubjectKeyLength = 200;
}

/// <summary>
/// Типы событий. Удалены: UpcomingCoupon (купоны — календарь на «Денежном потоке»),
/// YieldBelowAlternative (дублирует «слабые позиции» Рекомендаций), DurationDriftFromTarget
/// (цели никто не задаёт), LowLiquidityWarning (заглушка).
/// </summary>
public enum SignalType
{
    UpcomingAmortization,
    UpcomingRedemption,
    UpcomingOffer,
    FloaterRateReset,
    UninvestedCashThreshold,
    ConcentrationLimitBreached,
}

public enum SignalSeverity
{
    Info,
    Warning,
    Critical,
}
