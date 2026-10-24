using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Infrastructure.Analytics;
using Bonds.Infrastructure.Sync;
using FluentAssertions;
using Moq;
using Xunit;

namespace Bonds.Tests.Brokers;

/// <summary>Задача 60: оценка брокерских позиций — валюта строки → рубли только через курс, нет курса → null + dataIncomplete.</summary>
public class BrokerHoldingsBuilderTests
{
    private static readonly DateOnly AsOf = new(2026, 10, 23);
    private const ulong Acc = 5;

    private readonly Mock<IBrokerPositionRepository> _positions = new();
    private readonly Mock<IBrokerCashRepository> _cash = new();
    private readonly Mock<IBrokerOperationRepository> _ops = new();
    private readonly Mock<IAccountRepository> _accounts = new();
    private readonly Mock<ICurrencyRateLookup> _rates = new();

    private BrokerHoldingsBuilder Create() => new(_positions.Object, _cash.Object, _ops.Object, _accounts.Object, _rates.Object);

    private void Rate(string iso, decimal? rub) =>
        _rates.Setup(r => r.GetRubPerUnitAsync(iso, It.IsAny<DateOnly>(), It.IsAny<CancellationToken>())).ReturnsAsync(rub);

    private void Holdings(IEnumerable<BrokerPosition> positions, IEnumerable<BrokerCash>? cash = null)
    {
        _positions.Setup(p => p.GetByAccountAsync(Acc)).ReturnsAsync(positions.ToList());
        _cash.Setup(c => c.GetByAccountAsync(Acc)).ReturnsAsync((cash ?? []).ToList());
    }

    private static BrokerPosition Pos(string ticker, string cur, decimal qty, decimal avg, decimal last, decimal mv) =>
        new() { AccountId = Acc, Ticker = ticker, Currency = cur, Quantity = qty, AvgPrice = avg, LastPrice = last, MarketValue = mv, SyncedAt = new DateTime(2026, 10, 23, 10, 0, 0) };

    [Fact]
    public async Task Build_ValuesUsdEurGbp_InRubles_ByBondsRates_NotBrokerCurrval()
    {
        Rate("USD", 80m); Rate("EUR", 90m); Rate("GBP", 110m);
        Holdings(
            [Pos("LUV.US", "USD", 13m, 40.3m, 41.56m, 540.28m), Pos("ROBO.EU", "EUR", 3m, 24.4075m, 27.07m, 81.21m), Pos("X.UK", "GBP", 2m, 10m, 12m, 24m)],
            [new BrokerCash { AccountId = Acc, Currency = "USD", Amount = 4.77m, SyncedAt = DateTime.UtcNow }]);

        var h = await Create().BuildAsync(Acc, AsOf);

        h.Positions.Single(p => p.Position.Ticker == "LUV.US").MarketValueRub.Should().Be(540.28m * 80m);
        h.Positions.Single(p => p.Position.Ticker == "ROBO.EU").MarketValueRub.Should().Be(81.21m * 90m);
        h.Positions.Single(p => p.Position.Ticker == "X.UK").MarketValueRub.Should().Be(24m * 110m);
        h.Cash.Single().AmountRub.Should().Be(4.77m * 80m);
        var expectedRub = 540.28m * 80m + 81.21m * 90m + 24m * 110m + 4.77m * 80m;
        h.TotalValueRub.Should().Be(expectedRub);
        h.TotalValueUsd.Should().Be(expectedRub / 80m);
        h.DataIncomplete.Should().BeFalse();
    }

    [Fact]
    public async Task Build_PnlAndFraction_InPositionCurrency()
    {
        Rate("USD", 80m);
        Holdings([Pos("LUV.US", "USD", 13m, 40m, 44m, 572m)]);

        var p = (await Create().BuildAsync(Acc, AsOf)).Positions.Single();

        p.UnrealizedPnl.Should().Be(572m - 520m);
        p.UnrealizedPnlFraction.Should().Be(52m / 520m, "ДОЛЯ, не проценты");
    }

    [Fact]
    public async Task Build_ZeroCost_FractionIsNull_NotDivisionByZero()
    {
        Rate("USD", 80m);
        Holdings([Pos("GIFT.US", "USD", 5m, 0m, 10m, 50m)]);

        var p = (await Create().BuildAsync(Acc, AsOf)).Positions.Single();

        p.UnrealizedPnlFraction.Should().BeNull();
        p.UnrealizedPnl.Should().Be(50m);
    }

    [Fact]
    public async Task Build_ZeroMarketValue_FallsBackToLastPriceTimesQuantity()
    {
        Rate("USD", 80m);
        Holdings([Pos("X.US", "USD", 4m, 5m, 6m, 0m)]);

        (await Create().BuildAsync(Acc, AsOf)).Positions.Single().MarketValue.Should().Be(24m);
    }

    [Fact]
    public async Task Build_MissingRate_GivesNullRub_AndDataIncomplete_NotZeroSilently()
    {
        Rate("USD", 80m); Rate("GBP", null);
        Holdings([Pos("A.US", "USD", 1m, 1m, 10m, 10m), Pos("B.UK", "GBP", 1m, 1m, 20m, 20m)]);

        var h = await Create().BuildAsync(Acc, AsOf);

        h.Positions.Single(p => p.Position.Ticker == "B.UK").MarketValueRub.Should().BeNull();
        h.DataIncomplete.Should().BeTrue();
        h.TotalValueRub.Should().Be(800m, "позиция без курса в итог не входит");
    }

