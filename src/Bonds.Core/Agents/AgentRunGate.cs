namespace Bonds.Core.Agents;

/// <summary>Состояние пользователя для решения «можно ли запустить агента сейчас».</summary>
public sealed record AgentGateInput(
    bool ServerEnabled,
    bool CapabilityOk,
    AgentSettings Settings,
    int RunsToday,
    int EventReviewsToday,
    AgentTaskKind Kind,
    int EventReviewMaxPerDay);

public sealed record AgentGateDecision(bool Allowed, string? Reason)
{
    public static AgentGateDecision Allow { get; } = new(true, null);
}

/// <summary>
/// Ворота запуска агента (задача 68), чистая функция. Запуск — только если ВСЁ: агенты не выключены на сервере, у пользователя проверенное подключение ИИ
/// (<c>capabilities.aiAgents</c>), агент включён в настройках (<c>agent_enabled</c>, по умолчанию выкл.), не исчерпан суточный лимит, а разбор событий — не чаще
/// <see cref="AgentOptions.EventReviewMaxPerDay"/> в сутки. Сутки — МСК.
/// </summary>
public static class AgentRunGate
{
    public const string ServerDisabled = "ИИ-агенты отключены на сервере";
    public const string NoCapability = "Подключите Claude Code: Настройки → Подключения";
    public const string NotEnabled = "Агент выключен в настройках";

    public static AgentGateDecision Decide(AgentGateInput input)
    {
        if (!input.ServerEnabled) return new(false, ServerDisabled);
        if (!input.CapabilityOk) return new(false, NoCapability);
        if (!input.Settings.Enabled) return new(false, NotEnabled);
        if (input.RunsToday >= input.Settings.DailyRunLimit)
            return new(false, $"Лимит запусков агента на сегодня исчерпан ({input.Settings.DailyRunLimit})");
        if (input.Kind == AgentTaskKind.EventReview && input.EventReviewsToday >= input.EventReviewMaxPerDay)
            return new(false, $"Разборов событий сегодня уже {input.EventReviewMaxPerDay} — следующий завтра");
        return AgentGateDecision.Allow;
    }

    private static readonly TimeSpan MoscowOffset = TimeSpan.FromHours(3);

    /// <summary>Начало текущих суток МСК в UTC (МСК = UTC+3 без перехода на летнее время).</summary>
    public static DateTime MoscowDayStartUtc(DateTime nowUtc)
    {
        var msk = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc).Add(MoscowOffset);
        return DateTime.SpecifyKind(msk.Date - MoscowOffset, DateTimeKind.Utc);
    }

    /// <summary>
    /// Пора ли плановой задаче вида <paramref name="kind"/>: дайджест — будни, ежемесячный обзор — 1-е число; оба — в окне
    /// [<see cref="AgentOptions.DigestAtMoscow"/>, + <see cref="AgentOptions.ScheduleWindow"/>) МСК. Повтор в те же сутки отсекает вызывающий по журналу.
    /// Прочие виды по расписанию не запускаются.
    /// </summary>
    public static bool IsScheduledDue(AgentTaskKind kind, DateTime nowUtc, AgentOptions options)
    {
        var msk = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc).Add(MoscowOffset);
        var inWindow = msk.TimeOfDay >= options.DigestAtMoscow && msk.TimeOfDay < options.DigestAtMoscow + options.ScheduleWindow;
        if (!inWindow) return false;
        return kind switch
        {
            AgentTaskKind.DailyDigest => msk.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday),
            AgentTaskKind.MonthlyReview => msk.Day == 1,
            _ => false,
        };
    }
}
