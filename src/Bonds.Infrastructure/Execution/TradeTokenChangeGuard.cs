using Bonds.Core.Execution;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Core.Policy;
using Microsoft.Extensions.Logging;

namespace Bonds.Infrastructure.Execution;

/// <summary>Состояние заявки от брокера → поля <see cref="TradeOrder"/> (общее для исполнителя и <see cref="TradeTokenChangeGuard"/>).</summary>
public static class OrderStateMapping
{
    /// <summary>
    /// Применяет состояние брокера. Средняя цена ШТУКИ — <see cref="FillPrice.PerPiece"/>: есть итоговая сумма исполненного (<c>OrderState</c>) — сумма / (лоты ×
    /// <paramref name="lotSize"/>), иначе цена штуки от брокера (ответ на отправку заявки).
    /// </summary>
    public static void Apply(TradeOrder order, GatewayOrderState state, decimal lotSize, DateTime nowUtc)
    {
        if (!string.IsNullOrEmpty(state.BrokerOrderId)) order.BrokerOrderId = state.BrokerOrderId;
        order.Status = state.Status;
        order.LotsFilled = Math.Max(order.LotsFilled, state.LotsExecuted);
        if (FillPrice.PerPiece(state.ExecutedAmount, state.LotsExecuted, lotSize, state.AvgFillPrice) is { } avg) order.AvgFillPrice = avg;
        if (state.ExecutedCommission is { } fee) order.ExecutedCommission = fee;
        if (state.Status == TradeOrderStatuses.Rejected)
        {
            order.ErrorCode = TradeOrderErrorCodes.BrokerRejected;
            order.ErrorMessage = state.Message ?? "Брокер отклонил заявку";
        }

        order.UpdatedAt = nowUtc;
    }
}

/// <summary>Итог <see cref="TradeTokenChangeGuard.RunAsync"/>: <see cref="Ok"/> — смена выполнена; иначе 409 с <see cref="Error"/>.</summary>
public sealed record TokenChangeOutcome(bool Ok, string? Error, int ActiveOrders)
{
    public static readonly TokenChangeOutcome Done = new(true, null, 0);
}

/// <summary>
/// Удаление/замена торгового токена при живых заявках (ревью задачи 65, M1). Без этого исполнитель терял возможность снять висящие у брокера заявки (новый
/// токен — другой счёт, или токена нет вовсе).
/// <list type="bullet">
/// <item>Всё — под блокировкой исполнителя (<see cref="IPlanExecutorLock"/>, ожидание до 15 с): тик не идёт параллельно со сменой.</item>
/// <item>Есть активные заявки (<c>New</c>/<c>Submitted</c>/<c>PartiallyFilled</c>) и <c>cancelActive = false</c> — 409, ничего не меняется.</item>
/// <item><c>cancelActive = true</c> — СНАЧАЛА синхронно снимает каждую заявку ТЕКУЩИМ (ещё не заменённым) токеном через шлюз и подтверждает снятие
/// <c>GetOrderState</c>; планы этих заявок получают <c>cancel_requested</c> (исполнитель закроет остальные строки). Хоть одна не снята — 409, токен не меняется.</item>
/// </list>
/// Задача 66: то же для торгового ключа Freedom (<c>freedom_trade</c>) — параметр <c>broker</c>; учитываются и снимаются ТОЛЬКО заявки этого брокера
/// (<c>trade_orders.broker</c>), снятие — через его шлюз (<see cref="IExecutionVenue"/>).
/// Scoped.
/// </summary>
public sealed class TradeTokenChangeGuard
{
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(15);

    private readonly ITradeOrderRepository _orders;
    private readonly ITradePlanRepository _plans;
    private readonly IAccountRepository _accounts;
    private readonly IPlanExecutorLock _lock;
    private readonly ILogger<TradeTokenChangeGuard> _logger;
    private readonly Dictionary<string, IExecutionVenue> _venues = new(StringComparer.OrdinalIgnoreCase);

    public TradeTokenChangeGuard(
        ITradeOrderRepository orders, ITradePlanRepository plans, IAccountRepository accounts, ITInvestOrderGateway gateway, IPlanExecutorLock executorLock,
        ILogger<TradeTokenChangeGuard> logger, IExecutionGateResolver gate, IUserCredentialRepository credentialRecords,
        IFreedomOrderGateway? freedomGateway = null)
    {
        _orders = orders;
        _plans = plans;
        _accounts = accounts;
        _lock = executorLock;
        _logger = logger;

        // Снятие заявок воротами не ограничено: сессия брокера здесь — только счёт (OpenForManagementAsync), ворота и статусы секретов не трогаются.
        _venues[Brokers.TInvest] = new TInvestExecutionVenue(gateway, gate, accounts, credentialRecords, logger);
        if (freedomGateway is not null) _venues[Brokers.Freedom] = new FreedomExecutionVenue(freedomGateway, gate, accounts, credentialRecords, orders, logger);
    }

