namespace Bonds.Core.Time;

/// <summary>
/// Единый источник «бизнес-сегодня» (T-5/M-3): московская дата (MSK = UTC+3). Все расчётные пути
/// (горизонты метрик, денежный календарь, дни-до-даты) должны брать дату отсюда, а не из
/// <c>DateTime.Today</c> (локаль сервера) или <c>DateOnly.FromDateTime(DateTime.UtcNow)</c> —
/// иначе у границы суток «сегодня» в разных частях системы расходится на день. Планировщик
/// (<c>SyncSchedulerHostedService</c>) уже считает окна в MSK — здесь та же точка отсчёта.
/// Технические метки (CreatedAt/UpdatedAt/JWT exp) остаются на UtcNow — там это уместно.
/// </summary>
public static class BusinessClock
{
    private static readonly TimeZoneInfo MoscowTimeZone = ResolveMoscowTimeZone();

    /// <summary>Текущая московская бизнес-дата.</summary>
    public static DateOnly MoscowToday() => MoscowDate(DateTime.UtcNow);

    /// <summary>Чистое преобразование UTC-момента в московскую дату (тестируемо без часов).</summary>
    public static DateOnly MoscowDate(DateTime utcNow)
    {
        // Контракт: на входе момент по UTC. SpecifyKind помечает Kind=Utc (без сдвига времени),
        // чтобы ConvertTimeFromUtc не бросал для Unspecified/Local и трактовал момент как UTC.
        var utc = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, MoscowTimeZone));
    }

    private static TimeZoneInfo ResolveMoscowTimeZone()
    {
        // IANA id на Linux/macOS, Windows-id как фолбэк — как в SyncSchedulerHostedService.
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Russian Standard Time");
        }
    }
}
