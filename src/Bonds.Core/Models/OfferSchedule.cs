namespace Bonds.Core.Models;

/// <summary>
/// График оферт (put/call). Источник истины — MOEX ISS (spec §4.2, §4.4 — для бумаг
/// с офертой расчёты "к погашению" некорректны, см. spec §6/§7.3).
/// </summary>
public class OfferSchedule
{
    public ulong Id { get; set; }
    public ulong InstrumentId { get; set; }

    public DateOnly Date { get; set; }
    public OfferType OfferType { get; set; }

    /// <summary>Оферта уже была исполнена/прошла (для фильтрации "ближайшей неисполненной", spec §7.3).</summary>
    public bool IsExecuted { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>Put — инвестор может предъявить бумагу к выкупу (требует активного действия).
/// Call — эмитент может отозвать бумагу.</summary>
public enum OfferType
{
    Put,
    Call,
}
