using Bonds.Core.Models;
using Bonds.Infrastructure.Repositories;
using Bonds.IntegrationTests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Bonds.IntegrationTests;

/// <summary>
/// Round-trip тесты для IAccountRepository / IPositionRepository. Выборки скоупятся по
/// владельцу (UserId/AccountId) — критерий приёмки этапа 03.
/// </summary>
[Collection("Integration")]
public class AccountAndPositionRepositoriesTests
{
    private readonly TestWebApplicationFactory _factory;

    public AccountAndPositionRepositoriesTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<ulong> CreateUserAsync()
    {
        var repo = new UserRepository(_factory.Database.ConnectionString);
        var telegramId = Random.Shared.NextInt64(1, long.MaxValue);
        return await repo.CreateAsync(new User { TelegramId = telegramId, Username = "tester" });
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
    public async Task Account_Create_Then_GetById_RoundTrips_AndScopesToUser()
    {
        var userId = await CreateUserAsync();
        var otherUserId = await CreateUserAsync();
        var repo = new AccountRepository(_factory.Database.ConnectionString);

        var id = await repo.CreateAsync(new Account { UserId = userId, BrokerAccountId = "BA-1", Name = "Брокерский счёт" });

        var loaded = await repo.GetByIdAsync(id, userId);
        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("Брокерский счёт");

        var wrongOwner = await repo.GetByIdAsync(id, otherUserId);
        wrongOwner.Should().BeNull("выборка по счёту должна скоупиться по владельцу");

        var byUser = await repo.GetByUserIdAsync(userId);
        byUser.Should().ContainSingle(a => a.Id == id);
    }

    [Fact]
    public async Task Account_Update_Persists()
    {
        var userId = await CreateUserAsync();
        var repo = new AccountRepository(_factory.Database.ConnectionString);
        var id = await repo.CreateAsync(new Account { UserId = userId, Name = "Old name" });

        var account = await repo.GetByIdAsync(id, userId);
        account!.Name = "New name";
        account.BrokerAccountId = "BA-NEW";
        await repo.UpdateAsync(account);

        var reloaded = await repo.GetByIdAsync(id, userId);
        reloaded!.Name.Should().Be("New name");
        reloaded.BrokerAccountId.Should().Be("BA-NEW");
    }

    [Fact]
    public async Task Position_Upsert_Then_Read_RoundTrips_AndScopesToAccount()
    {
        var userId = await CreateUserAsync();
        var accountRepo = new AccountRepository(_factory.Database.ConnectionString);
        var accountId = await accountRepo.CreateAsync(new Account { UserId = userId, Name = "Main" });
        var otherAccountId = await accountRepo.CreateAsync(new Account { UserId = userId, Name = "Other" });

        var instrumentId = await CreateInstrumentAsync();
        var positionRepo = new PositionRepository(_factory.Database.ConnectionString);

        var positionId = await positionRepo.UpsertAsync(new Position
        {
            AccountId = accountId,
            InstrumentId = instrumentId,
            Quantity = 10,
            AvgPurchasePrice = 985.5m,
            Accrued = 12.3m,
        });

        var loaded = await positionRepo.GetByIdAsync(positionId, accountId);
        loaded.Should().NotBeNull();
        loaded!.Quantity.Should().Be(10);

        var fromOtherAccount = await positionRepo.GetByIdAsync(positionId, otherAccountId);
        fromOtherAccount.Should().BeNull("выборка по позиции должна скоупиться по счёту-владельцу");

        var byInstrument = await positionRepo.GetByAccountAndInstrumentAsync(accountId, instrumentId);
        byInstrument.Should().NotBeNull();
        byInstrument!.Id.Should().Be(positionId);
    }

    [Fact]
    public async Task Position_UpsertSameAccountInstrument_UpdatesInPlace_NoDuplicate()
    {
        var userId = await CreateUserAsync();
        var accountRepo = new AccountRepository(_factory.Database.ConnectionString);
        var accountId = await accountRepo.CreateAsync(new Account { UserId = userId, Name = "Main" });
        var instrumentId = await CreateInstrumentAsync();
        var positionRepo = new PositionRepository(_factory.Database.ConnectionString);

        var firstId = await positionRepo.UpsertAsync(new Position
        {
            AccountId = accountId,
            InstrumentId = instrumentId,
            Quantity = 10,
            AvgPurchasePrice = 1000m,
        });

        var secondId = await positionRepo.UpsertAsync(new Position
        {
            AccountId = accountId,
            InstrumentId = instrumentId,
            Quantity = 15,
            AvgPurchasePrice = 990m,
            DataIncomplete = true,
        });

        secondId.Should().Be(firstId);

        var all = await positionRepo.GetByAccountIdAsync(accountId);
        all.Should().ContainSingle(p => p.InstrumentId == instrumentId);

        var loaded = await positionRepo.GetByIdAsync(firstId, accountId);
        loaded!.Quantity.Should().Be(15);
        loaded.DataIncomplete.Should().BeTrue();
    }

    [Fact]
    public async Task Position_Delete_RemovesRow()
    {
        var userId = await CreateUserAsync();
        var accountRepo = new AccountRepository(_factory.Database.ConnectionString);
        var accountId = await accountRepo.CreateAsync(new Account { UserId = userId, Name = "Main" });
        var instrumentId = await CreateInstrumentAsync();
        var positionRepo = new PositionRepository(_factory.Database.ConnectionString);

        var id = await positionRepo.UpsertAsync(new Position { AccountId = accountId, InstrumentId = instrumentId, Quantity = 5 });
        await positionRepo.DeleteAsync(id, accountId);

        var loaded = await positionRepo.GetByIdAsync(id, accountId);
        loaded.Should().BeNull();
    }
}
