using System.Globalization;
using Bonds.Core.Capital;

namespace Bonds.Core.Policy;

/// <summary>
/// Задача 67 (аудит #7): неполные или устаревшие данные останавливают правила, которые решают по отклонениям корзин. Чистые функции от <see cref="PolicyInput"/>:
/// <list type="bullet">
/// <item><see cref="Problem"/> — <see cref="PolicyInput.StaleSince"/> (последний синк брокера старше <see cref="PolicyOptions.MaxSyncAge"/>) или
/// <see cref="PolicyInput.DataIncomplete"/>: P3b, P4 и распределение по дефициту (P2 и ручное «распределить сумму») не строятся;</item>
/// <item><see cref="LiquidityProblem"/> — для P1: устаревшие данные либо неполнота именно корзины <c>liquidity</c> или суммы свободных денег (<c>cash</c>).
/// Неполнота ДРУГОЙ корзины P1 не останавливает: недооценённая позиция занижает базу доли, вес ликвидности выходит выше — P1 паркует МЕНЬШЕ (безопасная сторона).</item>
/// </list>
/// Причина возвращается текстом — правило кладёт её в заметки (лог прогона и предпросмотр).
/// </summary>
public static class PolicyDataGate
{
    public static string? Problem(PolicyInput input) => StaleText(input) ?? (input.DataIncomplete ? IncompleteText(input) : null);

    public static string? LiquidityProblem(PolicyInput input)
    {
        if (StaleText(input) is { } stale) return stale;
        foreach (var code in new[] { AutoWhitelist.BasketCode, SystemBaskets.Cash })
        {
            if (input.IncompleteBaskets.Contains(code))
                return code == SystemBaskets.Cash
                    ? "данные неполные: сумма свободных денег не определена (нет курса валюты)"
                    : $"данные неполные: по корзине «{code}» нет цены или курса";
        }

        return null;
    }

    private static string? StaleText(PolicyInput input) => input.StaleSince switch
    {
        null => null,
        { } at when at == DateTime.MinValue => "данные брокера устарели: успешного синка ещё не было",
        { } at => "данные брокера устарели: последний успешный синк " +
                  TradingHours.ToMoscow(at).ToString("dd.MM HH:mm", CultureInfo.InvariantCulture) + " МСК",
    };

    private static string IncompleteText(PolicyInput input) =>
        input.IncompleteBaskets.Count == 0
            ? "данные неполные: по части позиций нет цены или курса"
            : "данные неполные: нет цены или курса (" + string.Join(", ", input.IncompleteBaskets.OrderBy(c => c, StringComparer.Ordinal)) + ")";
}
