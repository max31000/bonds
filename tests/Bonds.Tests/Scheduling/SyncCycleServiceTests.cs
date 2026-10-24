using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Core.Signals;
using Bonds.Core.Time;
using Bonds.Infrastructure.Analytics;
using Bonds.Infrastructure.CashFlow;
using Bonds.Infrastructure.Connectors.Moex;
using Bonds.Infrastructure.Connectors.TInvest;
using Bonds.Infrastructure.Scheduling;
using Bonds.Infrastructure.Services;
using Bonds.Infrastructure.Sync;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Bonds.Tests.Scheduling;

/// <summary>
/// Тесты полного цикла синка (plan/07 Часть B) — собирает реальный DI-контейнер с замоканными
/// интерфейсами репозиториев/коннекторов (тот же паттерн моков, что в BondSyncServiceTests/
/// CashFlowProjectionOrchestratorTests) и реальными конкретными классами BondSyncService/
/// CashFlowProjectionOrchestrator/PortfolioSnapshotService/SyncCycleService — потому что эти три
/// оркестратора зарегистрированы как конкретные классы (не за интерфейсом), а SyncCycleService
/// сам резолвит их через IServiceScopeFactory.CreateScope() внутри RunCycleAsync (Singleton-сервис,
/// см. doc-comment SyncCycleService).
/// </summary>
public class SyncCycleServiceTests
{
    private const ulong AccountId = 1;
    private const string BrokerAccountId = "BA-1";

    private readonly Mock<IAccountRepository> _accounts = new();
    private readonly Mock<ITInvestPortfolioClient> _tInvest = new();
    private readonly Mock<IMoexIssClient> _moex = new();
    private readonly Mock<IInstrumentRepository> _instruments = new();
    private readonly Mock<ICouponScheduleRepository> _coupons = new();
    private readonly Mock<IAmortizationScheduleRepository> _amortizations = new();
    private readonly Mock<IOfferScheduleRepository> _offers = new();
    private readonly Mock<IMarketQuoteRepository> _quotes = new();
    private readonly Mock<IYieldCurveRepository> _yieldCurve = new();
    private readonly Mock<IPositionRepository> _positions = new();
    private readonly Mock<IOperationRepository> _operations = new();
    private readonly Mock<IProjectedCashFlowRepository> _projectedCashFlows = new();
    private readonly Mock<IPortfolioValueSnapshotRepository> _snapshots = new();
    private readonly Mock<ISignalRepository> _signals = new();
    private readonly Mock<IUserSettingsRepository> _userSettings = new();
    private readonly Mock<ITelegramAlertSender> _alertSender = new();
    private readonly Mock<ITInvestTokenProvider> _tokenProvider = new();
    private readonly Mock<IWatchlistItemRepository> _watchlistItems = new();
    private readonly Mock<IInstrumentRiskLevelRepository> _riskLevels = new();
    private readonly Mock<IBondUniverseRepository> _universe = new();
    private readonly Mock<ISecurityDetailsRepository> _securityDetails = new();
    private Mock<IUserCredentialRepository>? _credentialRecords;
    private Bonds.Infrastructure.Policy.IPolicyCycleRunner? _policyRunner;

    /// <summary>Фейковый прогон правил (T-67D): считает вызовы, может ждать сигнала (медленный прогон) или бросать.</summary>
    private sealed class FakePolicyRunner : Bonds.Infrastructure.Policy.IPolicyCycleRunner
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Slow { get; init; }
        public bool Throw { get; init; }
        public int Calls;
        public bool Completed;

