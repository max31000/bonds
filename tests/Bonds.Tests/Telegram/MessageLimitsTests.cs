using System.Text.Json;
using System.Text.RegularExpressions;
using Bonds.Core.Policy;
using Bonds.Core.Telegram;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Telegram;

/// <summary>Лимит Telegram 4096 символов (UTF-16, после экранирования): формат плана, /plans, страховочная обрезка; эхо пользовательского ввода.</summary>
public class MessageLimitsTests
{
    private static readonly DateTime Now = new(2026, 10, 21, 9, 0, 0, DateTimeKind.Utc);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static TradePlan BigPlan(int lines, string trigger = "Свободные деньги 7 340 ₽", IReadOnlyList<string>? warnings = null, ulong id = 42) => new()
    {
        Id = id,
        Rule = PlanRules.P1Sweep,
        Trigger = trigger,
        Status = PlanStatuses.PendingApproval,
        DryRun = true,
        CreatedAt = Now,
        ExpiresAt = Now.AddMinutes(30),
        BotNonce = "AbCdEfGh123",
        SummaryJson = JsonSerializer.Serialize(
            new TradePlanSummary { Portfolio = "rub", Currency = "RUB", CashBefore = 1m, CashAfter = 0m, BuyTotal = 1m, FeeTotal = 0m, Warnings = warnings ?? [] }, Web),
        Lines = Enumerable.Range(1, lines).Select(i => new TradePlanLine
        {
            PlanId = id, LineNo = i, Ticker = "LONGTICKER-" + i, Side = PlanSides.Buy, QuantityLots = 1234, LotSize = 1m, LimitPrice = 1234.5678m, EstAmount = 1523456.78m, EstFee = 1234.5m, Currency = "RUB",
        }).ToList(),
    };

    /// <summary>Нет разорванной сущности/тега: все «&amp;» — целые сущности, «&lt;» и «&gt;» парные, теги сбалансированы.</summary>
    private static void AssertWellFormed(string html)
    {
        Regex.Matches(html, "&").Count.Should().Be(Regex.Matches(html, "&(amp|lt|gt);").Count, "сущности целые");
        html.Count(c => c == '<').Should().Be(html.Count(c => c == '>'), "теги целые");
        foreach (var tag in new[] { "b", "i", "code" })
            Regex.Matches(html, $"<{tag}>").Count.Should().Be(Regex.Matches(html, $"</{tag}>").Count, $"тег {tag} закрыт");
    }

    [Theory]
    [InlineData(60, 10, 5)]
    [InlineData(60, 4000, 40)]
    [InlineData(300, 300, 8)]
    [InlineData(1, 6000, 3)]
    public void FormatHtml_NeverExceedsTelegramLimit_AndKeepsMarkupWellFormed(int lines, int triggerLength, int warnings)
    {
        // «&» и «<» растут при экранировании — режется именно экранированный текст.
        var trigger = string.Concat(Enumerable.Repeat("a&b<c>", triggerLength / 6 + 1))[..triggerLength];
        var warns = Enumerable.Range(0, warnings).Select(i => string.Concat(Enumerable.Repeat("w&<", 400)) + i).ToList();

        var html = PlanMessageFormatter.FormatHtml(BigPlan(lines, trigger, warns), Now, string.Concat(Enumerable.Repeat("✅&", 500)));

        html.Length.Should().BeLessThanOrEqualTo(4096);
        AssertWellFormed(html);
        if (lines >= 60) html.Should().Contain("и ещё ").And.Contain("стр.");
        html.Should().Contain("Действует до");
    }

    [Fact]
    public void FormatHtml_60Lines_TruncatesWithCorrectRemainder()
    {
        var html = PlanMessageFormatter.FormatHtml(BigPlan(60), Now);

        html.Length.Should().BeLessThanOrEqualTo(4096);
        var shown = Regex.Matches(html, "<code>").Count;
        shown.Should().BeLessThan(60).And.BeGreaterThan(20);
        html.Should().Contain($"… и ещё {60 - shown} стр.");
    }

    [Fact]
    public void FormatHtml_ManyWarnings_ShowsFiveAndRemainder()
    {
        var html = PlanMessageFormatter.FormatHtml(BigPlan(1, warnings: Enumerable.Range(1, 8).Select(i => "w" + i).ToList()), Now);

        Regex.Matches(html, "⚠ w").Count.Should().Be(5);
        html.Should().Contain("… и ещё 3 предупр.");
    }

