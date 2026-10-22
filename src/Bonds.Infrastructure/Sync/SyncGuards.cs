using System.Collections.Concurrent;

namespace Bonds.Infrastructure.Sync;

/// <summary>
/// Общие защитные приёмы синка отдельных доменов (фонды — задача 51, валюта — задача 55): вынесены, чтобы не копировать логику.
/// Поведение фондов при выносе не менялось (закреплено их тестами).
/// </summary>
internal static class SyncGuards
{
    /// <summary>
    /// Защита от пустого ответа брокера при сверке позиций. Пустой список от брокера при непустых локальных позициях — подозрительно
    /// (деградация API, урезанный токен): удалять всё по одному ответу нельзя. Первый такой ответ запоминается в <paramref name="seen"/>
    /// (отдельный словарь на домен) и удаление откладывается (возврат true); второй подряд — удаление разрешено. Любой непустой
    /// ответ (или небезопасная сверка) сбрасывает отметку. <paramref name="onDeferred"/> получает число локальных позиций.
    /// </summary>
    /// <returns>true — удаление нужно отложить до следующего синка.</returns>
    public static async Task<bool> ShouldDeferEmptyReconciliationAsync(
        ConcurrentDictionary<ulong, byte> seen,
        ulong accountId,
        bool reconciliationSafe,
        int brokerCount,
        Func<Task<int>> getLocalCount,
        Action<int> onDeferred)
    {
        if (reconciliationSafe && brokerCount == 0)
        {
            var localCount = await getLocalCount();
            if (localCount > 0 && !seen.ContainsKey(accountId))
            {
                seen[accountId] = 0;
                onDeferred(localCount);
                return true;
            }

            return false;
        }

        seen.TryRemove(accountId, out _);
        return false;
    }

    /// <summary>
    /// Ворота суточного рефреша рыночных данных: пропустить, если последняя сохранённая дата не старее последней завершённой сессии
    /// (<paramref name="expected"/>), либо если по активу в БД так и нет данных (<paramref name="lastDate"/> = null), но сегодня уже
    /// пробовали (<paramref name="emptyAttemptedOn"/>) — «пустые» активы дёргают источник не чаще раза в календарный день.
    /// </summary>
    public static bool ShouldSkipMarketRefresh(
        DateOnly? lastDate, DateOnly expected, ConcurrentDictionary<ulong, DateOnly> emptyAttemptedOn, ulong assetId, DateOnly today)
    {
        if (lastDate is { } last && last >= expected) return true;
        return lastDate is null && emptyAttemptedOn.TryGetValue(assetId, out var attempted) && attempted >= today;
    }

    /// <summary>Итог попытки рефреша: данных в БД не было и не появилось — запомнить дату попытки; иначе сбросить отметку.</summary>
    public static void RecordMarketRefreshOutcome(
        DateOnly? lastDate, bool gotNewPoints, ConcurrentDictionary<ulong, DateOnly> emptyAttemptedOn, ulong assetId, DateOnly today)
    {
        if (lastDate is null && !gotNewPoints) emptyAttemptedOn[assetId] = today;
        else emptyAttemptedOn.TryRemove(assetId, out _);
    }
}
