using System.Text.Json;
using Bonds.Core.Brokers;
using Bonds.Core.Models;
using Bonds.Infrastructure.Connectors.Cbr;
using Bonds.Infrastructure.Connectors.Freedom;
using Bonds.Infrastructure.Connectors.Freedom.Models;
using Bonds.Infrastructure.Sync;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Brokers;

/// <summary>Задача 60: маппинг сделок и движений денег Freedom в журнал (по фикстурам реальных ответов), знаки, тикеры, идемпотентность.</summary>
public class FreedomOperationMapperTests
{
    private const ulong Acc = 7;

    private static JsonElement Root(string fixture) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Freedom", fixture + ".json"))).RootElement;

    private static IReadOnlyList<BrokerOperation> MapFixtureCashFlows() =>
        FreedomOperationMapper.MapCashFlows(Acc, FreedomParser.ParseCashFlows(Root("getUserCashFlows")));

    private static FreedomCashFlow Flow(string type, decimal amount, string comment = "c", string currency = "USD", string id = "1") =>
        new(id, type, new DateTime(2026, 10, 1, 12, 0, 0), currency, amount, comment);

    // --- движения денег: каждый type_code из фикстуры ---

    [Fact]
    public void CashFlowFixture_MapsEveryTypeCode()
    {
        var ops = MapFixtureCashFlows();

        ops.Should().HaveCount(5);
        ops.Select(o => (o.RawType, o.Type)).Should().BeEquivalentTo(new[]
        {
            ("commission_for_trades", BrokerOperationType.Fee),
            ("bank", BrokerOperationType.Deposit),
            ("dividend", BrokerOperationType.Dividend),
            ("tax", BrokerOperationType.Tax),
            ("card", BrokerOperationType.Deposit),
        });
    }

    [Fact]
    public void CashFlowFixture_KeepsBrokerSign_AndCurrency()
    {
        var ops = MapFixtureCashFlows();

        ops.Single(o => o.Type == BrokerOperationType.Fee).Amount.Should().Be(-0.27m, "комиссия — расход");
        ops.Single(o => o.Type == BrokerOperationType.Tax).Amount.Should().Be(-0.52m);
        ops.Single(o => o.RawType == "bank").Amount.Should().Be(73.27m);
        ops.Single(o => o.Type == BrokerOperationType.Dividend).Amount.Should().Be(0.01m);
        ops.Should().OnlyContain(o => o.Currency == "USD" && o.AccountId == Acc);
    }

    [Fact]
    public void FeeTicker_IsExtractedFromTradeComment()
    {
        var fee = MapFixtureCashFlows().Single(o => o.Type == BrokerOperationType.Fee);

        fee.Ticker.Should().Be("SPYM.US");
    }

    [Theory]
    [InlineData("(Trade 900000101 buy SPYM.US ) Рынок: kazakhstan_its", "SPYM.US")]
    [InlineData("(Trade 12 sell BRK.B ) x", "BRK.B")]
    [InlineData("(Trade 5 buy 7203.JP ) x", "7203.JP")]
    [InlineData("Комиссия без сделки", null)]
    [InlineData("", null)]
    public void ExtractFeeTicker_Cases(string comment, string? expected) =>
        FreedomOperationMapper.ExtractFeeTicker(comment).Should().Be(expected);

    [Fact]
    public void DividendAndTax_TickerIsExtractedFromNestedParentheses()
    {
        var ops = MapFixtureCashFlows();

        ops.Single(o => o.Type == BrokerOperationType.Dividend).Ticker.Should().Be("UFO.US");
        ops.Single(o => o.Type == BrokerOperationType.Tax).Ticker.Should().Be("LUV.US");
    }

    [Fact]
    public void BankAndCard_PositiveIsDeposit_NegativeIsWithdrawal()
    {
        var ops = FreedomOperationMapper.MapCashFlows(Acc, [Flow("bank", 100m), Flow("bank", -40m, id: "2"), Flow("card", 5m, id: "3")]);

        ops.Select(o => o.Type).Should().Equal(BrokerOperationType.Deposit, BrokerOperationType.Withdrawal, BrokerOperationType.Deposit);
        ops[1].Amount.Should().Be(-40m);
    }

