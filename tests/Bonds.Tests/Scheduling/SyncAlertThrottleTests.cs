using Bonds.Infrastructure.Scheduling;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Scheduling;

/// <summary>
/// Plan/13 часть D: не чаще одного Telegram-алерта на уникальный набор ошибок в сутки.
/// </summary>
public class SyncAlertThrottleTests
{
    private static readonly DateOnly Today = new(2026, 7, 3);
    private static readonly DateOnly Tomorrow = Today.AddDays(1);

    [Fact]
    public void ShouldAlert_NoPriorAlert_ReturnsTrue()
    {
        var throttle = new SyncAlertThrottle();

        throttle.ShouldAlert(["MOEX недоступен"], Today).Should().BeTrue();
    }

    [Fact]
    public void ShouldAlert_EmptyErrors_ReturnsFalse()
    {
        var throttle = new SyncAlertThrottle();

        throttle.ShouldAlert([], Today).Should().BeFalse();
    }

    [Fact]
    public void ShouldAlert_SameErrorsSameDayAfterMarked_ReturnsFalse()
    {
        var throttle = new SyncAlertThrottle();
        var errors = new[] { "MOEX недоступен" };

        throttle.MarkAlerted(errors, Today);

        throttle.ShouldAlert(errors, Today).Should().BeFalse("тот же набор ошибок в тот же день уже был отправлен");
    }

    [Fact]
    public void ShouldAlert_SameErrorsDifferentOrder_StillConsideredSameSet()
    {
        var throttle = new SyncAlertThrottle();
        throttle.MarkAlerted(["A: fail", "B: fail"], Today);

        throttle.ShouldAlert(["B: fail", "A: fail"], Today).Should().BeFalse("набор ошибок не зависит от порядка сбора");
    }

    [Fact]
    public void ShouldAlert_DifferentErrorsSameDay_ReturnsTrue()
    {
        var throttle = new SyncAlertThrottle();
        throttle.MarkAlerted(["MOEX недоступен"], Today);

        throttle.ShouldAlert(["T-Invest недоступен"], Today).Should().BeTrue("другой набор ошибок — новый повод для алерта");
    }

    [Fact]
    public void ShouldAlert_SameErrorsNextDay_ReturnsTrue()
    {
        var throttle = new SyncAlertThrottle();
        var errors = new[] { "MOEX недоступен" };
        throttle.MarkAlerted(errors, Today);

        throttle.ShouldAlert(errors, Tomorrow).Should().BeTrue("на следующий день лимит должен сброситься");
    }
}
