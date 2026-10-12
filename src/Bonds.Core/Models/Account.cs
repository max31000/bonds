namespace Bonds.Core.Models;

/// <summary>
/// Брокерский счёт — агрегат портфеля. MVP — один пользователь, один счёт (spec §2),
/// но модель несёт <see cref="UserId"/> как точку расширения под мультисчёт (plan/00 §11),
/// не реализуя саму мультисчётную логику.
/// </summary>
public class Account
{
    public ulong Id { get; set; }
    public ulong UserId { get; set; }

    /// <summary>Идентификатор счёта в T-Invest (account_id из брокерского API).</summary>
    public string? BrokerAccountId { get; set; }

    public string Name { get; set; } = "Основной счёт";

    /// <summary>
    /// Свободные деньги на счёте по последнему синку (T-Invest <c>TotalAmountCurrencies</c>, РУБЛИ).
    /// Сюда приходят погашения/купоны/амортизации. Null — баланс ещё ни разу не синкался.
    /// </summary>
    public decimal? CashRub { get; set; }

    /// <summary>Полная стоимость счёта по оценке брокера (<c>TotalAmountPortfolio</c>, РУБЛИ) — для сверки.</summary>
    public decimal? BrokerTotalRub { get; set; }

    /// <summary>Момент (UTC) последнего успешного обновления <see cref="CashRub"/>/<see cref="BrokerTotalRub"/>.</summary>
    public DateTime? BalanceSyncedAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
