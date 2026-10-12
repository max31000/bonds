using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bonds.Core.Models;
using Bonds.Infrastructure.Repositories;
using Bonds.IntegrationTests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Bonds.IntegrationTests;

/// <summary>
/// Local user resolution under the shared mvv42 auth: the local <c>users</c> row is found or created by the
/// <c>tg</c> claim only (never by the auth service's <c>sub</c>), with the Account onboarding the old login did.
/// </summary>
[Collection("Integration")]
public class Mvv42ProvisioningTests
{
    private readonly TestWebApplicationFactory _factory;

    public Mvv42ProvisioningTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task FirstRequest_OfNewTelegramUser_ProvisionsLocalUserOnce_AndPrimaryAccountExists()
    {
        var telegramId = Random.Shared.NextInt64(1, long.MaxValue);
        var userRepo = new UserRepository(_factory.Database.ConnectionString);
        (await userRepo.GetByTelegramIdAsync(telegramId)).Should().BeNull();

        var token = Mvv42TestAuth.CreateToken(telegramId, authUserId: "777", name: "Иван Петров");
        var client = Mvv42TestAuth.WithToken(_factory.CreateClient(), token);

        // Parallel first requests: the gate + 1062 retry must still produce exactly one row.
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => client.GetAsync("/api/watchlist")));
        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);

        var user = await userRepo.GetByTelegramIdAsync(telegramId);
        user.Should().NotBeNull();
        user!.FirstName.Should().Be("Иван Петров");

        var accountRepo = new AccountRepository(_factory.Database.ConnectionString);
        (await accountRepo.GetPrimaryAccountIdAsync()).Should().NotBeNull();
    }

    [Fact]
    public async Task Onboarding_CreatesThePrimaryAccount_ForTheProvisionedUser_WhenNoneExists()
    {
        // Onboarding is global (single-user product): it creates the primary account only when none exists. The
        // container is shared, so both branches are asserted precisely; the unit test pins the empty-table case.
        var accountRepo = new AccountRepository(_factory.Database.ConnectionString);
        var userRepo = new UserRepository(_factory.Database.ConnectionString);

        var telegramId = Random.Shared.NextInt64(1, long.MaxValue);
        var client = Mvv42TestAuth.WithToken(_factory.CreateClient(), Mvv42TestAuth.CreateToken(telegramId, authUserId: "888"));
        var hadPrimary = await accountRepo.GetPrimaryAccountIdAsync() is not null;

        (await client.GetAsync("/api/watchlist")).StatusCode.Should().Be(HttpStatusCode.OK);

        var user = await userRepo.GetByTelegramIdAsync(telegramId);
        user.Should().NotBeNull();
        var own = (await accountRepo.GetByUserIdAsync(user!.Id)).ToList();
        if (hadPrimary)
        {
            own.Should().BeEmpty("onboarding is global: an existing primary account is reused, none is created");
        }
        else
        {
            own.Should().ContainSingle("onboarding creates the primary account for the provisioned user");
            (await accountRepo.GetPrimaryAccountIdAsync()).Should().Be(own[0].Id);
        }
    }

    [Fact]
    public async Task ChangedTelegramName_IsWrittenToTheLocalUser()
    {
        var userRepo = new UserRepository(_factory.Database.ConnectionString);
        var telegramId = Random.Shared.NextInt64(1, long.MaxValue);
        await userRepo.CreateAsync(new User { TelegramId = telegramId, FirstName = "Старое Имя" });

        var client = Mvv42TestAuth.WithToken(_factory.CreateClient(), Mvv42TestAuth.CreateToken(telegramId, name: "Новое Имя"));
        (await client.GetAsync("/api/watchlist")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await userRepo.GetByTelegramIdAsync(telegramId))!.FirstName.Should().Be("Новое Имя");
    }

    [Fact]
    public async Task LocalUser_IsResolvedByTelegramId_NotByAuthSub()
    {
        var userRepo = new UserRepository(_factory.Database.ConnectionString);
        var settingsRepo = new UserSettingsRepository(_factory.Database.ConnectionString);

        // Decoy: a local user whose users.id equals the token's sub. If sub were used as the local id,
        // the response would carry the decoy's settings.
        var decoyId = await userRepo.CreateAsync(new User { TelegramId = Random.Shared.NextInt64(1, long.MaxValue) });
        await settingsRepo.UpsertAsync(new UserSettings { UserId = decoyId, UpcomingEventDaysThreshold = 11 });

        var telegramId = Random.Shared.NextInt64(1, long.MaxValue);
        var ownerId = await userRepo.CreateAsync(new User { TelegramId = telegramId });
        await settingsRepo.UpsertAsync(new UserSettings { UserId = ownerId, UpcomingEventDaysThreshold = 23 });
        ownerId.Should().NotBe(decoyId);

        var token = Mvv42TestAuth.CreateToken(telegramId, authUserId: decoyId.ToString(CultureInfo.InvariantCulture));
        var client = Mvv42TestAuth.WithToken(_factory.CreateClient(), token);

        var response = await client.GetAsync("/api/settings");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("upcomingEventDaysThreshold").GetInt32().Should().Be(23);
    }
}
