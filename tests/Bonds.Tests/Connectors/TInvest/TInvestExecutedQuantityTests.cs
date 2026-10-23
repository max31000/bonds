using Bonds.Infrastructure.Connectors.TInvest;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Connectors.TInvest;

/// <summary>
/// Количество операции = ИСПОЛНЕННОЕ, не заявленное в заявке. Регрессия на журнал владельца (2026-10): заявка на продажу 16 ОФЗ
/// исполнилась на 3 шт. (платёж 3 020 ₽), а в журнал ушло 16 — пул журнала обнулялся раньше погашения, погашение 8 000 ₽ выпадало
/// из итога как «неизвестная себестоимость», а реализованный убыток задваивался.
/// </summary>
public class TInvestExecutedQuantityTests
{
    [Fact]
    public void PartialFill_UsesQuantityDone()
        => TInvestPortfolioClient.ExecutedQuantity(quantity: 16, quantityRest: 13, quantityDone: 3).Should().Be(3m);

    [Fact]
    public void FullFill_QuantityDoneEqualsQuantity()
        => TInvestPortfolioClient.ExecutedQuantity(quantity: 7, quantityRest: 0, quantityDone: 7).Should().Be(7m);

    [Fact]
    public void QuantityDoneMissing_FallsBackToQuantityMinusRest()
        => TInvestPortfolioClient.ExecutedQuantity(quantity: 2, quantityRest: 1, quantityDone: 0).Should().Be(1m);

    [Fact]
    public void NoExecutionDetails_FallsBackToQuantity()
        => TInvestPortfolioClient.ExecutedQuantity(quantity: 5, quantityRest: 0, quantityDone: 0).Should().Be(5m);

    [Fact]
    public void NoQuantity_ReturnsNull()
        => TInvestPortfolioClient.ExecutedQuantity(quantity: 0, quantityRest: 0, quantityDone: 0).Should().BeNull();
}
