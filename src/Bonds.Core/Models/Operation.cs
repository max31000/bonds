namespace Bonds.Core.Models;

/// <summary>
/// Событие журнала операций по счёту — источник истины для XIRR (spec §5, §6.9).
/// <see cref="ExternalId"/> — идентификатор операции в T-Invest, используется для
/// идемпотентного upsert при повторном синке (plan/03 §C).
/// </summary>
public class Operation
{
    public ulong Id { get; set; }
    public ulong AccountId { get; set; }

    /// <summary>Null для операций без привязки к инструменту (напр. произвольная комиссия по счёту).</summary>
    public ulong? InstrumentId { get; set; }

    public OperationType Type { get; set; }
    public DateTime Date { get; set; }

    /// <summary>Сумма в рублях; знак отражает направление денежного потока (покупка — минус,
    /// поступление — плюс). Источник истины — T-Invest: брокер отдаёт <c>Payment</c> уже со
    /// знаком потока (см. Bonds.Infrastructure/Connectors/TInvest/README.md), синк копирует это
    /// значение без модификации. <see cref="Bonds.Core.Analytics.PortfolioXirrService"/> (этап 06)
    /// использует это поле как есть для XIRR, не переписывая знак по <see cref="Type"/> (пересмотрено
    /// при ревью этапов 04-06 — раньше было два независимых источника знака, что было риском
    /// расхождения; теперь единственный источник истины — это поле).</summary>
    public decimal AmountRub { get; set; }

    public decimal? Quantity { get; set; }

    /// <summary>Идентификатор операции у брокера (T-Invest) — ключ идемпотентности синка.</summary>
    public string ExternalId { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}

public enum OperationType
{
    Buy,
    Sell,
    Coupon,
    Amortization,
    Redemption,
    Tax,
    Fee,
}
