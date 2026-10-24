namespace Bonds.Infrastructure.Connectors.Freedom;

/// <summary>Настройки коннектора (секция конфигурации <c>Freedom</c>). Ключи пользователя сюда не кладутся — они в реестре секретов.</summary>
public sealed class FreedomOptions
{
    public const string SectionName = "Freedom";

    /// <summary>Хост API v3. Счёт Freedom Global / KZ живёт на tradernet.global (SDK по умолчанию — freedom24.com).</summary>
    public string BaseUrl { get; set; } = "https://tradernet.global";

    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Смещение часового пояса меток времени Tradernet БЕЗ пояса от UTC, ЧАСЫ: UTC = метка − смещение. 3 = МСК — ПОДТВЕРЖДЕНО на живом счёте: <c>date</c>/<c>stat_d</c>
    /// и <c>trade[].date</c> в списке заявок, <c>ltt</c> в <c>getStockQuotesJson</c> (BTC/USD: <c>ltt</c> = текущее UTC + 3 ч), время <c>getMarketStatus</c>.
    /// ВНИМАНИЕ: <c>getTradesHistory</c> отдаёт <c>date</c> в другом поясе (на 2 ч позже — UTC+5) — исполнитель время сделок из истории не сравнивает, только связь по
    /// <c>order_id</c>. Неверное смещение безопасно: сверка заявки без номера ищет её в окне [−30 с, +2 мин] от записи <c>trade_orders</c>, промах окна →
    /// <c>unknown_state</c> (без повторной отправки); свежесть котировки — промах в сторону «несвежая».
    /// </summary>
    public double OrderTimeUtcOffsetHours { get; set; } = 3;
}

/// <summary>
/// Пара ключей API v3. Передаётся в КАЖДЫЙ вызов клиента (клиент без состояния, мультипользовательский).
/// <see cref="ToString"/> скрывает значения, чтобы ключи не попали в логи через record-форматирование.
/// </summary>
public sealed record FreedomCredentials(string PublicKey, string PrivateKey)
{
    public override string ToString() => "FreedomCredentials { *** }";
}
