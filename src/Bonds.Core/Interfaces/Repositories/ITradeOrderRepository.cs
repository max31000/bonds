using Bonds.Core.Execution;

namespace Bonds.Core.Interfaces.Repositories;

/// <summary>
/// Заявки исполнителя (<c>trade_orders</c>, задача 65). Строка пишется ДО отправки брокеру (идемпотентность по UNIQUE <c>client_order_id</c>) и обновляется
/// по ответу/сверке. Брокера репозиторий не знает. Даты — UTC.
/// </summary>
public interface ITradeOrderRepository
{
    /// <summary>Вставляет заявку; повтор того же <c>client_order_id</c> или той же попытки строки — исключение уникальности (двойной отправки не будет).</summary>
    Task<ulong> InsertAsync(TradeOrder order);

    /// <summary>Обновляет изменяемые поля (статус, id брокера, исполнение, ошибка, <c>updated_at</c>).</summary>
    Task UpdateAsync(TradeOrder order);

    Task<IReadOnlyList<TradeOrder>> ListByPlanAsync(ulong planId);

    Task<IReadOnlyList<TradeOrder>> ListByPlansAsync(IReadOnlyCollection<ulong> planIds);

    /// <summary>Активные заявки пользователя (<c>New</c>/<c>Submitted</c>/<c>PartiallyFilled</c>) по всем его планам — могут висеть у брокера.</summary>
    Task<IReadOnlyList<TradeOrder>> ListActiveByUserAsync(ulong userId);

    /// <summary>То же, только заявки брокера <paramref name="broker"/> (<c>trade_orders.broker</c>, задача 66) — для смены торгового ключа этого брокера.</summary>
    Task<IReadOnlyList<TradeOrder>> ListActiveByUserAsync(ulong userId, string broker);

    /// <summary>
    /// id заявки брокера <paramref name="brokerOrderId"/> уже привязан к строке <c>trade_orders</c> этого брокера, кроме <paramref name="exceptOrderId"/> (задача 66: сверка заявки
    /// Freedom без id не должна «присвоить» заявку другой строки).
    /// </summary>
    Task<bool> IsBrokerOrderLinkedAsync(string broker, string brokerOrderId, ulong exceptOrderId);
}
