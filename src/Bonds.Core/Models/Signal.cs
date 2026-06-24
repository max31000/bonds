namespace Bonds.Core.Models;

/// <summary>
/// Сгенерированное событие-триггер (spec §5, §8). Заполняется Signals Engine (этап 07);
/// здесь только хранилище.
/// </summary>
public class Signal
{
    public ulong Id { get; set; }
    public ulong AccountId { get; set; }

    public SignalType Type { get; set; }
    public SignalSeverity Severity { get; set; }

    public ulong? PositionId { get; set; }
    public ulong? InstrumentId { get; set; }

    public string? SuggestedAction { get; set; }

    public DateOnly Date { get; set; }
    public bool IsRead { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>Типы триггеров по spec §8.</summary>
public enum SignalType
{
    UpcomingCoupon,
    UpcomingAmortization,
    UpcomingRedemption,
    UpcomingOffer,
    FloaterRateReset,
    UninvestedCashThreshold,
    YieldBelowAlternative,
    ConcentrationLimitBreached,
    DurationDriftFromTarget,
    LowLiquidityWarning,
}

public enum SignalSeverity
{
    Info,
    Warning,
    Critical,
}
