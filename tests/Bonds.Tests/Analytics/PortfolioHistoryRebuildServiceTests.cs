using Bonds.Core.Analytics;
using Bonds.Core.Models;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Analytics;

/// <summary>
/// Тесты ретроспективного восстановления истории портфеля (plan/15 §B.2): стоимость на контрольных
/// точках (ручной расчёт), восстановление количества при продаже, деградация при отсутствии цены
/// (флаг <see cref="PortfolioHistoryPoint.IsApproximate"/>).
/// </summary>
public class PortfolioHistoryRebuildServiceTests
{
    private const ulong InstrumentId = 10;
    private static readonly DateOnly BaseDate = new(2025, 1, 1);

    private static Operation Op(OperationType type, DateOnly date, decimal amount, decimal? quantity = null, ulong? instrumentId = InstrumentId) => new()
    {
        AccountId = 1,
        InstrumentId = instrumentId,
        Type = type,
        Date = date.ToDateTime(TimeOnly.MinValue),
        AmountRub = amount,
        Quantity = quantity,
        ExternalId = Guid.NewGuid().ToString(),
    };

    private static IReadOnlyDictionary<ulong, IReadOnlyDictionary<DateOnly, decimal>> PriceMap(
        ulong instrumentId, params (DateOnly Date, decimal Price)[] points) =>
        new Dictionary<ulong, IReadOnlyDictionary<DateOnly, decimal>>
        {
            [instrumentId] = points.ToDictionary(p => p.Date, p => p.Price),
        };

    [Fact]
    public void Rebuild_TwoBuysAndCoupon_MatchesHandComputedMarketValueAtCheckpoints()
    {
        // Покупка 10 шт по 950 (цена входа не участвует в расчёте стоимости — только количество),
        // ещё 5 шт через 10 дней, купон на 15-й день (не меняет количество). Цена на бирже постоянна:
        // 1000 руб/шт весь период — упрощает ручной расчёт стоимости на чекпоинтах.
        var operations = new[]
        {
            Op(OperationType.Buy, BaseDate, -9500m, quantity: 10m),
            Op(OperationType.Buy, BaseDate.AddDays(10), -5000m, quantity: 5m),
            Op(OperationType.Coupon, BaseDate.AddDays(15), 200m),
        };

        var prices = PriceMap(InstrumentId, (BaseDate, 1000m));
        var asOf = BaseDate.AddDays(20); // ровно 3 недельных шага (7*2=14 < 20, последняя точка = asOf)

        var points = PortfolioHistoryRebuildService.Rebuild(operations, prices, asOf);

        points.Should().NotBeEmpty();
        points.Last().Date.Should().Be(asOf, "последняя точка ряда — всегда asOf (plan/15 §B.1)");

        // Чекпоинт на BaseDate (день 0): только первая покупка применена → 10 шт × 1000 = 10000.
        var day0 = points.First(p => p.Date == BaseDate);
        day0.MarketValueRub.Should().Be(10000m);

        // Чекпоинт на BaseDate+14 (второй недельный шаг): обе покупки применены (10-й и 15-й день
        // операций уже позади) → (10+5) шт × 1000 = 15000.
        var day14 = points.First(p => p.Date == BaseDate.AddDays(14));
        day14.MarketValueRub.Should().Be(15000m, "обе покупки к этому чекпоинту уже прошли, купон количество не меняет");

        // Последняя точка (asOf = day 20) — то же количество, та же (единственная) цена.
        points.Last().MarketValueRub.Should().Be(15000m);
    }

    [Fact]
    public void Rebuild_SellReducesQuantity_MarketValueDropsAccordingly()
    {
        var operations = new[]
        {
            Op(OperationType.Buy, BaseDate, -10000m, quantity: 10m),
            Op(OperationType.Sell, BaseDate.AddDays(7), 4000m, quantity: 4m), // продали 4 из 10
        };

        var prices = PriceMap(InstrumentId, (BaseDate, 1000m));
        var asOf = BaseDate.AddDays(7);

        var points = PortfolioHistoryRebuildService.Rebuild(operations, prices, asOf);

        // Чекпоинты: BaseDate (день 0, до продажи) и asOf=BaseDate+7 (после продажи).
        points.Should().HaveCount(2);
        points[0].MarketValueRub.Should().Be(10000m, "на день 0 продажа ещё не наступила — 10 шт × 1000");
        points.Last().MarketValueRub.Should().Be(6000m, "10 куплено − 4 продано = 6 шт по 1000");
    }

    [Fact]
    public void Rebuild_ForwardFillsSparsePriceMap_BetweenKnownDates()
    {
        var operations = new[]
        {
            Op(OperationType.Buy, BaseDate, -10000m, quantity: 10m),
        };

        // Цена известна только на BaseDate — на все последующие чекпоинты переносится вперёд.
        var prices = PriceMap(InstrumentId, (BaseDate, 1000m));
        var asOf = BaseDate.AddDays(21); // 3 недельных шага

        var points = PortfolioHistoryRebuildService.Rebuild(operations, prices, asOf);

        points.Should().OnlyContain(p => p.MarketValueRub == 10000m, "цена одна на весь период — forward fill переносит её на все чекпоинты");
        points.Should().OnlyContain(p => !p.IsApproximate, "цена есть (хоть и перенесена вперёд) — это не Approximate");
    }

    [Fact]
    public void Rebuild_NoPriceAtAll_MarksPointApproximateAndExcludesInstrument()
    {
        var operations = new[]
        {
            Op(OperationType.Buy, BaseDate, -10000m, quantity: 10m),
        };

        // Пустая карта цен — для инструмента нет ни одной точки цены.
        var prices = new Dictionary<ulong, IReadOnlyDictionary<DateOnly, decimal>>();
        var asOf = BaseDate.AddDays(7);

        var points = PortfolioHistoryRebuildService.Rebuild(operations, prices, asOf);

        points.Should().OnlyContain(p => p.MarketValueRub == 0m, "бумага без цены исключается из суммы стоимости на всех чекпоинтах");
        points.Should().OnlyContain(p => p.IsApproximate, "нет цены вообще — все точки помечены приближёнными (plan/15 §B.1)");
    }

    [Fact]
    public void Rebuild_ComputesXirrAtEachCheckpoint_UsingOperationsUpToDate()
    {
        var operations = new[]
        {
            Op(OperationType.Buy, BaseDate, -10000m, quantity: 10m),
            Op(OperationType.Coupon, BaseDate.AddDays(180), 500m, instrumentId: null),
        };

        var prices = PriceMap(InstrumentId, (BaseDate, 1100m));
        var asOf = BaseDate.AddDays(365);

        var points = PortfolioHistoryRebuildService.Rebuild(operations, prices, asOf);

        var last = points.Last();
        last.Xirr.Should().NotBeNull("есть и отток (покупка), и притоки (купон + терминальная стоимость) — корень существует");
        last.Xirr!.Value.Should().BeGreaterThan(0m, "куплено за 10000, получен купон и текущая стоимость 11000 — доходность положительная");
    }

    [Fact]
    public void Rebuild_NoOperations_ReturnsEmptySeries()
    {
        var points = PortfolioHistoryRebuildService.Rebuild(
            Array.Empty<Operation>(),
            new Dictionary<ulong, IReadOnlyDictionary<DateOnly, decimal>>(),
            BaseDate);

        points.Should().BeEmpty();
    }
}
