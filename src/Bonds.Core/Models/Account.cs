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

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
