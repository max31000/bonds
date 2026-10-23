using Bonds.Core.Policy;

namespace Bonds.Infrastructure.Policy;

/// <summary>
/// Уведомление пользователя о только что сохранённом плане (задача 64; реализация — Telegram). Вызывается <see cref="PolicyRunner"/> после <c>SaveAsync</c>.
/// Реализация НЕ должна бросать исключений: уведомление «лучше-если-получится», сбой не ломает прогон правил.
/// </summary>
public interface IPlanNotifier
{
    Task NotifyPlanCreatedAsync(TradePlan plan, CancellationToken ct = default);
}

/// <summary>Ничего не делает (бот не настроен / тесты без Telegram).</summary>
public sealed class NullPlanNotifier : IPlanNotifier
{
    public Task NotifyPlanCreatedAsync(TradePlan plan, CancellationToken ct = default) => Task.CompletedTask;
}