    /// <summary>Выполняет <paramref name="change"/> (удаление/сохранение токена), только если активных заявок нет или все сняты (<paramref name="cancelActive"/>).</summary>
    /// <param name="broker">Брокер ключа: <c>tinvest</c> (по умолчанию, <c>tinvest_trade</c>) или <c>freedom</c> (<c>freedom_trade</c>).</param>
    public async Task<TokenChangeOutcome> RunAsync(
        ulong userId, bool cancelActive, Func<Task> change, DateTime nowUtc, CancellationToken ct = default, string broker = Brokers.TInvest)
    {
        await using var held = await _lock.TryAcquireAsync(LockWait, ct);
        if (held is null) return new TokenChangeOutcome(false, "Исполнитель сейчас занят — повторите через минуту.", 0);

        var active = await _orders.ListActiveByUserAsync(userId, broker);
        if (active.Count > 0)
        {
            if (!cancelActive)
            {
                return new TokenChangeOutcome(
                    false,
                    $"Есть активные заявки у брокера ({active.Count}): без торгового токена их нельзя будет снять. Снимите их (повторите с cancelActive=true или в терминале брокера) и повторите.",
                    active.Count);
            }

            var errors = await StopAllAsync(userId, broker, active, nowUtc, ct);
            if (errors.Count > 0)
                return new TokenChangeOutcome(false, "Не все заявки удалось снять — токен не изменён: " + string.Join("; ", errors), errors.Count);
        }

        await change();
        return TokenChangeOutcome.Done;
    }

    private async Task<List<string>> StopAllAsync(ulong userId, string broker, IReadOnlyList<TradeOrder> active, DateTime nowUtc, CancellationToken ct)
    {
        var errors = new List<string>();
        if (!_venues.TryGetValue(broker, out var venue))
        {
            errors.Add($"брокер {broker} не поддерживается исполнителем — снимите заявки в терминале брокера");
            return errors;
        }

        var session = await venue.OpenForManagementAsync(userId);
        if (!session.CanManageOrders)
        {
            errors.Add(broker == Brokers.TInvest
                ? "основной счёт T-Invest не привязан — снимите заявки в терминале брокера"
                : $"счёт {venue.Title} не привязан — снимите заявки в терминале брокера");
            return errors;
        }

        var plans = new Dictionary<ulong, TradePlan?>();
        foreach (var order in active)
        {
            if (!plans.TryGetValue(order.PlanId, out var plan))
            {
                plan = await _plans.GetAsync(userId, order.PlanId);
                plans[order.PlanId] = plan;
                await _plans.RequestCancelAsync(userId, order.PlanId); // остальные строки плана исполнитель закроет на ближайшем тике
            }

            var line = plan?.Lines.FirstOrDefault(l => l.LineNo == order.LineNo);
            var label = line?.Ticker ?? $"план #{order.PlanId}, строка {order.LineNo}";
            if (order.Status == TradeOrderStatuses.New && string.IsNullOrEmpty(order.BrokerOrderId) && broker != Brokers.TInvest)
            {
                // Freedom: ответа на размещение не было — сначала узнать id брокера по user_order_id (снять можно только по нему).
                try
                {
                    if (await venue.StateAsync(session, userId, order, true, ct) is { } found) OrderStateMapping.Apply(order, found, line?.LotSize ?? 1m, nowUtc);
                }
                catch (ExecutionGatewayException ex)
                {
                    errors.Add($"{label}: {venue.Title} недоступен ({ex.Code})");
                    continue;
                }
            }

            try
            {
                if (broker == Brokers.TInvest || !string.IsNullOrEmpty(order.BrokerOrderId)) await venue.CancelAsync(session, userId, order, ct);
            }
            catch (ExecutionGatewayException ex) when (ex.Kind == GatewayErrorKind.Transient)
            {
                errors.Add($"{label}: {venue.Title} недоступен ({ex.Code})");
                continue;
            }
            catch (ExecutionGatewayException ex)
            {
                // Отказ снять (уже исполнена/снята) — истину покажет сверка ниже.
                _logger.LogInformation("Token change: cancel of {ClientOrderId} refused ({Code})", order.ClientOrderId, ex.Code);
            }

            GatewayOrderState? state;
            try
            {
                state = await venue.StateAsync(session, userId, order, false, ct);
            }
            catch (ExecutionGatewayException ex)
            {
                errors.Add($"{label}: состояние заявки недоступно ({ex.Code})");
                continue;
            }

            if (state is null)
            {
                if (order.Status != TradeOrderStatuses.New)
                {
                    errors.Add($"{label}: брокер не нашёл заявку — проверьте терминал");
                    continue;
                }

                // Записана, но до брокера не дошла (сбой при отправке): повторно не отправлялась и снимать нечего.
                order.Status = TradeOrderStatuses.Failed;
                order.ErrorCode = TradeOrderErrorCodes.UnknownState;
                order.ErrorMessage = "Состояние заявки не подтвердилось у брокера при смене торгового токена — повторно не отправлялась, проверьте терминал";
                order.UpdatedAt = nowUtc;
                await _orders.UpdateAsync(order);
                continue;
            }

            OrderStateMapping.Apply(order, state, line?.LotSize ?? 1m, nowUtc);
            if (TradeOrderStatuses.IsActive(order.Status))
            {
                await _orders.UpdateAsync(order);
                errors.Add($"{label}: брокер не подтвердил снятие");
                continue;
            }

            if (order.Status == TradeOrderStatuses.Cancelled)
            {
                order.ErrorCode = TradeOrderErrorCodes.TokenChange;
                order.ErrorMessage = "Снята перед сменой торгового токена";
            }

            await _orders.UpdateAsync(order);
        }

        return errors;
    }
}
