using Bonds.Core.Models;
using Bonds.Infrastructure.Repositories;
using Bonds.IntegrationTests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Bonds.IntegrationTests;

/// <summary>
/// Round-trip тесты для ICouponScheduleRepository / IAmortizationScheduleRepository /
/// IOfferScheduleRepository — графики купонов/амортизации/оферт инструмента (plan/03 §D).
/// </summary>
[Collection("Integration")]
public class ScheduleRepositoriesTests
{
    private readonly TestWebApplicationFactory _factory;

    public ScheduleRepositoriesTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<ulong> CreateInstrumentAsync()
    {
        var repo = new InstrumentRepository(_factory.Database.ConnectionString);
        var isin = $"RU{Guid.NewGuid():N}".Substring(0, 12);
        return await repo.UpsertAsync(new Instrument
        {
            Isin = isin,
            FaceValue = 1000m,
            Currency = "RUB",
            CouponType = CouponType.Fixed,
            MaturityDate = new DateOnly(2031, 1, 1),
        });
    }

    [Fact]
    public async Task CouponSchedule_ReplaceForInstrument_RoundTrips_AndIsIdempotentOnReplay()
    {
        var instrumentId = await CreateInstrumentAsync();
        var repo = new CouponScheduleRepository(_factory.Database.ConnectionString);

        var schedule = new List<CouponSchedule>
        {
            new() { CouponDate = new DateOnly(2025, 1, 1), ValueRub = 35.5m, PeriodDays = 182, IsKnown = true },
            new() { CouponDate = new DateOnly(2025, 7, 1), ValueRub = null, PeriodDays = 182, IsKnown = false },
        };

        await repo.ReplaceForInstrumentAsync(instrumentId, schedule);

        var loaded = (await repo.GetByInstrumentIdAsync(instrumentId)).ToList();
        loaded.Should().HaveCount(2);
        loaded[0].ValueRub.Should().Be(35.5m);
        loaded[0].IsKnown.Should().BeTrue();
        loaded[1].ValueRub.Should().BeNull("флоатер за горизонтом известной ставки — купон не подставляется молча (spec §4.4)");
        loaded[1].IsKnown.Should().BeFalse();

        // Повторный вызов с тем же набором — не должен дублировать строки (идемпотентность обновления справочника).
        await repo.ReplaceForInstrumentAsync(instrumentId, schedule);
        var reloaded = await repo.GetByInstrumentIdAsync(instrumentId);
        reloaded.Should().HaveCount(2);
    }

    [Fact]
    public async Task AmortizationSchedule_ReplaceForInstrument_RoundTrips()
    {
        var instrumentId = await CreateInstrumentAsync();
        var repo = new AmortizationScheduleRepository(_factory.Database.ConnectionString);

        var schedule = new List<AmortizationSchedule>
        {
            new() { Date = new DateOnly(2026, 1, 1), AmountRub = 250m, IsKnown = true },
            new() { Date = new DateOnly(2027, 1, 1), AmountRub = 250m, IsKnown = true },
            // Audit(engine) E-1: строка с известной датой, но неизвестной суммой (MBS/ипотечный
            // агент) должна пройти через БД без потерь — не выбрасываться и не превращаться в
            // "реальный" 0.
            new() { Date = new DateOnly(2028, 1, 1), AmountRub = 0m, IsKnown = false },
        };

        await repo.ReplaceForInstrumentAsync(instrumentId, schedule);

        var loaded = (await repo.GetByInstrumentIdAsync(instrumentId)).ToList();
        loaded.Should().HaveCount(3);
        loaded.Where(a => a.IsKnown).Sum(a => a.AmountRub).Should().Be(500m);
        loaded.Single(a => a.Date == new DateOnly(2028, 1, 1)).IsKnown.Should().BeFalse();
    }

    [Fact]
    public async Task OfferSchedule_ReplaceForInstrument_RoundTrips_WithPutAndCall()
    {
        var instrumentId = await CreateInstrumentAsync();
        var repo = new OfferScheduleRepository(_factory.Database.ConnectionString);

        var schedule = new List<OfferSchedule>
        {
            new() { Date = new DateOnly(2026, 6, 1), OfferType = OfferType.Put, IsExecuted = false },
            new() { Date = new DateOnly(2024, 6, 1), OfferType = OfferType.Call, IsExecuted = true },
        };

        await repo.ReplaceForInstrumentAsync(instrumentId, schedule);

        var loaded = (await repo.GetByInstrumentIdAsync(instrumentId)).ToList();
        loaded.Should().HaveCount(2);
        loaded.Should().ContainSingle(o => o.OfferType == OfferType.Put && !o.IsExecuted);
        loaded.Should().ContainSingle(o => o.OfferType == OfferType.Call && o.IsExecuted);
    }
}
