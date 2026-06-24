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

    /// <summary>Сумма в рублях; знак отражает направление денежного потока (покупка — минус и т.п.) —
    /// конвенция знака закрепляется движком XIRR на этапе 06, на этом этапе храним как получено от брокера.</summary>
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
