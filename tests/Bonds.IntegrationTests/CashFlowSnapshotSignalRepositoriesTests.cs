using Bonds.Core.Models;
using Bonds.Infrastructure.Repositories;
using Bonds.IntegrationTests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Bonds.IntegrationTests;

/// <summary>
/// Round-trip тесты для IProjectedCashFlowRepository, IPortfolioValueSnapshotRepository,
/// ISignalRepository и ITargetAllocationRepository (plan/03 §D).
/// </summary>
[Collection("Integration")]
public class CashFlowSnapshotSignalRepositoriesTests
{
    private readonly TestWebApplicationFactory _factory;

    public CashFlowSnapshotSignalRepositoriesTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<(ulong AccountId, ulong InstrumentId, ulong PositionId)> SeedPositionAsync()
    {
        var userRepo = new UserRepository(_factory.Database.ConnectionString);
        var userId = await userRepo.CreateAsync(new User { TelegramId = Random.Shared.NextInt64(1, long.MaxValue) });

        var accountRepo = new AccountRepository(_factory.Database.ConnectionString);
        var accountId = await accountRepo.CreateAsync(new Account { UserId = userId, Name = "Main" });

        var instrumentRepo = new InstrumentRepository(_factory.Database.ConnectionString);
        var isin = $"RU{Guid.NewGuid():N}".Substring(0, 12);
        var instrumentId = await instrumentRepo.UpsertAsync(new Instrument
        {
            Isin = isin,
            FaceValue = 1000m,
            Currency = "RUB",
            CouponType = CouponType.Fixed,
            MaturityDate = new DateOnly(2031, 1, 1),
        });

        var positionRepo = new PositionRepository(_factory.Database.ConnectionString);
        var positionId = await positionRepo.UpsertAsync(new Position
        {
            AccountId = accountId,
            InstrumentId = instrumentId,
            Quantity = 10,
            AvgPurchasePrice = 1000m,
        });

        return (accountId, instrumentId, positionId);
    }

    [Fact]
    public async Task ProjectedCashFlow_ReplaceForPosition_RoundTrips_AndScopesByAccount()
    {
        var (accountId, instrumentId, positionId) = await SeedPositionAsync();
        var repo = new ProjectedCashFlowRepository(_factory.Database.ConnectionString);

        var flows = new List<ProjectedCashFlow>
        {
            new() { InstrumentId = instrumentId, Date = new DateOnly(2025, 7, 1), FlowType = CashFlowType.Coupon, GrossRub = 350, TaxRub = 45.5m, NetRub = 304.5m, IsEstimated = false },
            new() { InstrumentId = instrumentId, Date = new DateOnly(2031, 1, 1), FlowType = CashFlowType.Redemption, GrossRub = 10000, TaxRub = 0, NetRub = 10000, IsEstimated = false },
        };

        await repo.ReplaceForPositionAsync(positionId, flows);

        var byPosition = (await repo.GetByPositionIdAsync(positionId)).ToList();
        byPosition.Should().HaveCount(2);

        var byAccount = await repo.GetByAccountIdAsync(accountId);
        byAccount.Should().HaveCount(2);

        var filtered = await repo.GetByAccountIdAsync(accountId, from: new DateOnly(2025, 1, 1), to: new DateOnly(2025, 12, 31));
        filtered.Should().ContainSingle(f => f.FlowType == CashFlowType.Coupon);
    }

    [Fact]
    public async Task PortfolioValueSnapshot_Upsert_Then_GetLatest_RoundTrips_AndIsIdempotent()
    {
        var (accountId, _, _) = await SeedPositionAsync();
        var repo = new PortfolioValueSnapshotRepository(_factory.Database.ConnectionString);

        await repo.UpsertAsync(new PortfolioValueSnapshot { AccountId = accountId, AsOf = new DateOnly(2025, 6, 1), MarketValueRub = 100_000m, InvestedRub = 95_000m, XirrToDate = 0.12m });
        await repo.UpsertAsync(new PortfolioValueSnapshot { AccountId = accountId, AsOf = new DateOnly(2025, 6, 2), MarketValueRub = 101_000m, InvestedRub = 95_000m, XirrToDate = 0.13m });

        // Повторный снимок за тот же день (пересчёт после force-обновления) — обновляет, не дублирует.
        await repo.UpsertAsync(new PortfolioValueSnapshot { AccountId = accountId, AsOf = new DateOnly(2025, 6, 2), MarketValueRub = 102_500m, InvestedRub = 95_000m, XirrToDate = 0.14m });

        var latest = await repo.GetLatestAsync(accountId);
        latest!.AsOf.Should().Be(new DateOnly(2025, 6, 2));
        latest.MarketValueRub.Should().Be(102_500m);

        var all = await repo.GetByAccountIdAsync(accountId);
        all.Should().HaveCount(2, "повторный снимок за тот же день не должен дублировать строку");
    }

    [Fact]
    public async Task Signal_Create_Then_GetByAccount_And_MarkRead_RoundTrips()
    {
        var (accountId, instrumentId, positionId) = await SeedPositionAsync();
        var repo = new SignalRepository(_factory.Database.ConnectionString);

        var id = await repo.CreateAsync(new Signal
        {
            AccountId = accountId,
            Type = SignalType.UpcomingOffer,
            Severity = SignalSeverity.Critical,
            PositionId = positionId,
            InstrumentId = instrumentId,
            SuggestedAction = "Подать put-оферту через брокера до 2026-06-01",
            Date = new DateOnly(2026, 5, 1),
            IsRead = false,
        });

        var unread = await repo.GetByAccountIdAsync(accountId, isRead: false);
        unread.Should().ContainSingle(s => s.Id == id);

        await repo.MarkReadAsync(id, accountId);

        var stillUnread = await repo.GetByAccountIdAsync(accountId, isRead: false);
        stillUnread.Should().BeEmpty();

        var read = await repo.GetByAccountIdAsync(accountId, isRead: true);
        read.Should().ContainSingle(s => s.Id == id);
    }

    [Fact]
    public async Task TargetAllocation_Create_Update_Delete_RoundTrips()
    {
        var (accountId, _, _) = await SeedPositionAsync();
        var repo = new TargetAllocationRepository(_factory.Database.ConnectionString);

        var id = await repo.CreateAsync(new TargetAllocation
        {
            AccountId = accountId,
            Issuer = "Минфин РФ",
            MaxConcentrationPercent = 30m,
        });

        var all = await repo.GetByAccountIdAsync(accountId);
        all.Should().ContainSingle(a => a.Id == id && a.Issuer == "Минфин РФ");

        var toUpdate = all.Single(a => a.Id == id);
        toUpdate.MaxConcentrationPercent = 25m;
        await repo.UpdateAsync(toUpdate);

        var afterUpdate = (await repo.GetByAccountIdAsync(accountId)).Single(a => a.Id == id);
        afterUpdate.MaxConcentrationPercent.Should().Be(25m);

        await repo.DeleteAsync(id, accountId);
        var afterDelete = await repo.GetByAccountIdAsync(accountId);
        afterDelete.Should().BeEmpty();
    }
}
