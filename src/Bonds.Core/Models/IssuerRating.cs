namespace Bonds.Core.Models;

/// <summary>
/// Задача 46: кредитный рейтинг эмитента, введённый владельцем вручную (бесплатного источника нет,
/// скрейпинг сайтов агентств отклонён — docs/2026-10-analytics-quality-upgrade.md §6). Ключ —
/// (<see cref="Inn"/>, <see cref="Agency"/>): у каждого агентства одна актуальная запись.
/// <see cref="RatingDate"/> — дата присвоения/подтверждения (рейтинг старше 18 месяцев «протух»).
/// </summary>
public sealed class IssuerRating
{
    public string Inn { get; set; } = string.Empty;
    public string Agency { get; set; } = string.Empty;
    public string Rating { get; set; } = string.Empty;
    public DateOnly RatingDate { get; set; }
    public string? Note { get; set; }
}
