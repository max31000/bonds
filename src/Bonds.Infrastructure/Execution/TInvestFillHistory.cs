using Bonds.Infrastructure.Connectors.TInvest;
using Bonds.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Bonds.Infrastructure.Execution;

/// <summary>Исполненная сделка по журналу брокера: <see cref="Pieces"/> — ШТУКИ, <see cref="PricePerPiece"/> — валюта платежа за штуку (null — не отдана).</summary>
public sealed record BrokerFill(string? Figi, string? InstrumentUid, string Side, decimal Pieces, decimal? PricePerPiece, DateTime AtUtc);

/// <summary>
/// Задача 67 (#17): журнал исполненных сделок T-Invest по счёту (<c>GetOperationsByCursor</c>) — для сверки заявки, которую брокер «не знает» (NotFound по
/// <c>GetOrderState</c>): перед закрытием как <c>unknown_state</c> исполнитель восстанавливает <c>lots_filled</c> по сделкам. Читается КЛИЕНТОМ НА ЧТЕНИЕ
/// (<c>tinvest_read</c> пользователя плана), не торговым токеном.
/// </summary>
public interface ITInvestFillHistory
{
    /// <summary>Покупки/продажи (исполненные) счёта <paramref name="brokerAccountId"/> с <paramref name="fromUtc"/>. Сбой — исключение (вызывающий решает).</summary>
    Task<IReadOnlyList<BrokerFill>> GetFillsAsync(ulong userId, string brokerAccountId, DateTime fromUtc, CancellationToken ct);
}

/// <summary>
/// Реализация через <see cref="ITInvestPortfolioClient.GetOperationsAsync"/> в ОТДЕЛЬНОМ DI-скоупе, привязанном к пользователю плана
/// (<see cref="ITInvestTokenProvider.UseUser"/>, задача 67 #5): клиент строится по его <c>tinvest_read</c>.
/// </summary>
public sealed class TInvestFillHistory : ITInvestFillHistory
{
    private static readonly HashSet<string> Buys = new(StringComparer.Ordinal) { "Buy", "BuyCard", "BuyMargin" };
    private static readonly HashSet<string> Sells = new(StringComparer.Ordinal) { "Sell", "SellCard", "SellMargin" };

    private readonly IServiceScopeFactory _scopes;

    public TInvestFillHistory(IServiceScopeFactory scopes) => _scopes = scopes;

    public async Task<IReadOnlyList<BrokerFill>> GetFillsAsync(ulong userId, string brokerAccountId, DateTime fromUtc, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITInvestTokenProvider>().UseUser(userId);
        var client = scope.ServiceProvider.GetRequiredService<ITInvestPortfolioClient>();
        var operations = await client.GetOperationsAsync(brokerAccountId, fromUtc, ct);
        return operations
            .Where(o => o.Quantity is > 0m && (Buys.Contains(o.OperationType) || Sells.Contains(o.OperationType)))
            .Select(o => new BrokerFill(
                o.Figi, o.InstrumentUid, Buys.Contains(o.OperationType) ? Bonds.Core.Policy.PlanSides.Buy : Bonds.Core.Policy.PlanSides.Sell,
                o.Quantity!.Value, o.UnitPrice, DateTime.SpecifyKind(o.Date, DateTimeKind.Utc)))
            .ToList();
    }
}
