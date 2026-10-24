using System.Collections.Concurrent;
using Bonds.Core.Credentials;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Infrastructure.Connectors.Moex;
using Bonds.Infrastructure.Connectors.TInvest;
using Bonds.Infrastructure.Scheduling;
using Bonds.Infrastructure.Services;
using Bonds.Infrastructure.Sync;
using Bonds.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using Xunit;

namespace Bonds.IntegrationTests;

/// <summary>Своя БД (отдельный контейнер): тесту нужно управлять тем, какой пользователь «первый» (users.id) и чей счёт T-Invest — основной (первый accounts.id).</summary>
[CollectionDefinition("SyncIsolation")]
public class SyncIsolationCollection : ICollectionFixture<TestWebApplicationFactory> { }

/// <summary>
/// T-67E (minor 1) на реальном MySQL и реальном <see cref="TInvestTokenProvider"/>: цикл синка с двумя пользователями. «Основной пользователь» провайдера токена
/// (первый users.id, A) и владелец основного счёта T-Invest (первый accounts.id, B) — РАЗНЫЕ. Путь владельца обязан синкать счёт B токеном B (UseUser),
/// путь остальных пользователей — счёт A токеном A; данные не смешиваются. Брокер подменён двойником, который отвечает ПО ТОКЕНУ скоупа.
/// </summary>
[Collection("SyncIsolation")]
public class SyncTwoUsersIsolationTests
{
    private readonly TestWebApplicationFactory _factory;

    public SyncTwoUsersIsolationTests(TestWebApplicationFactory factory) => _factory = factory;

    private string Conn => _factory.Database.ConnectionString;

    [Fact]
    public async Task OwnerAndOtherUser_EachAccountSyncedWithItsOwnersToken_NoMixing()
    {
        var calls = new ConcurrentBag<(string? Token, string Account)>();
        var cashByToken = new Dictionary<string, decimal> { ["tok-A"] = 111m, ["tok-B"] = 222m };
        var host = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.RemoveAll<ITInvestPortfolioClient>();
            services.AddScoped(sp =>
            {
                var tokens = sp.GetRequiredService<ITInvestTokenProvider>();
                var client = new Mock<ITInvestPortfolioClient> { DefaultValue = DefaultValue.Empty };
                client.Setup(c => c.GetPortfolioAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .Returns(async (string account, CancellationToken ct) =>
                    {
                        var token = await tokens.GetTokenAsync(ct);
                        calls.Add((token, account));
                        return new TInvestPortfolio { Bonds = [], CashRub = token is not null && cashByToken.TryGetValue(token, out var c) ? c : -1m, TotalPortfolioRub = 0m };
                    });
                client.Setup(c => c.GetOperationsAsync(It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
                return client.Object;
            });

            // Сети нет: MOEX — пустой двойник, необязательные шаги цикла (риск-уровни, фонды, метаданные, курсы, Freedom, правила) выключены.
            services.RemoveAll<IMoexIssClient>();
            services.AddScoped(_ => new Mock<IMoexIssClient> { DefaultValue = DefaultValue.Empty }.Object);
            services.RemoveAll<InstrumentRiskLevelRefresher>();
            services.RemoveAll<FundMarketDataRefresher>();
            services.RemoveAll<TradingMetaRefresher>();
            services.RemoveAll<CurrencyRateRefresher>();
            services.RemoveAll<FreedomSyncService>();
            services.RemoveAll<Bonds.Infrastructure.Policy.IPolicyCycleRunner>();
        }));

        using var seedScope = host.Services.CreateScope();
        var users = seedScope.ServiceProvider.GetRequiredService<IUserRepository>();
        var accounts = seedScope.ServiceProvider.GetRequiredService<IAccountRepository>();
        var store = seedScope.ServiceProvider.GetRequiredService<IUserCredentialStore>();

        // A — «основной пользователь» провайдера (первый users.id); B — создан позже, но его счёт T-Invest — первый (основной счёт цикла).
        var userA = await users.GetPrimaryUserIdAsync() ?? await users.CreateAsync(new User { TelegramId = Random.Shared.NextInt64(1, long.MaxValue) });
        var userB = await users.CreateAsync(new User { TelegramId = Random.Shared.NextInt64(1, long.MaxValue) });
        var accountB = await accounts.CreateAsync(new Account { UserId = userB, Broker = Brokers.TInvest, BrokerAccountId = "BA-B", Name = "B" });
        var accountA = await accounts.CreateAsync(new Account { UserId = userA, Broker = Brokers.TInvest, BrokerAccountId = "BA-A", Name = "A" });
        (await users.GetPrimaryUserIdAsync()).Should().Be(userA, "предусловие: A — первый пользователь");
        (await accounts.GetPrimaryAccountIdAsync()).Should().Be(accountB, "предусловие: основной счёт — счёт B");
        await store.SaveAsync(userA, CredentialProviders.TInvestRead, new Dictionary<string, string> { ["token"] = "tok-A" }, "ok-A", null);
        await store.SaveAsync(userB, CredentialProviders.TInvestRead, new Dictionary<string, string> { ["token"] = "tok-B" }, "ok-B", null);

        await host.Services.GetRequiredService<ISyncCycleRunner>().RunCycleAsync();

        calls.Should().Contain(("tok-B", "BA-B"), "путь владельца: счёт B — токеном B");
        calls.Should().Contain(("tok-A", "BA-A"), "путь остальных: счёт A — токеном A");
        calls.Should().NotContain(c => (c.Account == "BA-B" && c.Token != "tok-B") || (c.Account == "BA-A" && c.Token != "tok-A"), "токен и счёт — всегда одного пользователя");
        (await accounts.GetByIdAsync(accountB))!.CashRub.Should().Be(222m);
        (await accounts.GetByIdAsync(accountA))!.CashRub.Should().Be(111m);
    }
}
