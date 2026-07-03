using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bonds.Infrastructure.Scheduling;

/// <summary>
/// Автосинк в окна торгов MOEX (plan/07 Часть B, spec §3/§11). <see cref="BackgroundService"/>
/// (singleton) — тот же паттерн, что cashpulse <c>ExchangeRateRefreshService</c> (plan/00 "Эталон
/// фоновых задач"): периодический опрос с try/catch вокруг каждого тика, без падения процесса.
/// Не резолвит scoped-зависимости само — делегирует выполнение цикла <see cref="ISyncCycleRunner"/>
/// (singleton), который сам открывает scope внутри <c>RunCycleAsync</c>.
/// <para>
/// <b>Таймзона.</b> Время сервера НЕ доверяется (<see cref="TimeZoneInfo.Local"/> может быть UTC
/// в контейнере) — окна синка заданы в Europe/Moscow (plan/07 явное требование), конвертация
/// явная через <see cref="TimeZoneInfo.ConvertTimeFromUtc"/> с UTC как известной точкой отсчёта.
/// Резолв TimeZoneInfo через <see cref="ResolveMoscowTimeZone"/> с фолбэком на Windows-ID — на
/// Linux/macOS (среда тестов/деплоя) "Europe/Moscow" резолвится из IANA tzdata, второй ID нужен
/// только для гипотетического запуска на Windows без IANA-данных.
/// </para>
/// <para>
/// <b>Не повторяет запуск в то же окно в тот же день.</b> Отслеживается через простую внутреннюю
/// пару (Дата, Окно) — "последнее окно, в которое уже синканулись сегодня" (plan/07 предлагает
/// этот вариант как самый простой). Не использует <see cref="ISyncCycleRunner.GetStatus"/> для
/// этой цели, т.к. force-refresh (этап 08, через тот же <see cref="ISyncCycleRunner"/>) тоже
/// обновляет <c>LastRunStartedAtUtc</c> — смешивать источники привело бы к тому, что ручной
/// force-refresh посреди дня "съедал" бы следующее плановое окно.
/// </para>
/// </summary>
public sealed class SyncSchedulerHostedService : BackgroundService
{
    private readonly ISyncCycleRunner _runner;
    private readonly SchedulerOptions _options;
    private readonly ITelegramAlertSender _alertSender;
    private readonly SyncAlertThrottle _alertThrottle;
    private readonly ILogger<SyncSchedulerHostedService> _logger;
    private readonly TimeZoneInfo _moscowTimeZone;

    private DateOnly? _lastTriggeredDate;
    private TimeOnly? _lastTriggeredWindow;

    public SyncSchedulerHostedService(
        ISyncCycleRunner runner,
        IOptions<SchedulerOptions> options,
        ITelegramAlertSender alertSender,
        SyncAlertThrottle alertThrottle,
        ILogger<SyncSchedulerHostedService> logger)
    {
        _runner = runner;
        _options = options.Value;
        _alertSender = alertSender;
        _alertThrottle = alertThrottle;
        _logger = logger;
        _moscowTimeZone = ResolveMoscowTimeZone();
    }

    private static TimeZoneInfo ResolveMoscowTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");
        }
        catch (TimeZoneNotFoundException)
        {
            // Windows-фолбэк (plan/07) — IANA ID недоступен только в редких Windows-окружениях
            // без обновлённых данных часовых зон; деплой — Linux (plan/00 §5), сюда дойдёт редко.
            return TimeZoneInfo.FindSystemTimeZoneById("Russian Standard Time");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Устойчивость (plan/07 "не падать"): ошибка в самой логике планирования
                // (а не в синке — тот уже устойчив внутри SyncCycleService) не должна останавливать
                // BackgroundService навсегда.
                _logger.LogError(ex, "Unexpected error in sync scheduler tick");
            }

            try
            {
                await Task.Delay(_options.PollingInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var nowMsk = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _moscowTimeZone);
        var today = DateOnly.FromDateTime(nowMsk);
        var timeOfDay = TimeOnly.FromDateTime(nowMsk);

        // Минимум по спеке — пропуск сб/вс (plan/07: праздники MOEX опциональны, идемпотентность
        // нижних слоёв уже не даёт пустому/повторному синку испортить данные).
        if (nowMsk.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return;
        }

        foreach (var window in _options.DailySyncTimesMsk)
        {
            if (timeOfDay < window) continue; // окно ещё не наступило
            if (_lastTriggeredDate == today && _lastTriggeredWindow == window) continue; // уже синканулись в это окно сегодня

            _logger.LogInformation("Sync window {Window} MSK reached — triggering sync cycle", window);
            _lastTriggeredDate = today;
            _lastTriggeredWindow = window;

            var result = await _runner.RunCycleAsync(ct);
            await MaybeAlertOnFailureAsync(result, today, ct);
        }
    }

    /// <summary>
    /// Plan/13 часть D: только для ПЛАНОВОГО (не ручного force-refresh — этот метод вызывается
    /// исключительно из <see cref="TickAsync"/>) цикла — если он завершился с ошибками или
    /// протухшим/отсутствующим токеном, шлём Telegram-алерт владельцу, не чаще одного раза в
    /// сутки на уникальный набор ошибок (<see cref="SyncAlertThrottle"/>). Сбой отправки не
    /// бросается наружу — <see cref="ITelegramAlertSender.SendAsync"/> сам это гарантирует.
    /// </summary>
    private async Task MaybeAlertOnFailureAsync(SyncCycleResult result, DateOnly today, CancellationToken ct)
    {
        if (!result.HasErrors && !result.TokenMissingOrInvalid)
        {
            return;
        }

        var errors = result.Errors.Count > 0
            ? result.Errors
            : ["Токен T-Invest не подключён или недействителен."];

        if (!_alertThrottle.ShouldAlert(errors, today))
        {
            return;
        }

        // Текст без токена/чувствительных данных (spec §11) — первая ошибка уже была составлена
        // выше по цепочке (SyncCycleService) как человекочитаемая строка без секретов.
        var message = $"Bonds: автосинк упал: {errors[0]}";
        await _alertSender.SendAsync(message, ct);
        _alertThrottle.MarkAlerted(errors, today);
    }
}
