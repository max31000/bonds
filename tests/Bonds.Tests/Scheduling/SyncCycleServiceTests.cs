using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Core.Signals;
using Bonds.Infrastructure.Analytics;
using Bonds.Infrastructure.CashFlow;
using Bonds.Infrastructure.Connectors.Moex;
using Bonds.Infrastructure.Connectors.TInvest;
using Bonds.Infrastructure.Scheduling;
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
    private readonly Mock<ITargetAllocationRepository> _targetAllocations = new();

    private void SetupNoPositionsHappyPath()
    {
        _accounts.Setup(a => a.GetPrimaryAccountIdAsync()).ReturnsAsync(AccountId);

        _tInvest.Setup(c => c.GetPrimaryAccountIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(BrokerAccountId);
        _tInvest.Setup(c => c.GetBondPositionsAsync(BrokerAccountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TInvestPortfolioPosition>());
        _tInvest.Setup(c => c.GetOperationsAsync(BrokerAccountId, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TInvestOperation>());
        _tInvest.Setup(c => c.GetQuotesAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, TInvestQuote>());
        _moex.Setup(m => m.GetYieldCurveAsync(It.IsAny<CancellationToken>())).ReturnsAsync((YieldCurveSnapshot?)null);

        _positions.Setup(p => p.GetByAccountIdAsync(AccountId)).ReturnsAsync(new List<Position>());
        _operations.Setup(o => o.GetByAccountIdAsync(AccountId, null, null)).ReturnsAsync(new List<Operation>());
        _targetAllocations.Setup(t => t.GetByAccountIdAsync(AccountId)).ReturnsAsync(new List<TargetAllocation>());
        _signals.Setup(s => s.GetByAccountIdAsync(AccountId, false)).ReturnsAsync(new List<Signal>());
        _yieldCurve.Setup(y => y.GetLatestAsync()).ReturnsAsync((YieldCurveSnapshot?)null);

        _snapshots.Setup(s => s.UpsertAsync(It.IsAny<PortfolioValueSnapshot>())).Returns(Task.CompletedTask);
    }

    private ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();

        services.AddSingleton(_accounts.Object);
        services.AddSingleton(_tInvest.Object);
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
        services.AddSingleton(_targetAllocations.Object);

        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));

        services.AddScoped<BondSyncService>();
        services.AddScoped<CashFlowProjectionOrchestrator>();
        services.AddScoped<PortfolioSnapshotService>();

        services.AddSingleton<IOptions<SignalEngineOptions>>(Options.Create(new SignalEngineOptions()));
        services.AddSingleton<ISyncCycleRunner, SyncCycleService>();

        return services.BuildServiceProvider();
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
            MaturityDate = new DateOnly(2026, 7, 1), // близко к "сейчас" → должен сработать UpcomingRedemption
            CouponType = CouponType.Fixed,
        };
        _instruments.Setup(i => i.GetByIdAsync(5)).ReturnsAsync(instrument);
        _coupons.Setup(c => c.GetByInstrumentIdAsync(5)).ReturnsAsync(new List<CouponSchedule>());
        _amortizations.Setup(a => a.GetByInstrumentIdAsync(5)).ReturnsAsync(new List<AmortizationSchedule>());
        _offers.Setup(o => o.GetByInstrumentIdAsync(5)).ReturnsAsync(new List<OfferSchedule>());
        _quotes.Setup(q => q.GetLatestAsync(5)).ReturnsAsync((MarketQuote?)null);

        ulong? createdSignalAccountId = null;
        _signals.Setup(s => s.CreateAsync(It.IsAny<Signal>()))
            .Callback<Signal>(sig => createdSignalAccountId = sig.AccountId)
            .ReturnsAsync(1ul);

        var sp = BuildServiceProvider();
        var runner = sp.GetRequiredService<ISyncCycleRunner>();

        var result = await runner.RunCycleAsync();

        result.SignalsCreated.Should().BeGreaterThan(0, "погашение в ближайшие 14 дней должно сгенерировать UpcomingRedemption");
        createdSignalAccountId.Should().Be(AccountId);
        _signals.Verify(s => s.CreateAsync(It.Is<Signal>(sig => sig.Type == SignalType.UpcomingRedemption)), Times.Once);
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
}
