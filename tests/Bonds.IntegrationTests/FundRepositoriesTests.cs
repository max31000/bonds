using Bonds.Core.Models;
using Bonds.Infrastructure.Repositories;
using Bonds.IntegrationTests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Bonds.IntegrationTests;

/// <summary>Задача 51: репозитории фондов на реальном MySQL — идемпотентность upsert, изоляция счетов, единицы.</summary>
[Collection("Integration")]
public class FundRepositoriesTests
{
    private readonly TestWebApplicationFactory _factory;

    public FundRepositoriesTests(TestWebApplicationFactory factory) => _factory = factory;

    private string Conn => _factory.Database.ConnectionString;

    private async Task<ulong> CreateAccountAsync()
    {
        var userId = await new UserRepository(Conn).CreateAsync(new User { TelegramId = Random.Shared.NextInt64(1, long.MaxValue) });
        return await new AccountRepository(Conn).CreateAsync(new Account { UserId = userId, Name = "Main" });
    }

    private async Task<ulong> CreateFundAsync(string? suffix = null)
    {
        suffix ??= Guid.NewGuid().ToString("N")[..10];
        return await new FundRepository(Conn).UpsertAsync(new Fund
        {
            Figi = $"FIGI{suffix}", Isin = $"RU{suffix}", Ticker = "TPAY", Secid = "TPAY", Name = "Пассивный доход",
            FocusType = "fixed_income", TotalExpenseFraction = 0.009m,
        });
    }

    [Fact]
    public async Task Fund_UpsertByFigi_IsIdempotent_AndKeepsUserSettings()
    {
        var repo = new FundRepository(Conn);
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var id = await CreateFundAsync(suffix);
        await repo.UpdateSettingsAsync(id, FundForecastMethod.IndexYield, "RGBI");

        var id2 = await repo.UpsertAsync(new Fund { Figi = $"FIGI{suffix}", Isin = $"RU{suffix}", Name = "Новое имя", TotalExpenseFraction = 0.0095m });

        id2.Should().Be(id);
        var loaded = await repo.GetByFigiAsync($"FIGI{suffix}");
        loaded!.Name.Should().Be("Новое имя");
        loaded.TotalExpenseFraction.Should().Be(0.0095m);
        loaded.ForecastMethod.Should().Be(FundForecastMethod.IndexYield, "суточный рефреш справочника не сбрасывает настройки пользователя");
        loaded.BenchmarkIndex.Should().Be("RGBI");
        (await repo.GetByIsinAsync($"RU{suffix}"))!.Id.Should().Be(id);
        (await repo.GetByIdAsync(id))!.Figi.Should().Be($"FIGI{suffix}");
    }

    [Fact]
    public async Task FundPosition_Upsert_ThenDeleteAllExcept_DoesNotTouchOtherAccount()
    {
        var accountA = await CreateAccountAsync();
        var accountB = await CreateAccountAsync();
        var fund1 = await CreateFundAsync();
        var fund2 = await CreateFundAsync();
        var repo = new FundPositionRepository(Conn);

        foreach (var acc in new[] { accountA, accountB })
        {
            await repo.UpsertAsync(new FundPosition { AccountId = acc, FundId = fund1, Quantity = 10, AvgPurchasePriceRub = 100.5m, CurrentPriceRub = 101.25m, PriceAsOf = DateTime.UtcNow });
            await repo.UpsertAsync(new FundPosition { AccountId = acc, FundId = fund2, Quantity = 5, AvgPurchasePriceRub = 50m });
        }

        // Повторный upsert — не дубль, обновляет поля.
        await repo.UpsertAsync(new FundPosition { AccountId = accountA, FundId = fund1, Quantity = 12, AvgPurchasePriceRub = 100.75m, CurrentPriceRub = 102m });
        var a = await repo.GetByAccountAsync(accountA);
        a.Should().HaveCount(2);
        a.Single(p => p.FundId == fund1).Quantity.Should().Be(12m);
        a.Single(p => p.FundId == fund1).AvgPurchasePriceRub.Should().Be(100.75m);

        var deleted = await repo.DeleteAllExceptAsync(accountA, new[] { fund1 });

        deleted.Should().Be(1);
        (await repo.GetByAccountAsync(accountA)).Select(p => p.FundId).Should().Equal(fund1);
        (await repo.GetByAccountAsync(accountB)).Should().HaveCount(2, "чужой счёт не затронут");

        (await repo.DeleteAllExceptAsync(accountA, Array.Empty<ulong>())).Should().Be(1);
        (await repo.GetByAccountAsync(accountB)).Should().HaveCount(2);
    }