    [Fact]
    public void UnknownTypeCode_BecomesOther_WithRawType()
    {
        var op = FreedomOperationMapper.MapCashFlows(Acc, [Flow("custody_fee", -1.5m)]).Single();

        op.Type.Should().Be(BrokerOperationType.Other);
        op.RawType.Should().Be("custody_fee");
        op.Amount.Should().Be(-1.5m);
    }

    [Fact]
    public void ZeroAmountCashFlow_IsSkipped() =>
        FreedomOperationMapper.MapCashFlows(Acc, [Flow("bank", 0m)]).Should().BeEmpty();

    [Fact]
    public void LegacyRurCurrency_IsNormalizedToRub()
    {
        var op = FreedomOperationMapper.MapCashFlows(Acc, [Flow("bank", 1000m, currency: "RUR")]).Single();

        op.Currency.Should().Be("RUB");
    }

    [Fact]
    public void LongCommentAndRawType_AreTruncated()
    {
        var op = FreedomOperationMapper.MapCashFlows(Acc, [Flow(new string('t', 100), 1m, new string('c', 900))]).Single();

        op.Comment!.Length.Should().Be(FreedomOperationMapper.MaxCommentLength);
        op.RawType.Length.Should().Be(FreedomOperationMapper.MaxRawTypeLength);
    }

    // --- идемпотентность ---

    [Fact]
    public void CashFlowExternalId_IsStable_AcrossRepeatedMapping()
    {
        var first = MapFixtureCashFlows().Select(o => o.ExternalId).ToList();
        var second = MapFixtureCashFlows().Select(o => o.ExternalId).ToList();

        second.Should().Equal(first);
        first.Should().OnlyContain(id => id.StartsWith("cf:") && id.Length <= 64).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public void CashFlowExternalId_DependsOnDateTypeSumComment()
    {
        var baseFlow = Flow("bank", 10m, "x");
        var ids = new[]
        {
            FreedomOperationMapper.CashFlowHash(baseFlow),
            FreedomOperationMapper.CashFlowHash(baseFlow with { Date = baseFlow.Date.AddSeconds(1) }),
            FreedomOperationMapper.CashFlowHash(baseFlow with { TypeCode = "card" }),
            FreedomOperationMapper.CashFlowHash(baseFlow with { Amount = 11m }),
            FreedomOperationMapper.CashFlowHash(baseFlow with { Comment = "y" }),
        };

        ids.Should().OnlyHaveUniqueItems();
        FreedomOperationMapper.CashFlowHash(baseFlow with { Id = "other-id" }).Should().Be(ids[0], "broker id в ключ не входит");
        FreedomOperationMapper.CashFlowHash(baseFlow with { Amount = 10.00m }).Should().Be(ids[0], "масштаб decimal не меняет ключ");
    }

    [Fact]
    public void IdenticalCashFlowRows_GetDistinctExternalIds_NotCollapsed()
    {
        var ops = FreedomOperationMapper.MapCashFlows(Acc, [Flow("bank", 10m), Flow("bank", 10m, id: "2")]);

        ops.Select(o => o.ExternalId).Should().OnlyHaveUniqueItems();
        ops[1].ExternalId.Should().EndWith("#2");
        ops[0].ExternalId.Should().NotContain("#");
    }

    // --- сделки ---

    [Fact]
    public void TradeFixture_SellIsPositiveAmount_WithTickerQuantityPrice()
    {
        var trades = FreedomParser.ParseTrades(Root("getTradesHistory"));
        var op = FreedomOperationMapper.MapTrade(Acc, trades.First(t => t.Id == "582334538"));

        op.ExternalId.Should().Be("trade:582334538");
        op.Type.Should().Be(BrokerOperationType.Sell);
        op.Amount.Should().Be(47.62m, "продажа — деньги пришли");
        op.Currency.Should().Be("EUR", "валюта сделки, не комиссии (комиссия в USD)");
        op.Ticker.Should().Be("IPRE.EU");
        op.Quantity.Should().Be(11m);
        op.Price.Should().Be(4.329m);
        op.RawType.Should().Be("trade");
    }

    [Fact]
    public void Trade_BuyIsNegativeAmount_RegardlessOfSourceSign()
    {
        var buy = new FreedomTrade("1", "o", new DateTime(2026, 10, 1), "SPYM.US", FreedomTradeSide.Buy, 2m, 10m, 20m, "USD", 0.1m, "USD");
        var buyNegativeSource = buy with { Id = "2", Amount = -20m };

        FreedomOperationMapper.MapTrade(Acc, buy).Amount.Should().Be(-20m);
        FreedomOperationMapper.MapTrade(Acc, buyNegativeSource).Amount.Should().Be(-20m);
        FreedomOperationMapper.MapTrade(Acc, buy).Type.Should().Be(BrokerOperationType.Buy);
    }

    [Theory]
    [InlineData("EUR/USD")]
    [InlineData("KZT/USD")]
    public void CurrencyConversionTrade_IsOtherWithFxRawType_NoTicker(string pair)
    {
        var fx = new FreedomTrade("9", "o", new DateTime(2026, 10, 1), pair, FreedomTradeSide.Buy, 100m, 1.1m, 110m, "USD", 0m, "USD");

        var op = FreedomOperationMapper.MapTrade(Acc, fx);

        op.Type.Should().Be(BrokerOperationType.Other);
        op.RawType.Should().Be("fx");
        op.Ticker.Should().BeNull();
        op.Comment.Should().StartWith(pair);
        op.ExternalId.Should().Be("trade:9");
    }

    [Fact]
    public void Trade_ZeroSumm_FallsBackToPriceTimesQuantity()
    {
        var t = new FreedomTrade("3", "o", new DateTime(2026, 10, 1), "X.US", FreedomTradeSide.Sell, 3m, 5m, 0m, "USD", 0m, "USD");

        FreedomOperationMapper.MapTrade(Acc, t).Amount.Should().Be(15m);
    }
}

public class BrokerAssetClassifierTests
{
    [Theory]
    [InlineData("L&G ROBO Global Robotics and Automation UCITS ETF", "etf")]
    [InlineData("Procure Space ETF", "etf")]
    [InlineData("Vanguard Total Stock Market Index Fund", "etf")]
    [InlineData("iShares Core MSCI funds", "etf")]
    [InlineData("Southwest Airlines", "stock")]
    [InlineData("CD Projekt SA", "stock")]
    [InlineData("Fundamental Holdings Inc", "stock")]
    [InlineData("", "stock")]
    [InlineData(null, "stock")]
    public void Classify_ByName(string? name, string expected) => BrokerAssetClassifier.Classify(name).Should().Be(expected);

