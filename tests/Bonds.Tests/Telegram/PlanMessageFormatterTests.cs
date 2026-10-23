using System.Text.Json;
using Bonds.Core.Policy;
using Bonds.Core.Telegram;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Telegram;

public class PlanMessageFormatterTests
{
    private static readonly DateTime Now = new(2026, 10, 21, 9, 0, 0, DateTimeKind.Utc); // 12:00 МСК

    private static TradePlan Plan(string status = PlanStatuses.PendingApproval, string trigger = "Свободные деньги 7 340 ₽", string ticker = "TMON@") => new()
    {
        Id = 42,
        Rule = PlanRules.P1Sweep,
        Trigger = trigger,
        Status = status,
        DryRun = true,
        CreatedAt = Now,
        ExpiresAt = Now.AddMinutes(30),
        BotNonce = "AbCdEfGh123",
        SummaryJson = JsonSerializer.Serialize(
            new TradePlanSummary { Portfolio = "rub", Currency = "RUB", CashBefore = 7340m, CashAfter = 11m, BuyTotal = 7329m, ReservedBuy = 7329m, FeeTotal = 0m },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        Lines =
        [
            new TradePlanLine
            {
                PlanId = 42, LineNo = 1, Ticker = ticker, Side = PlanSides.Buy, QuantityLots = 44, LotSize = 1m, LimitPrice = 166.57m, EstAmount = 7329.08m, EstFee = 0m, Currency = "RUB",
            },
        ],
    };

    [Fact]
    public void FormatLine_ReferenceString()
    {
        PlanMessageFormatter.FormatLine(Plan().Lines[0]).Should().Be("КУПИТЬ TMON@ ×44 @ 166.57 ≈ 7 329 ₽, комиссия 0");
    }

    [Fact]
    public void FormatLine_SellWithFee()
    {
        var line = Plan().Lines[0];
        line.Side = PlanSides.Sell;
        line.EstFee = 3.7m;
        PlanMessageFormatter.FormatLine(line).Should().Be("ПРОДАТЬ TMON@ ×44 @ 166.57 ≈ 7 329 ₽, комиссия 4 ₽");
    }

    [Fact]
    public void NewPendingPlan_HasButtons_DryRunNote_AndDetailsLink()
    {
        var message = PlanMessageFormatter.ForNewPlan(Plan(), Now, "https://example.org/");

        message.Html.Should().Contain("<b>План №42</b>").And.Contain("размещение свободных денег");
        message.Html.Should().Contain("<code>КУПИТЬ TMON@ ×44 @ 166.57 ≈ 7 329 ₽, комиссия 0</code>");
        message.Html.Should().Contain("покупки ≈ 7 329 ₽");
        message.Html.Should().Contain("Свободные деньги: 7 340 ₽ → 11 ₽");
        message.Html.Should().Contain("Действует до 12:30 МСК");
        message.Html.Should().Contain("режим наблюдения: сделка не отправляется");

        var buttons = message.Keyboard!.Rows.SelectMany(r => r).ToList();
        buttons.Select(b => b.Text).Should().Equal("✅ Подтвердить", "❌ Отклонить", "🔎 Детали");
        buttons[0].CallbackData.Should().Be("p:42:a:AbCdEfGh123");
        buttons[1].CallbackData.Should().Be("p:42:r:AbCdEfGh123");
        buttons[2].Url.Should().Be("https://example.org/bonds/plans/42");
        buttons.Where(b => b.CallbackData is not null).Should().OnlyContain(b => System.Text.Encoding.UTF8.GetByteCount(b.CallbackData!) <= 64);
    }

    [Fact]
    public void NoWebBaseUrl_NoDetailsButton()
    {
        var message = PlanMessageFormatter.ForNewPlan(Plan(), Now, null);
        message.Keyboard!.Rows.SelectMany(r => r).Select(b => b.Text).Should().Equal("✅ Подтвердить", "❌ Отклонить");
    }

    [Fact]
    public void AutoApprovedPlan_IsInformational_WithoutButtons()
    {
        var message = PlanMessageFormatter.ForNewPlan(Plan(PlanStatuses.AutoApproved), Now, "https://example.org");
        message.Keyboard.Should().BeNull();
        message.Html.Should().StartWith("ℹ Авто-подтверждено по лимитам.");
    }

    [Fact]
    public void UserControlledText_IsHtmlEscaped()
    {
        var message = PlanMessageFormatter.ForNewPlan(Plan(trigger: "<b>a & b</b>", ticker: "X<Y>&Z"), Now, null);
        message.Html.Should().Contain("Причина: &lt;b&gt;a &amp; b&lt;/b&gt;");
        message.Html.Should().Contain("X&lt;Y&gt;&amp;Z");
        message.Html.Should().NotContain("<b>a");
    }

    [Fact]
    public void Footer_IsAppended_AndLongPlanIsTruncated()
    {
        var plan = Plan();
        plan.Lines = Enumerable.Range(1, 300).Select(i => new TradePlanLine
        {
            PlanId = 42, LineNo = i, Ticker = "TICKER" + i, Side = PlanSides.Buy, QuantityLots = 1, LotSize = 1m, LimitPrice = 100m, EstAmount = 100m, Currency = "RUB",
        }).ToList();

        var html = PlanMessageFormatter.FormatHtml(plan, Now, "✅ Подтверждено 14:05 МСК");

        html.Length.Should().BeLessThan(4096);
        html.Should().Contain("и ещё ").And.EndWith("✅ Подтверждено 14:05 МСК");
    }

    [Theory]
    [InlineData(0, 0, "0")]
    [InlineData(7329.5, 0, "7 330")]
    [InlineData(1234567.891, 2, "1 234 567,89")]
    [InlineData(-1500, 0, "−1 500")]
    [InlineData(12, 0, "12")]
    public void Number_RussianLocale(double value, int decimals, string expected) => BotFormat.Number((decimal)value, decimals).Should().Be(expected);

    [Fact]
    public void Percent_FractionToPercentOnce()
    {
        BotFormat.Percent(0.142m).Should().Be("14,2%");
        BotFormat.PercentPoints(-0.008m).Should().Be("−0,8 п.п.");
        BotFormat.PercentPoints(0.02m).Should().Be("+2,0 п.п.");
    }
}
