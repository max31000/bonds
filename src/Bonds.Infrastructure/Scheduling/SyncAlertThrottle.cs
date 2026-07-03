namespace Bonds.Infrastructure.Scheduling;

/// <summary>
/// Антиспам-логика для Telegram-алертов об упавшем автосинке (plan/13 часть D) — не чаще одного
/// алерта на уникальный набор ошибок в сутки. Выделена в чистый класс отдельно от
/// <see cref="SyncSchedulerHostedService"/> специально для юнит-теста без часов/HTTP/Telegram
/// (plan/13: "Юнит-тест на антиспам-логику (выделить её в чистый класс)"). Не потокобезопасна
/// специально — вызывается только из последовательного цикла тика <see cref="SyncSchedulerHostedService"/>
/// (один экземпляр, один поток), как и остальное состояние там (_lastTriggeredDate/_lastTriggeredWindow).
/// </summary>
public sealed class SyncAlertThrottle
{
    private string? _lastAlertedErrorsHash;
    private DateOnly? _lastAlertedDate;

    /// <summary>
    /// true, если по этому набору ошибок для этой даты алерт ещё не отправлялся (или отправлялся
    /// для другого набора ошибок / в другой день) — вызывающий код должен вызвать
    /// <see cref="MarkAlerted"/> сразу после фактической отправки, иначе повторный вызов
    /// <see cref="ShouldAlert"/> для того же набора продолжит возвращать true.
    /// </summary>
    public bool ShouldAlert(IReadOnlyList<string> errors, DateOnly today)
    {
        if (errors.Count == 0) return false;

        var hash = HashErrors(errors);
        return _lastAlertedDate != today || _lastAlertedErrorsHash != hash;
    }

    /// <summary>Фиксирует факт отправки — последующие идентичные ошибки в тот же день не пройдут <see cref="ShouldAlert"/>.</summary>
    public void MarkAlerted(IReadOnlyList<string> errors, DateOnly today)
    {
        _lastAlertedErrorsHash = HashErrors(errors);
        _lastAlertedDate = today;
    }

    /// <summary>
    /// "Уникальный набор ошибок" — порядок-независимый (Sort перед join), чтобы одни и те же
    /// ошибки, собранные в разном порядке между шагами цикла, не считались новым набором.
    /// </summary>
    private static string HashErrors(IReadOnlyList<string> errors) =>
        string.Join("|", errors.OrderBy(e => e, StringComparer.Ordinal));
}
