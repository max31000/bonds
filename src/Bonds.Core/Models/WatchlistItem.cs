namespace Bonds.Core.Models;

/// <summary>
/// Ручной watchlist пользователя — конкретный ISIN, отслеживаемый вне текущих
/// позиций портфеля (не скринер по всей вселенной). Хранит только факт "пользователь следит за
/// этим ISIN" + заметку; справочные/расчётные данные бумаги живут в общем справочнике
/// <see cref="Instrument"/> (заводится тем же путём, что и позиции — см. doc-comment BondSyncService).
/// </summary>
public class WatchlistItem
{
    public ulong Id { get; set; }
    public ulong UserId { get; set; }

    public string Isin { get; set; } = string.Empty;

    public DateTime AddedAtUtc { get; set; }

    public string? Note { get; set; }
}
