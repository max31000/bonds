using Bonds.Core.Credentials;
using Bonds.Core.Execution;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Core.Policy;
using Bonds.Infrastructure.Connectors.TInvest;
using Bonds.Infrastructure.Execution;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Tinkoff.InvestApi;
using Tinkoff.InvestApi.V1;
using Xunit;
using Xunit.Abstractions;
using BrokerIds = Bonds.Core.Models.Brokers;

namespace Bonds.Tests.Execution;

/// <summary>
/// Задача 65, ОПЦИОНАЛЬНЫЙ прогон в песочнице T-Invest: реальный <see cref="TInvestOrderGateway"/> (контур sandbox) + <see cref="PlanExecutionEngine"/> на in-memory
/// репозиториях. Открыть sandbox-счёт → пополнить → план Approved (1 лот TMON@ по ask) → тики исполнителя до Done → закрыть счёт.
/// <para>
/// Запускается ТОЛЬКО при env <c>TINVEST_SANDBOX_TOKEN</c> (без него тест ничего не делает) и исключён из CI и pre-push фильтром
/// <c>Category!=Sandbox</c>. Ручной запуск: <c>TINVEST_SANDBOX_TOKEN=… dotnet test tests/Bonds.Tests --filter Category=Sandbox --logger "console;verbosity=detailed"</c>
/// в часы торгов. Лог прогона — в выводе теста.
/// </para>
/// </summary>
[Trait("Category", "Sandbox")]
public class TInvestSandboxTests
{
    private readonly ITestOutputHelper _out;

    public TInvestSandboxTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task SandboxPlan_ApprovedToDone()
    {
        var token = Environment.GetEnvironmentVariable("TINVEST_SANDBOX_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            _out.WriteLine("TINVEST_SANDBOX_TOKEN не задан — песочница пропущена.");
            return;
        }

        var sdk = InvestApiClientFactory.Create(new InvestApiSettings { AccessToken = token, AppName = "bonds-sandbox-test", Sandbox = true });

        // Чистый старт: шлюз требует, чтобы токен видел РОВНО один счёт.
        foreach (var stale in (await sdk.Sandbox.GetSandboxAccountsAsync(new GetAccountsRequest())).Accounts)
            await sdk.Sandbox.CloseSandboxAccountAsync(new CloseSandboxAccountRequest { AccountId = stale.Id });
        var accountId = (await sdk.Sandbox.OpenSandboxAccountAsync(new OpenSandboxAccountRequest())).AccountId;
        _out.WriteLine($"sandbox account …{accountId[^4..]} opened");
        try
        {
            await sdk.Sandbox.SandboxPayInAsync(new SandboxPayInRequest { AccountId = accountId, Amount = new MoneyValue { Currency = "rub", Units = 100_000 } });
            _out.WriteLine("pay-in 100 000 RUB");

            var found = await sdk.Instruments.FindInstrumentAsync(new FindInstrumentRequest { Query = "TMON@" });
            var tmon = found.Instruments.FirstOrDefault(i => i.Ticker == "TMON@" && i.ClassCode == "SPBRU") ?? found.Instruments.First(i => i.Ticker == "TMON@");
            _out.WriteLine($"instrument {tmon.Ticker} {tmon.ClassCode} uid {tmon.Uid}");

            var store = new Mock<IUserCredentialStore>();
            store.Setup(s => s.GetSecretResultAsync(1, CredentialProviders.TInvestTrade))
                .ReturnsAsync(new CredentialSecretResult(CredentialReadOutcome.Ok, new Dictionary<string, string> { ["token"] = token }));
            var gate = new Mock<IExecutionGateResolver>();
            gate.Setup(g => g.ResolveAsync(1)).ReturnsAsync(ExecutionGate.Evaluate(true, ExecutionModes.Live, true, false));
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["TInvest:Sandbox"] = "true" }).Build();
            var gateway = new TInvestOrderGateway(store.Object, gate.Object, config, new TInvestTradeClientCache(), NullLogger<TInvestOrderGateway>.Instance);

            var book = await gateway.GetOrderBookAsync(1, tmon.Uid);
            _out.WriteLine($"book bid {book.Bid} ask {book.Ask} at {book.TimestampUtc:O}");
            if (book.Ask is null)
            {
                _out.WriteLine("стакан пуст (вне часов торгов?) — прогон остановлен");
                return;
            }

            var plans = new InMemoryPlans();
            var orders = new InMemoryOrders();
            var plan = new TradePlan
            {
                UserId = 1, Rule = PlanRules.Manual, Trigger = "sandbox", Status = PlanStatuses.Approved, DryRun = false, CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(30), DecidedAt = DateTime.UtcNow, DecidedVia = DecidedVia.Web,
                Lines =
                [
                    new TradePlanLine
                    {
                        LineNo = 1, Broker = BrokerIds.TInvest, AccountId = 1, InstrumentKey = "TMON", Ticker = "TMON@", Side = PlanSides.Buy, QuantityLots = 1, LotSize = 1m,
                        LimitPrice = book.Ask.Value, EstAmount = book.Ask.Value, Currency = "RUB", Reason = "sandbox",
                    },
                ],
            };
            plans.Add(plan);

            var meta = new Mock<IInstrumentTradingMetaRepository>();
            meta.Setup(m => m.GetByKeysAsync(BrokerIds.TInvest, It.IsAny<IReadOnlyCollection<string>>())).ReturnsAsync(
            [
                new InstrumentTradingMeta
                {
                    Broker = BrokerIds.TInvest, InstrumentKey = "TMON", Uid = tmon.Uid, Ticker = "TMON@", ClassCode = tmon.ClassCode, Lot = 1, MinPriceIncrement = 0.01m,
                    Currency = "RUB", ApiTradeAvailable = true, BuyAvailable = true, SellAvailable = true,
                },
            ]);
            var accounts = new Mock<IAccountRepository>();
            accounts.Setup(a => a.GetByUserIdAndBrokerAsync(1, BrokerIds.TInvest)).ReturnsAsync([new Bonds.Core.Models.Account { Id = 1, UserId = 1, BrokerAccountId = accountId }]);
            var engine = new PlanExecutionEngine(
                plans, orders, meta.Object, accounts.Object, Mock.Of<IUserCredentialRepository>(), Mock.Of<ICapitalBasketRepository>(), gateway, gate.Object, new NullExecutionNotifier(),
                new NullPortfolioSyncTrigger(), Mock.Of<IOptionsMonitor<ExecutionOptions>>(m => m.CurrentValue == new ExecutionOptions { Enabled = true }),
                Options.Create(new ExecutionLimitsOptions()), Options.Create(new PolicyOptions()), NullLogger<PlanExecutionEngine>.Instance);

            for (var tick = 0; tick < 10 && plans[plan.Id].Status is PlanStatuses.Approved or PlanStatuses.Submitting; tick++)
            {
                var r = await engine.RunTickAsync(DateTime.UtcNow);
                _out.WriteLine($"tick {tick}: plan {plans[plan.Id].Status}, posted {r.OrdersPosted}, orders [{string.Join(", ", orders.All.Select(o => $"{o.Attempt}:{o.Status}:{o.LotsFilled}"))}]");
                await Task.Delay(TimeSpan.FromSeconds(3));
            }

            plans[plan.Id].Status.Should().Be(PlanStatuses.Done);
        }
        finally
        {
            await sdk.Sandbox.CloseSandboxAccountAsync(new CloseSandboxAccountRequest { AccountId = accountId });
            _out.WriteLine("sandbox account closed");
        }
    }
}
