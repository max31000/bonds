using Bonds.Core.Time;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Time;

/// <summary>
/// T-5 (M-3): единый источник «бизнес-сегодня» — московская дата. У границы суток UTC-дата и
/// MSK-дата (UTC+3) расходятся; раньше разные части системы брали то DateTime.Today, то UtcNow,
/// что сдвигало горизонты/НКД/дни-до-даты на день.
/// </summary>
public class BusinessClockTests
{
    [Fact]
    public void MoscowDate_LateUtcEvening_RollsToNextMoscowDay()
    {
        // 2026-06-26 23:30 UTC == 2026-06-27 02:30 MSK → бизнес-дата уже следующий день.
        var utc = new DateTime(2026, 6, 26, 23, 30, 0, DateTimeKind.Utc);

        BusinessClock.MoscowDate(utc).Should().Be(new DateOnly(2026, 6, 27));
    }

    [Fact]
    public void MoscowDate_Midday_SameDay()
    {
        var utc = new DateTime(2026, 6, 26, 9, 0, 0, DateTimeKind.Utc);

        BusinessClock.MoscowDate(utc).Should().Be(new DateOnly(2026, 6, 26));
    }
}
