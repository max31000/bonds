using Bonds.Core.Telegram;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Telegram;

/// <summary>Задача 67: тексты бота — /help зависит от режима (NIT), /stop и /resume описывают поведение планов (#11).</summary>
public class AuditBotTextsTests
{
    [Fact]
    public void T67_Help_DependsOnMode()
    {
        var observe = BotTexts.Help(paused: false, dryRun: true);
        observe.Should().Contain(PlanMessageFormatter.ObservationNote).And.NotContain(PlanMessageFormatter.RealOrdersMark);

        var live = BotTexts.Help(paused: true, dryRun: false);
        live.Should().Contain(PlanMessageFormatter.RealOrdersMark).And.Contain("Отправить заявки").And.NotContain(PlanMessageFormatter.ObservationNote);
        live.Should().Contain("заявки не отправляются");
    }

    [Fact]
    public void T67_StopAndResume_DescribeWhatHappensToPlans()
    {
        BotTexts.StopReply.Should().Contain("НЕ отменяются").And.Contain("до конца своего срока").And.Contain("висящие заявки снимаются").And.Contain("/resume");
        BotTexts.ResumeReply.Should().Contain("срок которых не истёк, исполняются");
    }
}