    [Fact]
    public async Task Build_RubCurrency_NeedsNoRate()
    {
        Rate("USD", 80m);
        Holdings([], [new BrokerCash { AccountId = Acc, Currency = "RUB", Amount = 1500m, SyncedAt = DateTime.UtcNow }]);

        var h = await Create().BuildAsync(Acc, AsOf);

        h.Cash.Single().AmountRub.Should().Be(1500m);
        h.DataIncomplete.Should().BeFalse();
        _rates.Verify(r => r.GetRubPerUnitAsync("RUB", It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Build_NoUsdRate_TotalUsdIsNull()
    {
        Rate("USD", null); Rate("EUR", 90m);
        Holdings([Pos("ROBO.EU", "EUR", 1m, 1m, 10m, 10m)]);

        var h = await Create().BuildAsync(Acc, AsOf);

        h.TotalValueRub.Should().Be(900m);
        h.TotalValueUsd.Should().BeNull();
    }

    [Fact]
    public async Task T67_OtherBrokersValue_SyncOlderThan26h_IsIncomplete()
    {
        // Задача 67 (#14): последний синк Freedom 2026-10-23 10:00 UTC; через 27 ч сводка капитала помечает оценку неполной (через 2 ч — нет).
        Rate("USD", 80m);
        _accounts.Setup(a => a.GetByUserIdAsync(1)).ReturnsAsync(new[] { new Account { Id = Acc, UserId = 1, Broker = "freedom" } });
        Holdings([Pos("A.US", "USD", 1m, 1m, 10m, 10m)]);
        var synced = new DateTime(2026, 10, 23, 10, 0, 0, DateTimeKind.Utc);

        (await Create().GetOtherBrokersValueAsync(1, AsOf, nowUtc: synced.AddHours(2))).DataIncomplete.Should().BeFalse();
        (await Create().GetOtherBrokersValueAsync(1, AsOf, nowUtc: synced.AddHours(27))).DataIncomplete.Should().BeTrue();
    }

    [Fact]
    public async Task T67_FreedomBond_MarksValuationIncomplete_ShortKeepsNegativeValue()
    {
        Rate("USD", 80m);
        var bond = Pos("XS123.US", "USD", 2m, 100m, 101m, 202m);
        bond.AssetClass = BrokerAssetClass.Bond;
        Holdings([Pos("LUV.US", "USD", -3m, 40m, 41m, -123m), bond]);

        var h = await Create().BuildAsync(Acc, AsOf);

        h.DataIncomplete.Should().BeTrue("облигации Freedom — оценка неполная");
        h.Positions.Single(p => p.Position.Ticker == "LUV.US").MarketValueRub.Should().Be(-123m * 80m, "шорт — отрицательная стоимость");
        h.TotalValueRub.Should().Be((-123m + 202m) * 80m);
    }

    [Fact]
    public async Task OtherBrokersValue_SumsOnlyNonTInvestAccounts()
    {
        Rate("USD", 80m);
        _accounts.Setup(a => a.GetByUserIdAsync(1)).ReturnsAsync(new[]
        {
            new Account { Id = 1, UserId = 1, Broker = "tinvest" },
            new Account { Id = Acc, UserId = 1, Broker = "freedom" },
        });
        Holdings([Pos("A.US", "USD", 1m, 1m, 10m, 10m)]);

        var (value, incomplete) = await Create().GetOtherBrokersValueAsync(1, AsOf, nowUtc: new DateTime(2026, 10, 23, 11, 0, 0, DateTimeKind.Utc));

        value.Should().Be(800m);
        incomplete.Should().BeFalse();
        _positions.Verify(p => p.GetByAccountAsync(1), Times.Never, "счёт T-Invest не оценивается этим билдером");
    }

    [Fact]
    public async Task OtherBrokersValue_NoOtherAccounts_IsZero()
    {
        _accounts.Setup(a => a.GetByUserIdAsync(1)).ReturnsAsync(new[] { new Account { Id = 1, UserId = 1, Broker = "tinvest" } });

        (await Create().GetOtherBrokersValueAsync(1, AsOf)).Should().Be((0m, false));
    }

    // --- комиссии ---

    private static BrokerOperation Fee(decimal amount, string cur, DateTime? date = null) =>
        new() { AccountId = Acc, Type = BrokerOperationType.Fee, Amount = amount, Currency = cur, Date = date ?? new DateTime(2026, 10, 1) };

    [Fact]
    public async Task Fees_UsdSummed_AsPositivePaid_OtherCurrencyConvertedViaRubles()
    {
        Rate("USD", 80m); Rate("EUR", 90m);
        _ops.Setup(o => o.GetByTypeAsync(Acc, BrokerOperationType.Fee)).ReturnsAsync(new[] { Fee(-0.27m, "USD"), Fee(-1m, "USD"), Fee(-0.8m, "EUR") });

        var fees = await Create().GetFeesPaidUsdAsync(Acc, AsOf);

        fees.FeesPaidUsd.Should().Be(0.27m + 1m + 0.8m * 90m / 80m);
        fees.DataIncomplete.Should().BeFalse();
    }

    [Fact]
    public async Task Fees_NoRateForForeignCurrency_IsSkipped_AndFlagged()
    {
        Rate("USD", 80m); Rate("GBP", null);
        _ops.Setup(o => o.GetByTypeAsync(Acc, BrokerOperationType.Fee)).ReturnsAsync(new[] { Fee(-1m, "USD"), Fee(-2m, "GBP") });

        var fees = await Create().GetFeesPaidUsdAsync(Acc, AsOf);

        fees.FeesPaidUsd.Should().Be(1m);
        fees.DataIncomplete.Should().BeTrue();
    }

    [Fact]
    public async Task Fees_None_IsZero()
    {
        _ops.Setup(o => o.GetByTypeAsync(Acc, BrokerOperationType.Fee)).ReturnsAsync(Array.Empty<BrokerOperation>());

        (await Create().GetFeesPaidUsdAsync(Acc, AsOf)).Should().Be(new BrokerFees(0m, false));
    }
}
