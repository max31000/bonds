using System.Text.Json;

namespace Bonds.Core.Policy;

/// <summary>Действующее удержание под вывод: сумма (валюта портфеля) и число планов; <see cref="Active"/> — хотя бы один план держит деньги.</summary>
public sealed record CashHoldState(decimal Amount, int Plans)
{
    public bool Active => Plans > 0;
}

/// <summary>
/// Удержание денег под вывод (задача 67, аудит #3) — чистые функции. План вывода (P5) или выхода из корзины несёт в итогах <see cref="TradePlanSummary.CashHold"/>
/// (валюта портфеля). Вывод в банк владелец делает ВРУЧНУЮ, поэтому деньги держатся: (1) пока план открыт (Draft/PendingApproval/AutoApproved/Approved — до
/// <c>expires_at</c>; Submitting — пока исполняется) и (2) после исполнения (Done/PartiallyDone) — <see cref="PolicyOptions.WithdrawHoldTtl"/> рабочих дней от
/// <c>expires_at</c> плана (исполнение заканчивается не позже закрытия сессии, а срок живого плана — это закрытие сессии: точка отсчёта консервативна).
/// Владелец снимает удержание раньше (<see cref="TradePlan.CashHoldReleasedAt"/>: <c>/withdrawn</c> или «Деньги выведены»). Отклонённый, просроченный или
/// неисполненный (Failed) план не держит ничего. Рабочие дни — пн–пт по МСК, праздники не учитываются.
/// </summary>
public static class WithdrawHold
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Правила, чьи планы могут держать деньги: P5 и выход из корзины (<see cref="PlanRules.Manual"/>).</summary>
    public static readonly IReadOnlyList<string> Rules = [PlanRules.P5Withdraw, PlanRules.Manual];

    /// <summary>Статусы-кандидаты (дальше решает <see cref="IsActive"/>): открытые и исполненные.</summary>
    public static readonly IReadOnlyList<string> Statuses =
        [PlanStatuses.Draft, PlanStatuses.PendingApproval, PlanStatuses.AutoApproved, PlanStatuses.Approved, PlanStatuses.Submitting, PlanStatuses.Done, PlanStatuses.PartiallyDone];

    /// <summary>Сумма удержания из итогов (валюта портфеля <see cref="TradePlanSummary.Currency"/>); битая/старая сводка — 0.</summary>
    public static decimal AmountOf(TradePlan plan) => SummaryOf(plan)?.CashHold is { } hold && hold > 0m ? hold : 0m;

    public static TradePlanSummary? SummaryOf(TradePlan plan)
    {
        if (string.IsNullOrWhiteSpace(plan.SummaryJson)) return null;
        try
        {
            return JsonSerializer.Deserialize<TradePlanSummary>(plan.SummaryJson, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Удержание плана действует в момент <paramref name="nowUtc"/>.</summary>
    public static bool IsActive(TradePlan plan, DateTime nowUtc, int ttlBusinessDays)
    {
        if (plan.CashHoldReleasedAt is not null || AmountOf(plan) <= 0m) return false;
        return plan.Status switch
        {
            PlanStatuses.Submitting => true,
            PlanStatuses.Draft or PlanStatuses.PendingApproval or PlanStatuses.AutoApproved or PlanStatuses.Approved => plan.ExpiresAt > nowUtc,
            PlanStatuses.Done or PlanStatuses.PartiallyDone => nowUtc < AddBusinessDays(plan.ExpiresAt, ttlBusinessDays),
            _ => false,
        };
    }

    /// <summary>+N рабочих дней (пн–пт по МСК) к моменту UTC; время суток сохраняется. Старт в выходной — считается от него же (суббота + 1 = понедельник).</summary>
    public static DateTime AddBusinessDays(DateTime utc, int days)
    {
        var msk = TradingHours.ToMoscow(utc);
        for (var added = 0; added < days;)
        {
            msk = msk.AddDays(1);
            if (msk.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) added++;
        }

        return DateTime.SpecifyKind(msk.AddHours(-3), DateTimeKind.Utc); // МСК = UTC+3 круглый год (как TradingHours)
    }
}