        public async Task<Bonds.Infrastructure.Policy.PolicyRunResult> RunAllAsync(DateTime nowUtc, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            Started.TrySetResult();
            if (Slow) await Release.Task;
            if (Throw) throw new InvalidOperationException("policy boom");
            Completed = true;
            return new Bonds.Infrastructure.Policy.PolicyRunResult();
        }
    }

    private void SetupNoPositionsHappyPath()
    {
        _accounts.Setup(a => a.GetPrimaryAccountIdAsync()).ReturnsAsync(AccountId);

        _tokenProvider.Setup(t => t.GetTokenStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TInvestTokenStatus.Valid);

        _tInvest.Setup(c => c.GetPrimaryAccountIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(BrokerAccountId);
        _tInvest.Setup(c => c.GetPortfolioAsync(BrokerAccountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TInvestPortfolio { Bonds = [] });
        _tInvest.Setup(c => c.GetOperationsAsync(BrokerAccountId, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TInvestOperation>());
        _tInvest.Setup(c => c.GetQuotesAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, TInvestQuote>());
        _moex.Setup(m => m.GetYieldCurveAsync(It.IsAny<CancellationToken>())).ReturnsAsync((YieldCurveSnapshot?)null);

        _positions.Setup(p => p.GetByAccountIdAsync(AccountId)).ReturnsAsync(new List<Position>());
        _operations.Setup(o => o.GetByAccountIdAsync(AccountId, null, null)).ReturnsAsync(new List<Operation>());
        _signals.Setup(s => s.GetByAccountIdAsync(AccountId, true)).ReturnsAsync(new List<Signal>());
        _signals.Setup(s => s.InsertAsync(It.IsAny<Signal>())).ReturnsAsync(1ul);
        _yieldCurve.Setup(y => y.GetLatestAsync()).ReturnsAsync((YieldCurveSnapshot?)null);

        _snapshots.Setup(s => s.UpsertAsync(It.IsAny<PortfolioValueSnapshot>())).Returns(Task.CompletedTask);

        // Задача 20 часть A: шаг синка watchlist — пустой список по умолчанию (ни одной watchlist-записи).
        _watchlistItems.Setup(w => w.GetAllAsync()).ReturnsAsync(new List<WatchlistItem>());

        // Задача 49: банк/карточки по умолчанию пусты (рыночные/кредитные события не порождаются).
        _universe.Setup(u => u.GetByIsinsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _universe.Setup(u => u.GetRecentHistoryForSecidsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _securityDetails.Setup(d => d.GetBySecidsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _securityDetails.Setup(d => d.GetEmitentDefaultHistoryAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, EmitentDefaultHistory>());
    }

    private ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();

        services.AddSingleton(_accounts.Object);
        services.AddSingleton(_tInvest.Object);
        services.AddSingleton(_tokenProvider.Object);
        services.AddSingleton(_moex.Object);
        services.AddSingleton(_instruments.Object);
        services.AddSingleton(_coupons.Object);
        services.AddSingleton(_amortizations.Object);
        services.AddSingleton(_offers.Object);
        services.AddSingleton(_quotes.Object);
        services.AddSingleton(_yieldCurve.Object);
        services.AddSingleton(_positions.Object);
        services.AddSingleton(_operations.Object);
        services.AddSingleton(_projectedCashFlows.Object);
        services.AddSingleton(_snapshots.Object);
        services.AddSingleton(_signals.Object);
        services.AddSingleton(_userSettings.Object);
        services.AddSingleton(_alertSender.Object);
        services.AddSingleton(_watchlistItems.Object);
        services.AddSingleton(_riskLevels.Object);
        services.AddSingleton(_universe.Object);
        services.AddSingleton(_securityDetails.Object);
        if (_credentialRecords is not null) services.AddSingleton(_credentialRecords.Object);
        if (_policyRunner is not null) services.AddSingleton(_policyRunner);

        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));

        services.AddScoped<BondSyncService>();
        services.AddScoped<PortfolioHoldingsBuilder>();
        services.AddScoped<CashFlowProjectionOrchestrator>();
        services.AddScoped<PortfolioSnapshotService>();
        services.AddScoped<InstrumentEnrichmentService>();
        services.AddScoped<WatchlistSyncService>();
        services.AddScoped<PortfolioHistoryBackfillService>();
        services.AddScoped(_ => new Mock<ICurrencyRateLookup>().Object);
        services.AddScoped<InstrumentRiskLevelRefresher>();
        services.AddScoped<Bonds.Infrastructure.Universe.PositionMarketContextBuilder>();

        services.AddSingleton<IOptions<SignalEngineOptions>>(Options.Create(new SignalEngineOptions()));
        services.AddSingleton<ISyncCycleRunner, SyncCycleService>();

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task T67_TwoUsersWithTInvestRead_EachSyncedWithOwnTokenIntoOwnAccount_OwnerPathUnchanged()
    {
        // Задача 67 (#5): владелец (user 1, основной счёт 1 / BA-1) — как раньше, без привязки провайдера; второй пользователь с валидным tinvest_read
        // (user 2, счёт 2 / BA-2) — в отдельном скоупе с UseUser(2): клиент T-Invest строится по ЕГО токену, синк идёт в ЕГО счёт.
        SetupNoPositionsHappyPath();
        _credentialRecords = new Mock<IUserCredentialRepository>();
        _credentialRecords.Setup(r => r.ListUserIdsAsync("tinvest_read", "ok")).ReturnsAsync([1UL, 2UL]);
        _accounts.Setup(a => a.GetByIdAsync(AccountId)).ReturnsAsync(new Account { Id = AccountId, UserId = 1, BrokerAccountId = BrokerAccountId });
        _accounts.Setup(a => a.GetByIdAsync(2UL)).ReturnsAsync(new Account { Id = 2, UserId = 2, BrokerAccountId = "BA-2" });
        _accounts.Setup(a => a.GetByUserIdAndBrokerAsync(2UL, Bonds.Core.Models.Brokers.TInvest)).ReturnsAsync([new Account { Id = 2, UserId = 2, BrokerAccountId = "BA-2" }]);
        _tInvest.Setup(c => c.GetPortfolioAsync("BA-2", It.IsAny<CancellationToken>())).ReturnsAsync(new TInvestPortfolio { Bonds = [] });
        _tInvest.Setup(c => c.GetOperationsAsync("BA-2", It.IsAny<DateTime?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<TInvestOperation>());
        _positions.Setup(p => p.GetByAccountIdAsync(2UL)).ReturnsAsync(new List<Position>());
        _operations.Setup(o => o.GetByAccountIdAsync(2UL, null, null)).ReturnsAsync(new List<Operation>());

        var result = await BuildServiceProvider().GetRequiredService<ISyncCycleRunner>().RunCycleAsync();

        result.HasErrors.Should().BeFalse();
        _tokenProvider.Verify(t => t.UseUser(2UL), Times.Once);
        _tokenProvider.Verify(t => t.UseUser(1UL), Times.Never, "путь владельца не меняется");
        _tInvest.Verify(c => c.GetPortfolioAsync(BrokerAccountId, It.IsAny<CancellationToken>()), Times.Once);
        _tInvest.Verify(c => c.GetPortfolioAsync("BA-2", It.IsAny<CancellationToken>()), Times.Once);
        _accounts.Verify(a => a.CreateAsync(It.IsAny<Account>()), Times.Never, "счёт второго пользователя уже есть");
    }

    [Fact]
    public async Task RunCycleAsync_NoAccountConfigured_SkipsWithoutError()
    {
        _accounts.Setup(a => a.GetPrimaryAccountIdAsync()).ReturnsAsync((ulong?)null);
        var sp = BuildServiceProvider();
        var runner = sp.GetRequiredService<ISyncCycleRunner>();

        var result = await runner.RunCycleAsync();

        result.NoAccountConfigured.Should().BeTrue();
        result.HasErrors.Should().BeFalse();
    }

    [Fact]
    public async Task RunCycleAsync_JournalSyncFailed_HistoryBackfillNotAttempted()
    {
        // Бэкфилл истории из неполного журнала закрепился бы навсегда — без успешного синка журнала
        // его не запускаем (ни разу не спрашиваем количество снапшотов).
        SetupNoPositionsHappyPath();
        _tInvest.Setup(c => c.GetOperationsAsync(BrokerAccountId, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("journal down"));
        var runner = BuildServiceProvider().GetRequiredService<ISyncCycleRunner>();

        await runner.RunCycleAsync();

        _snapshots.Verify(s => s.GetByAccountIdAsync(AccountId, null, null), Times.Never);
    }

    [Fact]
    public async Task RunCycleAsync_JournalSynced_SparseHistory_ChecksAndBackfills()
    {
        SetupNoPositionsHappyPath();
        _snapshots.Setup(s => s.GetByAccountIdAsync(AccountId, null, null)).ReturnsAsync(new List<PortfolioValueSnapshot>());
        var runner = BuildServiceProvider().GetRequiredService<ISyncCycleRunner>();

        await runner.RunCycleAsync();

        _snapshots.Verify(s => s.GetByAccountIdAsync(AccountId, null, null), Times.AtLeastOnce);
    }

    // ─── T-67D: правила — в фоне после цикла, вне критического пути ручного синка ─────────────────────

    [Fact]
    public async Task T67D_Policies_RunInBackground_AfterCycleReturnedAndGateReleased()
    {
        SetupNoPositionsHappyPath();
        var fake = new FakePolicyRunner { Slow = true };
        _policyRunner = fake;
        var service = (SyncCycleService)BuildServiceProvider().GetRequiredService<ISyncCycleRunner>();

        var result = await service.RunCycleAsync().WaitAsync(TimeSpan.FromSeconds(10)); // медленные правила НЕ держат цикл

        result.HasErrors.Should().BeFalse();
        service.GetStatus().IsRunning.Should().BeFalse("ворота синка отпущены до правил");
        await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        fake.Completed.Should().BeFalse("правила ещё идут, а цикл уже вернул результат");

        fake.Release.SetResult();
        await service.BackgroundPolicies.WaitAsync(TimeSpan.FromSeconds(10));
        fake.Completed.Should().BeTrue();
        fake.Calls.Should().Be(1);
    }

    [Fact]
    public async Task T67D_Policies_InvokedOncePerCycle()
    {
        SetupNoPositionsHappyPath();
        var fake = new FakePolicyRunner();
        _policyRunner = fake;
        var service = (SyncCycleService)BuildServiceProvider().GetRequiredService<ISyncCycleRunner>();

        await service.RunCycleAsync();
        await service.BackgroundPolicies.WaitAsync(TimeSpan.FromSeconds(10));
        fake.Calls.Should().Be(1);

        await service.RunCycleAsync();
        await service.BackgroundPolicies.WaitAsync(TimeSpan.FromSeconds(10));
        fake.Calls.Should().Be(2);
    }

    [Fact]
    public async Task T67D_Policies_ExceptionDoesNotEscape()
    {
        SetupNoPositionsHappyPath();
        var fake = new FakePolicyRunner { Throw = true };
        _policyRunner = fake;
        var service = (SyncCycleService)BuildServiceProvider().GetRequiredService<ISyncCycleRunner>();

        var result = await service.RunCycleAsync();
        await service.BackgroundPolicies.WaitAsync(TimeSpan.FromSeconds(10)); // не бросает

        result.HasErrors.Should().BeFalse("сбой правил — только лог");
        service.BackgroundPolicies.IsFaulted.Should().BeFalse();
        fake.Calls.Should().Be(1);
    }

    [Fact]
    public async Task T67D_Policies_NotRun_WhenNoAccountConfigured()
    {
        _accounts.Setup(a => a.GetPrimaryAccountIdAsync()).ReturnsAsync((ulong?)null);
        var fake = new FakePolicyRunner();
        _policyRunner = fake;
        var service = (SyncCycleService)BuildServiceProvider().GetRequiredService<ISyncCycleRunner>();

        (await service.RunCycleAsync()).NoAccountConfigured.Should().BeTrue();
        await service.BackgroundPolicies.WaitAsync(TimeSpan.FromSeconds(10));
        fake.Calls.Should().Be(0);
    }

    [Fact]
    public async Task RunCycleAsync_HappyPath_RunsFullCycleAndUpdatesStatus()
    {
        SetupNoPositionsHappyPath();
        var sp = BuildServiceProvider();
        var runner = sp.GetRequiredService<ISyncCycleRunner>();

        var result = await runner.RunCycleAsync();

        result.AlreadyRunning.Should().BeFalse();
        result.NoAccountConfigured.Should().BeFalse();
        result.SnapshotStored.Should().BeTrue();
        result.HasErrors.Should().BeFalse();

        var status = runner.GetStatus();
        status.IsRunning.Should().BeFalse();
        status.LastSuccessAtUtc.Should().NotBeNull();
        status.LastRunStartedAtUtc.Should().NotBeNull();

        _snapshots.Verify(s => s.UpsertAsync(It.Is<PortfolioValueSnapshot>(snap => snap.AccountId == AccountId)), Times.Once);
    }

    [Fact]
    public async Task RunCycleAsync_RiskLevelStepRunsOnceBulkCall_AndItsFailureDoesNotFailTheCycle()
    {
        SetupNoPositionsHappyPath();
        _riskLevels.Setup(r => r.GetLastFetchedAtAsync(It.IsAny<CancellationToken>())).ReturnsAsync((DateTime?)null);
        _tInvest.Setup(c => c.GetBondRiskLevelsAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new Exception("grpc down"));
        var runner = BuildServiceProvider().GetRequiredService<ISyncCycleRunner>();

        var result = await runner.RunCycleAsync();

        _tInvest.Verify(c => c.GetBondRiskLevelsAsync(It.IsAny<CancellationToken>()), Times.Once);
        result.HasErrors.Should().BeFalse("risk_level — необязательный вход скоркарты, сбой не краснит синк");
        result.SnapshotStored.Should().BeTrue();
    }

    [Fact]
    public async Task RunCycleAsync_TokenInvalid_RiskLevelStepIsSkipped()
    {
        SetupNoPositionsHappyPath();
        _tokenProvider.Setup(t => t.GetTokenStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(TInvestTokenStatus.NotConfigured);
        var runner = BuildServiceProvider().GetRequiredService<ISyncCycleRunner>();

        await runner.RunCycleAsync();

        _tInvest.Verify(c => c.GetBondRiskLevelsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunCycleAsync_SyncStepThrows_OtherStepsStillRunAndStatusReflectsFailure()
    {
        SetupNoPositionsHappyPath();
        _tInvest.Setup(c => c.GetPrimaryAccountIdAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated T-Invest outage"));

        var sp = BuildServiceProvider();
        var runner = sp.GetRequiredService<ISyncCycleRunner>();

        var result = await runner.RunCycleAsync();

        result.HasErrors.Should().BeTrue();
        result.SnapshotStored.Should().BeTrue("сбой синка не должен останавливать остальные шаги цикла");

        var status = runner.GetStatus();
        status.LastFailureAtUtc.Should().NotBeNull();
        status.LastRunErrors.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData(TInvestTokenStatus.NotConfigured)]
    [InlineData(TInvestTokenStatus.DecryptionFailed)]
    public async Task RunCycleAsync_TokenNotConfiguredOrUndecryptable_StatusReflectsTokenMissingOrInvalid(TInvestTokenStatus tokenStatus)
    {
        SetupNoPositionsHappyPath();
        _tokenProvider.Setup(t => t.GetTokenStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(tokenStatus);

        var sp = BuildServiceProvider();
        var runner = sp.GetRequiredService<ISyncCycleRunner>();

        var result = await runner.RunCycleAsync();

        result.TokenMissingOrInvalid.Should().BeTrue();
        runner.GetStatus().TokenMissingOrInvalid.Should().BeTrue();
    }

    [Fact]
    public async Task RunCycleAsync_TokenValid_StatusDoesNotFlagTokenMissingOrInvalid()
    {
        SetupNoPositionsHappyPath();

        var sp = BuildServiceProvider();
        var runner = sp.GetRequiredService<ISyncCycleRunner>();

        var result = await runner.RunCycleAsync();

        result.TokenMissingOrInvalid.Should().BeFalse();
        runner.GetStatus().TokenMissingOrInvalid.Should().BeFalse();
    }

    [Fact]
    public async Task RunCycleAsync_GeneratesSignalsAndPersistsThem_WhenCandidatesExist()
    {
        SetupNoPositionsHappyPath();

        var position = new Position { Id = 10, AccountId = AccountId, InstrumentId = 5, Quantity = 1, Accrued = 0m };
        _positions.Setup(p => p.GetByAccountIdAsync(AccountId)).ReturnsAsync(new[] { position });

        var instrument = new Instrument
        {
            Id = 5,
            Isin = "RU000TEST001",
            Issuer = "Тест-Эмитент",
            FaceValue = 1000m,
            // Относительно BusinessClock.MoscowToday() (та же точка отсчёта, что и SyncCycleService
            // использует для asOf) — заведомо внутри окна "погашение в ближайшие 14 дней" для
            // UpcomingRedemption независимо от того, в какой день реально запускается тест
            // (было: захардкоженная абсолютная дата 2026-07-01 — тест ломался, как только "сегодня"
            // проезжало мимо этой даты).
            MaturityDate = BusinessClock.MoscowToday().AddDays(7),
            CouponType = CouponType.Fixed,
        };
        _instruments.Setup(i => i.GetByIdAsync(5)).ReturnsAsync(instrument);
        _coupons.Setup(c => c.GetByInstrumentIdAsync(5)).ReturnsAsync(new List<CouponSchedule>());
        _amortizations.Setup(a => a.GetByInstrumentIdAsync(5)).ReturnsAsync(new List<AmortizationSchedule>());
        _offers.Setup(o => o.GetByInstrumentIdAsync(5)).ReturnsAsync(new List<OfferSchedule>());
        _quotes.Setup(q => q.GetLatestAsync(5)).ReturnsAsync((MarketQuote?)null);

        ulong? createdSignalAccountId = null;
        _signals.Setup(s => s.InsertAsync(It.IsAny<Signal>()))
            .Callback<Signal>(sig => createdSignalAccountId = sig.AccountId)
            .ReturnsAsync(1ul);

        var sp = BuildServiceProvider();
        var runner = sp.GetRequiredService<ISyncCycleRunner>();

        var result = await runner.RunCycleAsync();

        result.SignalsCreated.Should().BeGreaterThan(0, "погашение в ближайшие 14 дней должно сгенерировать UpcomingRedemption");
        createdSignalAccountId.Should().Be(AccountId);
        _signals.Verify(s => s.InsertAsync(It.Is<Signal>(sig => sig.Type == SignalType.UpcomingRedemption && sig.Subject == "Тест-Эмитент")), Times.Once);
    }

    private void SetupOpenPosition(DateOnly maturity, IReadOnlyList<OfferSchedule>? offers = null, CouponType couponType = CouponType.Fixed)
    {
        var position = new Position { Id = 10, AccountId = AccountId, InstrumentId = 5, Quantity = 1, Accrued = 0m };
        _positions.Setup(p => p.GetByAccountIdAsync(AccountId)).ReturnsAsync(new[] { position });
        _instruments.Setup(i => i.GetByIdAsync(5)).ReturnsAsync(new Instrument
        {
            Id = 5, Isin = "RU000TEST001", Name = "Тест-Бумага", Issuer = "Тест-Эмитент", FaceValue = 1000m,
            MaturityDate = maturity, CouponType = couponType,
        });
        _coupons.Setup(c => c.GetByInstrumentIdAsync(5)).ReturnsAsync(new List<CouponSchedule>());
        _amortizations.Setup(a => a.GetByInstrumentIdAsync(5)).ReturnsAsync(new List<AmortizationSchedule>());
        _offers.Setup(o => o.GetByInstrumentIdAsync(5)).ReturnsAsync((offers ?? []).ToList());
        _quotes.Setup(q => q.GetLatestAsync(5)).ReturnsAsync((MarketQuote?)null);
    }

    [Fact]
    public async Task RunCycleAsync_Events_SecondRunWithSameState_DoesNotInsertAgain_AndResolvesDisappeared()
    {
        SetupNoPositionsHappyPath();
        var today = BusinessClock.MoscowToday();
        SetupOpenPosition(today.AddDays(7));

        var redemptionKey = Signal.BuildKey(SignalType.UpcomingRedemption, "5", today.AddDays(7));
        var staleKey = Signal.BuildKey(SignalType.UpcomingRedemption, "99", today.AddDays(3));
        _signals.Setup(s => s.GetByAccountIdAsync(AccountId, true)).ReturnsAsync(new List<Signal>
        {
            new() { Id = 1, AccountId = AccountId, EventKey = redemptionKey, Type = SignalType.UpcomingRedemption, AcknowledgedAt = DateTime.UtcNow.AddHours(-2) },
            new() { Id = 2, AccountId = AccountId, EventKey = staleKey, Type = SignalType.UpcomingRedemption },
        });

        var runner = BuildServiceProvider().GetRequiredService<ISyncCycleRunner>();
        var result = await runner.RunCycleAsync();

        result.SignalsCreated.Should().Be(0, "событие с тем же ключом уже есть (даже подтверждённое) — дубля нет");
        _signals.Verify(s => s.InsertAsync(It.IsAny<Signal>()), Times.Never);
        _signals.Verify(s => s.RefreshAsync(1ul, It.IsAny<SignalSeverity>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<DateTime>()), Times.Once);
        _signals.Verify(s => s.ResolveAsync(It.Is<IReadOnlyCollection<ulong>>(ids => ids.Single() == 2ul), It.IsAny<DateTime>()), Times.Once);
        _signals.Verify(s => s.PurgeResolvedBeforeAsync(AccountId, It.IsAny<DateTime>()), Times.Once);
    }

    [Fact]
    public async Task RunCycleAsync_NewPutOffer_SendsSingleTelegramAlert_ButInfoDoesNot()
    {
        SetupNoPositionsHappyPath();
        var today = BusinessClock.MoscowToday();
        SetupOpenPosition(today.AddYears(3), [new OfferSchedule { InstrumentId = 5, Date = today.AddDays(5), OfferType = OfferType.Put }]);

        var runner = BuildServiceProvider().GetRequiredService<ISyncCycleRunner>();
        await runner.RunCycleAsync();

        _alertSender.Verify(a => a.SendAsync(It.Is<string>(m => m.Contains("Тест-Бумага")), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunCycleAsync_Events_RedemptionOnly_NoTelegramAlert()
    {
        SetupNoPositionsHappyPath();
        SetupOpenPosition(BusinessClock.MoscowToday().AddDays(7));

        await BuildServiceProvider().GetRequiredService<ISyncCycleRunner>().RunCycleAsync();

        _alertSender.Verify(a => a.SendAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunCycleAsync_Events_UserSettingsThresholdsOverrideDefaults()
    {
        SetupNoPositionsHappyPath();
        var today = BusinessClock.MoscowToday();
        SetupOpenPosition(today.AddDays(40)); // вне дефолтного окна 14 дней
        _accounts.Setup(a => a.GetByIdAsync(AccountId)).ReturnsAsync(new Account { Id = AccountId, UserId = 3, CashRub = 50_000m });
        _userSettings.Setup(u => u.GetByUserIdAsync(3)).ReturnsAsync(new UserSettings { UserId = 3, UpcomingEventDaysThreshold = 60, UninvestedCashThresholdRub = 40_000m });

        await BuildServiceProvider().GetRequiredService<ISyncCycleRunner>().RunCycleAsync();

        _signals.Verify(s => s.InsertAsync(It.Is<Signal>(x => x.Type == SignalType.UpcomingRedemption)), Times.Once);
        _signals.Verify(s => s.InsertAsync(It.Is<Signal>(x => x.Type == SignalType.UninvestedCashThreshold)), Times.Once);
    }

    [Fact]
    public async Task RunCycleAsync_SecondCallWhileFirstIsRunning_ReturnsAlreadyRunningImmediately()
    {
        SetupNoPositionsHappyPath();

        // Замедляем первый вызов искусственно через медленный мок T-Invest, чтобы second call
        // застал первый "в процессе" — гарантирует детерминированную проверку без race condition.
        var gate = new TaskCompletionSource();
        _tInvest.Setup(c => c.GetPrimaryAccountIdAsync(It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await gate.Task;
                return BrokerAccountId;
            });

        var sp = BuildServiceProvider();
        var runner = sp.GetRequiredService<ISyncCycleRunner>();

        var firstCallTask = runner.RunCycleAsync();

        // Даём первому вызову время дойти до точки ожидания и захватить семафор.
        await Task.Delay(50);

        var secondResult = await runner.RunCycleAsync();

        secondResult.AlreadyRunning.Should().BeTrue();

        gate.SetResult();
        await firstCallTask;
    }

    // ─── Задача 49: рыночные и кредитные события ───────────────────────────────────────────

    private void SetupBankEntryForOpenPosition(decimal gspread, int emitentId = 777)
    {
        _universe.Setup(u => u.GetByIsinsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new BondUniverseEntry
            {
                Secid = "SU49", Isin = "RU000TEST001", EmitentId = emitentId, GspreadOfficialFraction = gspread, PricePercent = 99m, IsFloater = false,
            }]);
    }

    [Fact]
    public async Task RunCycleAsync_IssuerDefaultAppears_InsertsCriticalEvent_AndSendsTelegramAlertOnce()
    {
        SetupNoPositionsHappyPath();
        SetupOpenPosition(BusinessClock.MoscowToday().AddYears(3));
        SetupBankEntryForOpenPosition(gspread: 0.02m);
        _securityDetails.Setup(d => d.GetEmitentDefaultHistoryAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, EmitentDefaultHistory> { [777] = new(777, true, false) });

        await BuildServiceProvider().GetRequiredService<ISyncCycleRunner>().RunCycleAsync();

        _signals.Verify(s => s.InsertAsync(It.Is<Signal>(x =>
            x.Type == SignalType.IssuerDefaultEvent && x.Severity == SignalSeverity.Critical && x.Subject == "Тест-Эмитент")), Times.Once);
        _alertSender.Verify(a => a.SendAsync(It.Is<string>(m => m.Contains("Тест-Эмитент") && m.Contains("дефолт")), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunCycleAsync_IssuerDefaultAlreadyKnown_RefreshesWithoutSecondTelegramAlert()
    {
        SetupNoPositionsHappyPath();
        SetupOpenPosition(BusinessClock.MoscowToday().AddYears(3));
        SetupBankEntryForOpenPosition(gspread: 0.02m);
        _securityDetails.Setup(d => d.GetEmitentDefaultHistoryAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, EmitentDefaultHistory> { [777] = new(777, true, false) });
        _signals.Setup(s => s.GetByAccountIdAsync(AccountId, true)).ReturnsAsync(new List<Signal>
        {
            new() { Id = 5, AccountId = AccountId, EventKey = Signal.BuildKey(SignalType.IssuerDefaultEvent, "777:default", null), Type = SignalType.IssuerDefaultEvent },
        });

        await BuildServiceProvider().GetRequiredService<ISyncCycleRunner>().RunCycleAsync();

        _signals.Verify(s => s.InsertAsync(It.Is<Signal>(x => x.Type == SignalType.IssuerDefaultEvent)), Times.Never);
        _signals.Verify(s => s.RefreshAsync(5ul, SignalSeverity.Critical, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<DateTime>()), Times.Once);
        _alertSender.Verify(a => a.SendAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunCycleAsync_IssuerDefaultFlagCleared_ResolvesEvent()
    {
        SetupNoPositionsHappyPath();
        SetupOpenPosition(BusinessClock.MoscowToday().AddYears(3));
        SetupBankEntryForOpenPosition(gspread: 0.02m);
        // флага больше нет (агрегат false/false)
        _securityDetails.Setup(d => d.GetEmitentDefaultHistoryAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, EmitentDefaultHistory> { [777] = new(777, false, false) });
        _signals.Setup(s => s.GetByAccountIdAsync(AccountId, true)).ReturnsAsync(new List<Signal>
        {
            new() { Id = 5, AccountId = AccountId, EventKey = Signal.BuildKey(SignalType.IssuerDefaultEvent, "777:default", null), Type = SignalType.IssuerDefaultEvent },
        });

        await BuildServiceProvider().GetRequiredService<ISyncCycleRunner>().RunCycleAsync();

        _signals.Verify(s => s.ResolveAsync(It.Is<IReadOnlyCollection<ulong>>(ids => ids.Single() == 5ul), It.IsAny<DateTime>()), Times.Once);
    }

    [Fact]
    public async Task RunCycleAsync_SpreadWidening_InsertsWarning_FromBatchedHistory_WithoutTelegram()
    {
        SetupNoPositionsHappyPath();
        var today = BusinessClock.MoscowToday();
        SetupOpenPosition(today.AddYears(3));
        SetupBankEntryForOpenPosition(gspread: 0.0300m);
        // 12 прошлых торговых дней со спредом 0.0200 → сегодня +100 б.п. > 50 б.п.
        _universe.Setup(u => u.GetRecentHistoryForSecidsAsync(
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Range(1, 12).Select(d => new BondUniverseHistoryPoint
            {
                Secid = "SU49", SnapshotDate = today.AddDays(-d), GspreadEffectiveFraction = 0.0200m, PricePercent = 99m,
            }).ToList());

        await BuildServiceProvider().GetRequiredService<ISyncCycleRunner>().RunCycleAsync();

        _signals.Verify(s => s.InsertAsync(It.Is<Signal>(x => x.Type == SignalType.SpreadWidening && x.Severity == SignalSeverity.Warning)), Times.Once);
        _alertSender.Verify(a => a.SendAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _universe.Verify(u => u.GetByIsinsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