    [Fact]
    public void FormatHtml_Footer_IsEscapedInside_ExactlyOnce()
    {
        var html = PlanMessageFormatter.FormatHtml(BigPlan(1), Now, "Отклонено <b> & co");

        html.Should().Contain("Отклонено &lt;b&gt; &amp; co").And.NotContain("&amp;amp;");
    }

    [Fact]
    public void HtmlTruncated_DoesNotSplitEntityOrSurrogatePair()
    {
        var text = string.Concat(Enumerable.Repeat("😀&", 100));
        for (var max = 5; max < 40; max++)
        {
            var cut = BotFormat.HtmlTruncated(text, max);
            cut.Length.Should().BeLessThanOrEqualTo(max);
            cut.Should().EndWith("…");
            AssertWellFormed(cut);
            char.IsHighSurrogate(cut[^2]).Should().BeFalse();
        }
    }

    [Fact]
    public void TruncateHtml_NeverCutsInsideTagOrEntity_ClosesOpenTags()
    {
        var html = "<b>" + string.Concat(Enumerable.Repeat("x&amp;<code>y</code>", 400)) + "</b>";
        for (var max = 20; max < 120; max++)
        {
            var cut = BotFormat.TruncateHtml(html, max);
            cut.Length.Should().BeLessThanOrEqualTo(max);
            AssertWellFormed(cut);
        }

        BotFormat.TruncateHtml("short", 100).Should().Be("short");
    }

    [Fact]
    public void Echo_TruncatesTo32Chars()
    {
        BotFormat.Echo(new string('x', 32)).Should().HaveLength(32);
        BotFormat.Echo(new string('x', 500)).Should().Be(new string('x', 32) + "…");
    }

    [Fact]
    public void Plans_200Open_ShowsTenNewest_WithinTelegramLimits()
    {
        var plans = Enumerable.Range(1, 200).Select(i => BigPlan(1, id: (ulong)i)).Reverse().ToList(); // от новых к старым

        var message = BotTexts.Plans(plans, "https://example.org", 10);

        message.Html.Length.Should().BeLessThanOrEqualTo(4096);
        Regex.Matches(message.Html, "№").Count.Should().Be(10);
        message.Html.Should().Contain("№200").And.Contain("№191").And.NotContain("№190 ").And.Contain("… и ещё 190 — см. веб");
        message.Keyboard!.Rows.Should().HaveCount(10);
        message.Keyboard.Rows.Sum(r => r.Count).Should().BeLessThanOrEqualTo(100);
    }

    [Fact]
    public void Plans_NotMoreThanLimit_NoRemainderLine() =>
        BotTexts.Plans([BigPlan(1)], null, 10).Html.Should().NotContain("см. веб");

    [Theory]
    [InlineData("/inflow 0.5")]
    [InlineData("/inflow 0.99 usd")]
    [InlineData("/withdraw 0.01")]
    [InlineData("/withdraw 0,5 usd")]
    public void Amount_BelowOneUnit_IsRejected(string text)
    {
        var c = BotCommandParser.Parse(text);
        c.Kind.Should().Be(BotCommandKind.Invalid);
        c.Error.Should().Contain("не меньше 1");
    }

    [Theory]
    [InlineData("/inflow 1", 1)]
    [InlineData("/withdraw 1.0 usd", 1)]
    public void Amount_ExactlyOneUnit_IsAccepted(string text, double amount)
    {
        var c = BotCommandParser.Parse(text);
        c.Kind.Should().NotBe(BotCommandKind.Invalid);
        c.Amount.Should().Be((decimal)amount);
    }

    [Fact]
    public void Withdraw_SmallPercent_IsNotSubjectToAmountFloor() =>
        BotCommandParser.Parse("/withdraw 0.5%").Fraction.Should().Be(0.005m);

    [Fact]
    public void ErrorReplies_EchoUserTokensTruncatedTo32Chars()
    {
        var junk = new string('я', 500);
        foreach (var text in new[] { "/inflow " + junk, "/inflow 100 " + junk, "/" + junk, "/withdraw 5" + junk })
        {
            var c = BotCommandParser.Parse(text);
            c.Kind.Should().Be(BotCommandKind.Invalid, text[..Math.Min(20, text.Length)]);
            c.Error!.Length.Should().BeLessThan(250);
            c.Error.Should().Contain("я…").And.NotContain(new string('я', 33));
        }
    }
}
