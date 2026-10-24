using Bonds.Core.Capital;
using Bonds.Core.Telegram;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Telegram;

public class BotCommandParserTests
{
    [Theory]
    [InlineData("/help", BotCommandKind.Help)]
    [InlineData("/start", BotCommandKind.Help)]
    [InlineData("/status", BotCommandKind.Status)]
    [InlineData("/STATUS@MyBonds_bot", BotCommandKind.Status)]
    [InlineData("  /plans  ", BotCommandKind.Plans)]
    [InlineData("/stop", BotCommandKind.Stop)]
    [InlineData("/withdrawn", BotCommandKind.Withdrawn)]
    [InlineData("/resume", BotCommandKind.Resume)]
    public void SimpleCommands(string text, BotCommandKind kind) => BotCommandParser.Parse(text).Kind.Should().Be(kind);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("привет")]
    [InlineData("status")]
    public void PlainText_IsNotACommand(string? text) => BotCommandParser.Parse(text).Kind.Should().Be(BotCommandKind.NotCommand);

    [Fact]
    public void UnknownCommand_GivesHint()
    {
        var c = BotCommandParser.Parse("/buy 100");
        c.Kind.Should().Be(BotCommandKind.Invalid);
        c.Error.Should().Contain("/help");
    }

    [Theory]
    [InlineData("/inflow 50000", 50000, "rub")]
    [InlineData("/inflow 50000.5", 50000.5, "rub")]
    [InlineData("/inflow 1234,5", 1234.5, "rub")]
    [InlineData("/inflow 500 usd", 500, "usd")]
    [InlineData("/inflow 500usd", 500, "usd")]
    [InlineData("/inflow 500$", 500, "usd")]
    [InlineData("/inflow 7000 руб", 7000, "rub")]
    public void Inflow_Amount(string text, double amount, string portfolio)
    {
        var c = BotCommandParser.Parse(text);
        c.Kind.Should().Be(BotCommandKind.Inflow);
        c.Amount.Should().Be((decimal)amount);
        c.Fraction.Should().BeNull();
        c.Portfolio.Should().Be(portfolio);
    }

    [Theory]
    [InlineData("/inflow")]
    [InlineData("/inflow abc")]
    [InlineData("/inflow -5")]
    [InlineData("/inflow 0")]
    [InlineData("/inflow 10%")]
    [InlineData("/inflow 100 eur")]
    [InlineData("/inflow 100 usd rub")]
    [InlineData("/inflow 1e5")]
    public void Inflow_Garbage_GivesHint(string text)
    {
        var c = BotCommandParser.Parse(text);
        c.Kind.Should().Be(BotCommandKind.Invalid);
        c.Error.Should().Contain("/inflow");
    }

    [Fact]
    public void Withdraw_Percent_BecomesFraction_NotPercent()
    {
        var c = BotCommandParser.Parse("/withdraw 15%");
        c.Kind.Should().Be(BotCommandKind.Withdraw);
        c.Fraction.Should().Be(0.15m, "15% — это ДОЛЯ 0.15 (контракт единиц: бэкенд оперирует долями)");
        c.Amount.Should().BeNull();
        c.Portfolio.Should().Be(CapitalPortfolios.Rub);
    }

    [Theory]
    [InlineData("/withdraw 50000", 50000, "rub")]
    [InlineData("/withdraw 200 usd", 200, "usd")]
    [InlineData("/withdraw 200 USD", 200, "usd")]
    public void Withdraw_Amount(string text, double amount, string portfolio)
    {
        var c = BotCommandParser.Parse(text);
        c.Kind.Should().Be(BotCommandKind.Withdraw);
        c.Amount.Should().Be((decimal)amount);
        c.Fraction.Should().BeNull();
        c.Portfolio.Should().Be(portfolio);
    }

    [Fact]
    public void Withdraw_PercentInUsdPortfolio()
    {
        var c = BotCommandParser.Parse("/withdraw 100% usd");
        c.Fraction.Should().Be(1m);
        c.Portfolio.Should().Be("usd");
    }

    [Theory]
    [InlineData("/withdraw 150%")]
    [InlineData("/withdraw 0%")]
    [InlineData("/withdraw 15% usd usd")]
    [InlineData("/withdraw 15%usd usd")]
    [InlineData("/withdraw много")]
    public void Withdraw_Garbage_GivesHint(string text)
    {
        var c = BotCommandParser.Parse(text);
        c.Kind.Should().Be(BotCommandKind.Invalid);
        c.Error.Should().Contain("/withdraw");
    }

    [Fact]
    public void Exit_TakesBasketCode()
    {
        var c = BotCommandParser.Parse("/exit Temporary");
        c.Kind.Should().Be(BotCommandKind.Exit);
        c.Code.Should().Be("temporary");
    }

    [Theory]
    [InlineData("/exit")]
    [InlineData("/exit a b")]
    [InlineData("/exit <script>")]
    public void Exit_Garbage_GivesHint(string text) => BotCommandParser.Parse(text).Kind.Should().Be(BotCommandKind.Invalid);
}