    [Theory]
    [InlineData("RUR", "RUB")]
    [InlineData("rur", "RUB")]
    [InlineData(" usd ", "USD")]
    [InlineData("KZT", "KZT")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeCurrency_Cases(string? code, string? expected) => BrokerAssetClassifier.NormalizeCurrency(code).Should().Be(expected);
}

public class CbrRatesParserTests
{
    [Fact]
    public void Parse_ReadsUnitRate_WithDecimalComma()
    {
        const string xml = "<?xml version=\"1.0\" encoding=\"windows-1251\"?><ValCurs ID=\"R01239\" DateRange1=\"15.10.2026\" DateRange2=\"16.10.2026\" name=\"x\">"
            + "<Record Date=\"15.10.2026\" Id=\"R01239\"><Nominal>1</Nominal><Value>93,9229</Value><VunitRate>93,9229</VunitRate></Record>"
            + "<Record Date=\"16.10.2026\" Id=\"R01239\"><Nominal>1</Nominal><Value>93,7693</Value><VunitRate>93,7693</VunitRate></Record></ValCurs>";

        var points = CbrRatesParser.Parse(xml);

        points.Should().Equal(new CbrRatePoint(new DateOnly(2026, 10, 15), 93.9229m), new CbrRatePoint(new DateOnly(2026, 10, 16), 93.7693m));
    }

    [Fact]
    public void Parse_NoVunitRate_DividesValueByNominal()
    {
        const string xml = "<ValCurs><Record Date=\"15.10.2026\"><Nominal>100</Nominal><Value>55,0000</Value></Record></ValCurs>";

        CbrRatesParser.Parse(xml).Single().RubPerUnit.Should().Be(0.55m);
    }

    [Fact]
    public void Parse_SkipsBadRecords_AndHandlesEmptyDocument()
    {
        const string xml = "<ValCurs><Record Date=\"bad\"><VunitRate>1,0</VunitRate></Record><Record Date=\"15.10.2026\"><VunitRate>0</VunitRate></Record></ValCurs>";

        CbrRatesParser.Parse(xml).Should().BeEmpty();
        CbrRatesParser.Parse("<Error>no data</Error>").Should().BeEmpty();
    }
}
