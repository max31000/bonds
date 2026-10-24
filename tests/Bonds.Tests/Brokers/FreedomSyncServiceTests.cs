using Bonds.Core.Credentials;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Infrastructure.Connectors.Freedom;
using Bonds.Infrastructure.Connectors.Freedom.Models;
using Bonds.Infrastructure.Sync;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Bonds.Tests.Brokers;

/// <summary>Задача 60: синк Freedom на фейках — счёт, позиции/деньги, журнал, сверка (SyncGuards), изоляция сбоев, ключи.</summary>
[Collection(FreedomSyncStaticCollection.Name)]
public class FreedomSyncServiceTests
{
    private const string Pub = "PUBKEY-1234567890";
    private const string Priv = "PRIVKEY-SECRET-XYZ";

    private readonly Mock<IFreedomApiClient> _api = new();
    private readonly Mock<IUserCredentialStore> _store = new();
    private readonly FakeAccounts _accounts = new();
    private readonly FakePositions _positions = new();
    private readonly FakeCash _cash = new();
    private readonly FakeOperations _ops = new();
    private readonly CapturingLogger<FreedomSyncService> _log = new();

    public FreedomSyncServiceTests()
    {
        FreedomSyncService.EmptyPositionsSeen.Clear();
        _store.Setup(s => s.ListUserIdsWithValidAsync("freedom")).ReturnsAsync(new ulong[] { 1 });
        _store.Setup(s => s.GetSecretResultAsync(1, "freedom")).ReturnsAsync(Secret());
        _api.Setup(a => a.GetUserInfoAsync(It.IsAny<FreedomCredentials>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FreedomUserInfo("100001", 1431, "Standard"));
        SetupPortfolio(Portfolio());
        _api.Setup(a => a.GetTradesAsync(It.IsAny<FreedomCredentials>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _api.Setup(a => a.GetCashFlowsAsync(It.IsAny<FreedomCredentials>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
    }

    private FreedomSyncService Create() =>
        new(_api.Object, _store.Object, _accounts, _positions, _cash, _ops, _log);

    private static CredentialSecretResult Secret() =>
        new(CredentialReadOutcome.Ok, new Dictionary<string, string> { ["publicKey"] = Pub, ["privateKey"] = Priv });

    private void SetupPortfolio(FreedomPortfolio p) =>
        _api.Setup(a => a.GetPortfolioAsync(It.IsAny<FreedomCredentials>(), It.IsAny<CancellationToken>())).ReturnsAsync(p);

    private static FreedomPortfolio Portfolio() => new(
        [new FreedomCashBalance("USD", 4.77m), new FreedomCashBalance("EUR", 88.57m), new FreedomCashBalance("RUR", 0m), new FreedomCashBalance("KZT", 0m)],
        [
            new FreedomPosition("LUV.US", "Southwest Airlines", 13m, "USD", 40.3m, 41.56m, 540.28m),
            new FreedomPosition("ROBO.EU", "L&G ROBO Global Robotics and Automation UCITS ETF", 3m, "EUR", 24.4075m, 27.07m, 81.21m),
        ]);

    [Fact]
    public async Task Sync_CreatesFreedomAccount_AndStoresPositionsAndCash()
    {
        var result = await Create().SyncAllAsync();

        result.UsersSynced.Should().Be(1);
        var account = _accounts.Items.Single();
        account.Broker.Should().Be("freedom");
        account.BrokerAccountId.Should().Be("100001");
        account.Name.Should().Be("Freedom");
        account.UserId.Should().Be(1);

        _positions.Items.Should().HaveCount(2);
        var luv = _positions.Items.Single(p => p.Ticker == "LUV.US");
        (luv.Currency, luv.Quantity, luv.AvgPrice, luv.LastPrice, luv.MarketValue, luv.AssetClass)
            .Should().Be(("USD", 13m, 40.3m, 41.56m, 540.28m, "stock"));
        _positions.Items.Single(p => p.Ticker == "ROBO.EU").AssetClass.Should().Be("etf");

        _cash.Items.Select(c => (c.Currency, c.Amount)).Should().BeEquivalentTo(new[] { ("USD", 4.77m), ("EUR", 88.57m) }, "нулевые остатки (RUR, KZT) не хранятся");
    }

    [Fact]
    public async Task T67_Sync_RefreshesFreedomTariffInCredentialMeta_KeepsOtherFields()
    {
        // Задача 67 (#6): брокер перевёл счёт на Investor (1432) — meta секрета обновляется на синке (GetUserInfoAsync уже вызывается), прочие поля сохраняются.
        _store.Setup(s => s.ListStatusesAsync(1)).ReturnsAsync(
            [new CredentialStatusInfo("freedom", "ok", null, "7890", @"{""accountNumber"":""100001"",""tariffId"":1431,""tariffName"":""Standard"",""extra"":""x""}", null)]);
        _api.Setup(a => a.GetUserInfoAsync(It.IsAny<FreedomCredentials>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FreedomUserInfo("100001", 1432, "Investor"));
        string? saved = null;
        _store.Setup(s => s.UpdateMetaAsync(1, "freedom", It.IsAny<string?>())).Callback<ulong, string, string?>((_, _, m) => saved = m).Returns(Task.CompletedTask);

        await Create().SyncAllAsync();

        saved.Should().NotBeNull();
        using var doc = System.Text.Json.JsonDocument.Parse(saved!);
        doc.RootElement.GetProperty("tariffId").GetInt32().Should().Be(1432);
        doc.RootElement.GetProperty("tariffName").GetString().Should().Be("Investor");
        doc.RootElement.GetProperty("extra").GetString().Should().Be("x");
    }

    [Fact]
    public async Task T67_Sync_SameTariff_DoesNotRewriteMeta()
    {
        _store.Setup(s => s.ListStatusesAsync(1)).ReturnsAsync(
            [new CredentialStatusInfo("freedom", "ok", null, "7890", @"{""accountNumber"":""100001"",""tariffId"":1431,""tariffName"":""Standard""}", null)]);

        await Create().SyncAllAsync();

        _store.Verify(s => s.UpdateMetaAsync(It.IsAny<ulong>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task T67_Sync_ShortPosition_IsStored_NotDropped()
    {
        // Задача 67 (#14): шорт (qty < 0) хранится с отрицательным количеством и стоимостью; закрытая (0) — нет.
        SetupPortfolio(new FreedomPortfolio(
            [new FreedomCashBalance("USD", 500m)],
            [new FreedomPosition("TSLA.US", "Tesla", -2m, "USD", 250m, 240m, -480m), new FreedomPosition("ZERO.US", "Closed", 0m, "USD", 1m, 1m, 0m)]));

        await Create().SyncAllAsync();

        var shortPos = _positions.Items.Single();
        (shortPos.Ticker, shortPos.Quantity, shortPos.MarketValue).Should().Be(("TSLA.US", -2m, -480m));
    }

    [Theory]
    [InlineData("HTTP 401 Unauthorized", true)]
    [InlineData("HTTP 403 Forbidden", true)]
    [InlineData("HTTP 503 Service Unavailable", false)]
    public async Task T67_Sync_KeyRejected401Or403_MarksCredentialInvalid_TransientDoesNot(string message, bool invalid)
    {
        // Задача 67 (#16): 401/403 на синке — ключ freedom помечается invalid; временный сбой (5xx) ключ не трогает.
        _api.Setup(a => a.GetUserInfoAsync(It.IsAny<FreedomCredentials>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FreedomApiException("getAllUserTexInfo", message));

        await Create().SyncAllAsync();

        _store.Verify(s => s.MarkInvalidAsync(1, "freedom", It.IsAny<string>()), invalid ? Times.Once() : Times.Never());
        _accounts.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Sync_LegacyRurBalance_IsStoredAsRub()
    {
        SetupPortfolio(new FreedomPortfolio([new FreedomCashBalance("RUR", 1500m)], []));

        await Create().SyncAllAsync();

        _cash.Items.Should().ContainSingle(c => c.Currency == "RUB" && c.Amount == 1500m);
    }

    [Fact]
    public async Task Sync_Twice_IsIdempotent_NoDuplicateRows()
    {
        _api.Setup(a => a.GetCashFlowsAsync(It.IsAny<FreedomCredentials>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new FreedomCashFlow("1", "bank", new DateTime(2026, 10, 1), "USD", 73.27m, "x")]);
        _api.Setup(a => a.GetTradesAsync(It.IsAny<FreedomCredentials>(), new DateOnly(2020, 1, 1), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new FreedomTrade("55", "o", new DateTime(2026, 10, 2), "LUV.US", FreedomTradeSide.Buy, 1m, 40m, 40m, "USD", 0.2m, "USD")]);
        var service = Create();

        await service.SyncAllAsync();
        var after1 = (_accounts.Items.Count, _positions.Items.Count, _cash.Items.Count, _ops.Items.Count);
        await service.SyncAllAsync();

        (_accounts.Items.Count, _positions.Items.Count, _cash.Items.Count, _ops.Items.Count).Should().Be(after1);
        _ops.Items.Select(o => o.ExternalId).Should().BeEquivalentTo(new[] { "trade:55", _ops.Items.First(o => o.ExternalId.StartsWith("cf:")).ExternalId });
    }

    [Fact]
    public async Task Sync_FetchesTradesFrom2020_AndCashFlowsWindow500()
    {
        await Create().SyncAllAsync();

        _api.Verify(a => a.GetTradesAsync(It.IsAny<FreedomCredentials>(), new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31), It.IsAny<CancellationToken>()), Times.Once);
        _api.Verify(a => a.GetCashFlowsAsync(It.IsAny<FreedomCredentials>(), 500, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Sync_TruncatedTradesWindow_IsSplitInHalf()
    {
        var full = Enumerable.Range(0, FreedomSyncService.TradesPageLimit)
            .Select(i => new FreedomTrade($"t{i}", "o", new DateTime(2020, 3, 1), "X.US", FreedomTradeSide.Buy, 1m, 1m, 1m, "USD", 0m, "USD"))
            .ToList();
        _api.Setup(a => a.GetTradesAsync(It.IsAny<FreedomCredentials>(), new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31), It.IsAny<CancellationToken>()))
            .ReturnsAsync(full);
        // Половины окна возвращают по одной сделке: результат усечённого окна отбрасывается, берутся данные половин.
        _api.Setup(a => a.GetTradesAsync(It.IsAny<FreedomCredentials>(), It.Is<DateOnly>(d => d.Year == 2020), It.Is<DateOnly>(d => d.Year == 2020 && d < new DateOnly(2020, 12, 31)), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FreedomCredentials _, DateOnly from, DateOnly _, CancellationToken _) =>
                [new FreedomTrade($"half-{from:MMdd}", "o", new DateTime(2020, 3, 1), "X.US", FreedomTradeSide.Buy, 1m, 1m, 1m, "USD", 0m, "USD")]);
        _api.Setup(a => a.GetTradesAsync(It.IsAny<FreedomCredentials>(), It.Is<DateOnly>(d => d > new DateOnly(2020, 3, 1)), new DateOnly(2020, 12, 31), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FreedomCredentials _, DateOnly from, DateOnly _, CancellationToken _) =>
                [new FreedomTrade($"half-{from:MMdd}", "o", new DateTime(2020, 9, 1), "X.US", FreedomTradeSide.Buy, 1m, 1m, 1m, "USD", 0m, "USD")]);

        await Create().SyncAllAsync();

        _api.Verify(a => a.GetTradesAsync(It.IsAny<FreedomCredentials>(), new DateOnly(2020, 1, 1), It.Is<DateOnly>(d => d < new DateOnly(2020, 12, 31)), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        _ops.Items.Should().HaveCount(2).And.OnlyContain(o => o.ExternalId.StartsWith("trade:half-"));
    }

    // --- сверка ---

    [Fact]
    public async Task Sync_PositionNoLongerHeld_IsRemoved()
    {
        var service = Create();
        await service.SyncAllAsync();
        SetupPortfolio(new FreedomPortfolio(Portfolio().Cash, [Portfolio().Positions[0]]));

        var result = await service.SyncAllAsync();

        _positions.Items.Select(p => p.Ticker).Should().Equal("LUV.US");
        result.PositionsClosed.Should().Be(1);
    }

    [Fact]
    public async Task Sync_EmptyBrokerResponse_DeletesPositionsOnlyOnSecondConsecutiveSync()
    {
        var service = Create();
        await service.SyncAllAsync();
        SetupPortfolio(new FreedomPortfolio([], []));

        await service.SyncAllAsync();
        _positions.Items.Should().HaveCount(2, "первый пустой ответ — подозрительный, удаление откладывается");

        await service.SyncAllAsync();
        _positions.Items.Should().BeEmpty("второй пустой ответ подряд — удаляем");
    }

    [Fact]
    public async Task Sync_EmptyCashList_DoesNotWipeStoredBalances_ButAllZeroDoes()
    {
        var service = Create();
        await service.SyncAllAsync();

        SetupPortfolio(new FreedomPortfolio([], Portfolio().Positions));
        await service.SyncAllAsync();
        _cash.Items.Should().HaveCount(2, "пустой acc — признак деградации ответа");

        SetupPortfolio(new FreedomPortfolio([new FreedomCashBalance("USD", 0m), new FreedomCashBalance("EUR", 0m)], Portfolio().Positions));
        await service.SyncAllAsync();
        _cash.Items.Should().BeEmpty("брокер явно отдал нули — остатков нет");
    }

    // --- устойчивость и ключи ---

    [Fact]
    public async Task Sync_PortfolioFails_TradesAndCashFlowsStillSynced_NoThrow()
    {
        _api.Setup(a => a.GetPortfolioAsync(It.IsAny<FreedomCredentials>(), It.IsAny<CancellationToken>())).ThrowsAsync(new FreedomApiException("getPositionJson", "boom"));
        _api.Setup(a => a.GetCashFlowsAsync(It.IsAny<FreedomCredentials>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new FreedomCashFlow("1", "bank", new DateTime(2026, 10, 1), "USD", 5m, "x")]);

        var result = await Create().SyncAllAsync();

        result.Errors.Should().ContainSingle(e => e.Contains("portfolio"));
        _ops.Items.Should().HaveCount(1);
        result.UsersSynced.Should().Be(1);
    }

    [Fact]
    public async Task Sync_OneUserFails_OtherUsersStillSynced()
    {
        _store.Setup(s => s.ListUserIdsWithValidAsync("freedom")).ReturnsAsync(new ulong[] { 1, 2 });
        _store.Setup(s => s.GetSecretResultAsync(2, "freedom")).ReturnsAsync(Secret());
        _api.SetupSequence(a => a.GetUserInfoAsync(It.IsAny<FreedomCredentials>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FreedomApiException("GetAllUserTexInfo", "invalid key"))
            .ReturnsAsync(new FreedomUserInfo("200002", 1430, "Investor"));

        var result = await Create().SyncAllAsync();

        result.UsersSynced.Should().Be(1);
        result.Errors.Should().ContainSingle();
        _accounts.Items.Should().ContainSingle(a => a.UserId == 2 && a.BrokerAccountId == "200002");
    }

    [Fact]
    public async Task Sync_UserWithUnreadableSecret_IsSkipped_WithoutCallingBroker()
    {
        _store.Setup(s => s.GetSecretResultAsync(1, "freedom")).ReturnsAsync(new CredentialSecretResult(CredentialReadOutcome.DecryptionFailed, null));

        var result = await Create().SyncAllAsync();

        result.UsersSynced.Should().Be(0);
        _api.Verify(a => a.GetUserInfoAsync(It.IsAny<FreedomCredentials>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Sync_NoUsersWithKeys_DoesNothing()
    {
        _store.Setup(s => s.ListUserIdsWithValidAsync("freedom")).ReturnsAsync(Array.Empty<ulong>());

        var result = await Create().SyncAllAsync();

        result.UsersSynced.Should().Be(0);
        _accounts.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Sync_PassesKeysToClient_ButNeverLogsThem()
    {
        _api.Setup(a => a.GetPortfolioAsync(It.IsAny<FreedomCredentials>(), It.IsAny<CancellationToken>())).ThrowsAsync(new FreedomApiException("getPositionJson", "boom"));

        var result = await Create().SyncAllAsync();

        _api.Verify(a => a.GetUserInfoAsync(It.Is<FreedomCredentials>(c => c.PublicKey == Pub && c.PrivateKey == Priv), It.IsAny<CancellationToken>()), Times.Once);
        var everything = string.Join('\n', _log.Messages.Concat(result.Errors));
        everything.Should().NotContain(Pub).And.NotContain(Priv);
    }

    [Fact]
    public async Task Sync_ExistingFreedomAccount_IsReused_AndBrokerAccountIdUpdated()
    {
        _accounts.Items.Add(new Account { Id = 50, UserId = 1, Broker = "freedom", BrokerAccountId = "old", Name = "Freedom" });

        await Create().SyncAllAsync();

        _accounts.Items.Should().ContainSingle().Which.BrokerAccountId.Should().Be("100001");
        _positions.Items.Should().OnlyContain(p => p.AccountId == 50);
    }

    // --- fakes ---

    private sealed class FakeAccounts : IAccountRepository
    {
        public readonly List<Account> Items = [];
        private ulong _next = 100;

        public Task<IReadOnlyList<Account>> GetByUserIdAndBrokerAsync(ulong userId, string broker) =>
            Task.FromResult<IReadOnlyList<Account>>(Items.Where(a => a.UserId == userId && a.Broker == broker).OrderBy(a => a.Id).ToList());

        public Task<ulong> CreateAsync(Account account)
        {
            account.Id = _next++;
            Items.Add(account);
            return Task.FromResult(account.Id);
        }

        public Task UpdateAsync(Account account) => Task.CompletedTask;

        public Task<Account?> GetByIdAsync(ulong id, ulong userId) => throw new NotImplementedException();
        public Task<Account?> GetByIdAsync(ulong id) => throw new NotImplementedException();
        public Task<IEnumerable<Account>> GetByUserIdAsync(ulong userId) => throw new NotImplementedException();
        public Task UpdateBalanceAsync(ulong accountId, decimal? cashRub, decimal? brokerTotalRub, DateTime syncedAtUtc) => throw new NotImplementedException();
        public Task<ulong?> GetPrimaryAccountIdAsync() => throw new NotImplementedException();
    }

    private sealed class FakePositions : IBrokerPositionRepository
    {
        public readonly List<BrokerPosition> Items = [];

        public Task<IReadOnlyList<BrokerPosition>> GetByAccountAsync(ulong accountId) =>
            Task.FromResult<IReadOnlyList<BrokerPosition>>(Items.Where(p => p.AccountId == accountId).ToList());

        public Task UpsertAsync(BrokerPosition position)
        {
            Items.RemoveAll(p => p.AccountId == position.AccountId && p.Ticker == position.Ticker);
            Items.Add(position);
            return Task.CompletedTask;
        }

        public Task<int> DeleteAllExceptAsync(ulong accountId, IReadOnlyCollection<string> keepTickers) =>
            Task.FromResult(Items.RemoveAll(p => p.AccountId == accountId && !keepTickers.Contains(p.Ticker)));
    }

    private sealed class FakeCash : IBrokerCashRepository
    {
        public readonly List<BrokerCash> Items = [];

        public Task<IReadOnlyList<BrokerCash>> GetByAccountAsync(ulong accountId) =>
            Task.FromResult<IReadOnlyList<BrokerCash>>(Items.Where(c => c.AccountId == accountId).ToList());

        public Task UpsertAsync(BrokerCash cash)
        {
            Items.RemoveAll(c => c.AccountId == cash.AccountId && c.Currency == cash.Currency);
            Items.Add(cash);
            return Task.CompletedTask;
        }

        public Task<int> DeleteAllExceptAsync(ulong accountId, IReadOnlyCollection<string> keepCurrencies) =>
            Task.FromResult(Items.RemoveAll(c => c.AccountId == accountId && !keepCurrencies.Contains(c.Currency)));
    }

    private sealed class FakeOperations : IBrokerOperationRepository
    {
        public readonly List<BrokerOperation> Items = [];

        public Task<int> UpsertManyAsync(IReadOnlyCollection<BrokerOperation> operations)
        {
            foreach (var op in operations)
            {
                Items.RemoveAll(o => o.AccountId == op.AccountId && o.ExternalId == op.ExternalId);
                Items.Add(op);
            }

            return Task.FromResult(operations.Count);
        }

        public Task<IReadOnlyList<BrokerOperation>> GetByAccountAsync(ulong accountId, DateOnly? from, DateOnly? to) =>
            Task.FromResult<IReadOnlyList<BrokerOperation>>(Items.Where(o => o.AccountId == accountId).ToList());

        public Task<IReadOnlyList<BrokerOperation>> GetByTypeAsync(ulong accountId, BrokerOperationType type) =>
            Task.FromResult<IReadOnlyList<BrokerOperation>>(Items.Where(o => o.AccountId == accountId && o.Type == type).ToList());
    }
}

/// <summary>Логгер, собирающий все сообщения (с исключениями) для проверки, что секреты не попали в лог.</summary>
internal sealed class CapturingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    public readonly List<string> Messages = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Messages.Add(formatter(state, exception));
        if (exception is not null) Messages.Add(exception.ToString());
    }
}

/// <summary>Общий статический словарь <see cref="FreedomSyncService.EmptyPositionsSeen"/> — тесты синка Freedom не гоняются параллельно.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FreedomSyncStaticCollection
{
    public const string Name = "FreedomSyncStatic";
}
