using Bonds.Core.Models;
using Bonds.Infrastructure.Repositories;
using Bonds.IntegrationTests.Infrastructure;
using FluentAssertions;
using MySqlConnector;
using Xunit;

namespace Bonds.IntegrationTests;

/// <summary>
/// Round-trip и идемпотентность IOperationRepository — журнал операций, истина для XIRR
/// (spec §5/§6.9). Идемпотентный upsert по ExternalId — ключевой критерий приёмки этапа 03
/// (повторный синк той же брокерской операции не создаёт дубль).
/// </summary>
[Collection("Integration")]
public class OperationRepositoryTests
{
    private readonly TestWebApplicationFactory _factory;

    public OperationRepositoryTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<ulong> CreateAccountAsync()
    {
        var userRepo = new UserRepository(_factory.Database.ConnectionString);
        var userId = await userRepo.CreateAsync(new User { TelegramId = Random.Shared.NextInt64(1, long.MaxValue) });

        var accountRepo = new AccountRepository(_factory.Database.ConnectionString);
        return await accountRepo.CreateAsync(new Account { UserId = userId, Name = "Main" });
    }

    [Fact]
    public async Task UpsertByExternalId_Then_GetByExternalId_RoundTrips()
    {
        var accountId = await CreateAccountAsync();
        var repo = new OperationRepository(_factory.Database.ConnectionString);
        var externalId = $"tinvest-{Guid.NewGuid():N}";

        var id = await repo.UpsertByExternalIdAsync(new Operation
        {
            AccountId = accountId,
            Type = OperationType.Buy,
            Date = new DateTime(2025, 1, 10),
            AmountRub = -10_000m,
            Quantity = 10,
            ExternalId = externalId,
        });

        id.Should().BeGreaterThan(0);

        var loaded = await repo.GetByExternalIdAsync(externalId);
        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(id);
        loaded.Type.Should().Be(OperationType.Buy);
        loaded.AmountRub.Should().Be(-10_000m);
    }

    [Fact]
    public async Task UpsertByExternalId_ReplayedSync_DoesNotDuplicate_AndUpdatesFields()
    {
        var accountId = await CreateAccountAsync();
        var repo = new OperationRepository(_factory.Database.ConnectionString);
        var externalId = $"tinvest-{Guid.NewGuid():N}";

        var firstId = await repo.UpsertByExternalIdAsync(new Operation
        {
            AccountId = accountId,
            Type = OperationType.Coupon,
            Date = new DateTime(2025, 2, 1),
            AmountRub = 350m,
            ExternalId = externalId,
        });

        // Повторный синк той же брокерской операции — например, брокер досчитал/скорректировал сумму.
        var secondId = await repo.UpsertByExternalIdAsync(new Operation
        {
            AccountId = accountId,
            Type = OperationType.Coupon,
            Date = new DateTime(2025, 2, 1),
            AmountRub = 351.25m,
            ExternalId = externalId,
        });

        secondId.Should().Be(firstId, "повторный синк по тому же ExternalId не должен создавать новую строку");

        var all = await repo.GetByAccountIdAsync(accountId);
        all.Count(o => o.ExternalId == externalId).Should().Be(1);

        var loaded = await repo.GetByExternalIdAsync(externalId);
        loaded!.AmountRub.Should().Be(351.25m);
    }

    [Fact]
    public async Task UpsertManyByExternalId_BatchIsIdempotentAcrossReplays()
    {
        var accountId = await CreateAccountAsync();
        var repo = new OperationRepository(_factory.Database.ConnectionString);

        var ops = Enumerable.Range(0, 5).Select(i => new Operation
        {
            AccountId = accountId,
            Type = OperationType.Fee,
            Date = new DateTime(2025, 3, 1).AddDays(i),
            AmountRub = -1.5m,
            ExternalId = $"batch-{i}-{Guid.NewGuid():N}",
        }).ToList();

        var firstRun = await repo.UpsertManyByExternalIdAsync(ops);
        firstRun.Should().Be(5);

        // Повторный батч-синк того же набора операций (имитация повторного запуска синка).
        var secondRun = await repo.UpsertManyByExternalIdAsync(ops);
        secondRun.Should().Be(5);

        var all = await repo.GetByAccountIdAsync(accountId);
        all.Count(o => ops.Any(p => p.ExternalId == o.ExternalId)).Should().Be(5, "повторный батч-синк не должен дублировать операции");
    }

    [Fact]
    public async Task DuplicateExternalId_ViaRawInsert_ViolatesUniqueConstraint()
    {
        var accountId = await CreateAccountAsync();
        var externalId = $"raw-{Guid.NewGuid():N}";

        await using var conn = new MySqlConnection(_factory.Database.ConnectionString);
        await conn.OpenAsync();

        const string insertSql = @"
            INSERT INTO operations (account_id, type, date, amount_rub, external_id)
            VALUES (@AccountId, 'Fee', '2025-01-01', -1, @ExternalId)";

        await using (var cmd = new MySqlCommand(insertSql, conn))
        {
            cmd.Parameters.AddWithValue("@AccountId", accountId);
            cmd.Parameters.AddWithValue("@ExternalId", externalId);
            await cmd.ExecuteNonQueryAsync();
        }

        Func<Task> act = async () =>
        {
            await using var cmd = new MySqlCommand(insertSql, conn);
            cmd.Parameters.AddWithValue("@AccountId", accountId);
            cmd.Parameters.AddWithValue("@ExternalId", externalId);
            await cmd.ExecuteNonQueryAsync();
        };

        await act.Should().ThrowAsync<MySqlException>();
    }

    [Fact]
    public async Task GetByAccountId_FiltersByDateRange()
    {
        var accountId = await CreateAccountAsync();
        var repo = new OperationRepository(_factory.Database.ConnectionString);

        await repo.UpsertByExternalIdAsync(new Operation { AccountId = accountId, Type = OperationType.Buy, Date = new DateTime(2024, 1, 1), AmountRub = -100, ExternalId = $"a-{Guid.NewGuid():N}" });
        await repo.UpsertByExternalIdAsync(new Operation { AccountId = accountId, Type = OperationType.Coupon, Date = new DateTime(2025, 1, 1), AmountRub = 10, ExternalId = $"b-{Guid.NewGuid():N}" });
        await repo.UpsertByExternalIdAsync(new Operation { AccountId = accountId, Type = OperationType.Coupon, Date = new DateTime(2026, 1, 1), AmountRub = 10, ExternalId = $"c-{Guid.NewGuid():N}" });

        var filtered = await repo.GetByAccountIdAsync(accountId, from: new DateOnly(2025, 1, 1), to: new DateOnly(2025, 12, 31));

        filtered.Should().ContainSingle();
        filtered.Single().Date.Year.Should().Be(2025);
    }
}