    [Fact]
    public async Task FundOperation_UpsertByExternalId_IsIdempotent_AndPreservesSignAndNullFund()
    {
        var accountId = await CreateAccountAsync();
        var fundId = await CreateFundAsync();
        var repo = new FundOperationRepository(Conn);
        var ext = $"op-{Guid.NewGuid():N}";
        var ext2 = $"op-{Guid.NewGuid():N}";

        FundOperation Op(string e, FundOperationType t, decimal amount, ulong? fund) => new()
        {
            AccountId = accountId, FundId = fund, Type = t, Date = new DateTime(2026, 10, 1, 12, 0, 0), AmountRub = amount, Quantity = 10, ExternalId = e,
        };

        await repo.UpsertManyByExternalIdAsync([Op(ext, FundOperationType.Buy, -1015.5m, fundId), Op(ext2, FundOperationType.Distribution, 9.25m, null)]);
        await repo.UpsertManyByExternalIdAsync([Op(ext, FundOperationType.Buy, -1015.5m, fundId), Op(ext2, FundOperationType.Distribution, 9.5m, fundId)]);

        var all = await repo.GetByAccountAsync(accountId);
        all.Should().HaveCount(2, "повторный синк не создаёт дублей");
        all.Single(o => o.ExternalId == ext).AmountRub.Should().Be(-1015.5m);
        all.Single(o => o.ExternalId == ext).Type.Should().Be(FundOperationType.Buy);
        all.Single(o => o.ExternalId == ext2).Type.Should().Be(FundOperationType.Distribution);
        all.Single(o => o.ExternalId == ext2).AmountRub.Should().Be(9.5m);
        (await repo.GetByFundAsync(accountId, fundId)).Should().HaveCount(2);
    }

    [Fact]
    public async Task FundOperation_FundDeleted_KeepsOperationWithNullFund()
    {
        var accountId = await CreateAccountAsync();
        var fundId = await CreateFundAsync();
        var ops = new FundOperationRepository(Conn);
        await ops.UpsertManyByExternalIdAsync([new FundOperation { AccountId = accountId, FundId = fundId, Type = FundOperationType.Sell, Date = DateTime.UtcNow.Date, AmountRub = 100m, ExternalId = $"op-{Guid.NewGuid():N}" }]);

        await using var conn = new MySqlConnector.MySqlConnection(Conn);
        await conn.OpenAsync();
        await Dapper.SqlMapper.ExecuteAsync(conn, "DELETE FROM funds WHERE id = @Id", new { Id = fundId });

        var left = await ops.GetByAccountAsync(accountId);
        left.Should().ContainSingle().Which.FundId.Should().BeNull("ON DELETE SET NULL: деньги не теряются");
    }

    [Fact]
    public async Task FundMarketData_PricesDistributionsIndex_UpsertIdempotent_AndRangeQueries()
    {
        var fundId = await CreateFundAsync();
        var repo = new FundMarketDataRepository(Conn);
        var d0 = new DateOnly(2026, 9, 1);

        await repo.UpsertPricesAsync(fundId, [new(d0, 100.5m), new(d0.AddDays(1), 100.75m)]);
        await repo.UpsertPricesAsync(fundId, [new(d0.AddDays(1), 100.8m), new(d0.AddDays(2), 101m)]);

        var prices = await repo.GetPricesAsync(fundId, d0, d0.AddDays(10));
        prices.Select(p => p.CloseRub).Should().Equal(100.5m, 100.8m, 101m);
        (await repo.GetLastPriceDateAsync(fundId)).Should().Be(d0.AddDays(2));
        (await repo.GetPricesAsync(fundId, d0.AddDays(1), d0.AddDays(1))).Should().ContainSingle();

        await repo.UpsertDistributionsAsync(fundId, [new(d0, d0.AddDays(5), 0.8m)]);
        await repo.UpsertDistributionsAsync(fundId, [new(d0, d0.AddDays(6), 0.85m), new(d0.AddMonths(1), null, 0.9m)]);
        var dist = await repo.GetDistributionsAsync(fundId);
        dist.Should().HaveCount(2);
        dist[0].AmountPerShareRub.Should().Be(0.85m);
        dist[0].PaymentDate.Should().Be(d0.AddDays(6));
        dist[1].PaymentDate.Should().BeNull();

        var code = $"IDX{Guid.NewGuid():N}"[..16];
        await repo.UpsertIndexHistoryAsync(code, [new(d0, 111.2m, 0.1608m, 4.51m), new(d0.AddDays(1), 111.3m, null, null)]);
        await repo.UpsertIndexHistoryAsync(code, [new(d0.AddDays(1), 111.4m, 0.161m, 4.5m)]);
        var latest = await repo.GetLatestIndexPointAsync(code);
        latest!.TradeDate.Should().Be(d0.AddDays(1));
        latest.YieldFraction.Should().Be(0.161m);
        (await repo.GetLastIndexDateAsync(code)).Should().Be(d0.AddDays(1));
        (await repo.GetLatestIndexPointAsync("NOPE")).Should().BeNull();
    }
}
