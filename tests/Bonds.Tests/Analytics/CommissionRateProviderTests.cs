using Bonds.Core.Analytics;
using Bonds.Core.Interfaces;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Infrastructure.Analytics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Bonds.Tests.Analytics;

/// <summary>
/// Тесты резолвера эффективной ставки комиссии (plan/22 часть C) — контракт для задач 23/25.
/// Приоритет: override из настроек → оценка из журнала → дефолт. Репозитории замоканы.
/// </summary>
public class CommissionRateProviderTests
{
    private readonly Mock<IAccountRepository> _accounts = new();
    private readonly Mock<IUserSettingsRepository> _settings = new();
    private readonly Mock<IOperationRepository> _operations = new();

    private const ulong AccountId = 1;
    private const ulong UserId = 42;
    private static readonly DateTime AsOf = new(2026, 7, 6);

    private CommissionRateProvider CreateProvider() => new(
        _accounts.Object, _settings.Object, _operations.Object,
        NullLogger<CommissionRateProvider>.Instance,
        () => AsOf);

    private static Operation Op(OperationType type, DateTime date, decimal amountRub) => new()
    {
        AccountId = AccountId,
        Type = type,
        Date = date,
        AmountRub = amountRub,
        ExternalId = Guid.NewGuid().ToString(),
    };

    private void SetupAccount() =>
        _accounts.Setup(a => a.GetByIdAsync(AccountId)).ReturnsAsync(new Account { Id = AccountId, UserId = UserId });

    [Fact]
    public async Task GetAsync_OverrideSet_WinsOverEstimateAndDefault()
    {
        SetupAccount();
        _settings.Setup(s => s.GetByUserIdAsync(UserId))
            .ReturnsAsync(new UserSettings { UserId = UserId, CommissionRateOverride = 0.0005m });
        _operations.Setup(o => o.GetByAccountIdAsync(AccountId, null, null))
            .ReturnsAsync(new List<Operation>
            {
                Op(OperationType.Buy, AsOf.AddDays(-1), -100_000m),
                Op(OperationType.Fee, AsOf.AddDays(-1), -300m), // оценка была бы 0.3%, override должен победить
            });

        var provider = CreateProvider();
        var result = await provider.GetAsync(AccountId);

        result.Rate.Should().Be(0.0005m);
        result.Source.Should().Be(CommissionRateSource.UserOverride);
    }

    [Fact]
    public async Task GetAsync_NoOverride_UsesEstimateFromJournal()
    {
        SetupAccount();
        _settings.Setup(s => s.GetByUserIdAsync(UserId))
            .ReturnsAsync(new UserSettings { UserId = UserId, CommissionRateOverride = null });
        _operations.Setup(o => o.GetByAccountIdAsync(AccountId, null, null))
            .ReturnsAsync(new List<Operation>
            {
                Op(OperationType.Buy, AsOf.AddDays(-10), -100_000m),
                Op(OperationType.Fee, AsOf.AddDays(-10), -46m),
                Op(OperationType.Sell, AsOf.AddDays(-5), 50_000m),
                Op(OperationType.Fee, AsOf.AddDays(-5), -23m),
            });

        var provider = CreateProvider();
        var result = await provider.GetAsync(AccountId);

        result.Source.Should().Be(CommissionRateSource.EstimatedFromTrades);
        result.Rate.Should().BeApproximately(69m / 150_000m, 0.0000001m);
        result.Estimate.Should().NotBeNull();
    }

    [Fact]
    public async Task GetAsync_NoOverrideNoJournalData_FallsBackToDefault()
    {
        SetupAccount();
        _settings.Setup(s => s.GetByUserIdAsync(UserId)).ReturnsAsync((UserSettings?)null);
        _operations.Setup(o => o.GetByAccountIdAsync(AccountId, null, null))
            .ReturnsAsync(new List<Operation>());

        var provider = CreateProvider();
        var result = await provider.GetAsync(AccountId);

        result.Source.Should().Be(CommissionRateSource.Default);
        result.Rate.Should().Be(SwitchAnalysisService.DefaultCommissionRate);
        result.Estimate.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_AccountNotFound_FallsBackToDefault()
    {
        _accounts.Setup(a => a.GetByIdAsync(AccountId)).ReturnsAsync((Account?)null);

        var provider = CreateProvider();
        var result = await provider.GetAsync(AccountId);

        result.Source.Should().Be(CommissionRateSource.Default);
        result.Rate.Should().Be(SwitchAnalysisService.DefaultCommissionRate);
    }

    [Fact]
    public async Task GetAsync_OverrideIsZeroOrNull_DoesNotWinOverEstimate()
    {
        // Null override — очевидно "не задан". Обнуление явно проверяем отдельно ниже, чтобы
        // задокументировать: 0 недопустим по валидации API (часть D), но резолвер на всякий
        // случай не должен слепо доверять 0 как "явно заданной ставке" наравне с null.
        SetupAccount();
        _settings.Setup(s => s.GetByUserIdAsync(UserId))
            .ReturnsAsync(new UserSettings { UserId = UserId, CommissionRateOverride = null });
        _operations.Setup(o => o.GetByAccountIdAsync(AccountId, null, null))
            .ReturnsAsync(new List<Operation>
            {
                Op(OperationType.Buy, AsOf.AddDays(-10), -100_000m),
                Op(OperationType.Fee, AsOf.AddDays(-10), -46m),
            });

        var provider = CreateProvider();
        var result = await provider.GetAsync(AccountId);

        result.Source.Should().Be(CommissionRateSource.EstimatedFromTrades);
    }

    [Fact]
    public async Task GetAsync_OverrideIsZero_TreatedAsUnsetAndFallsBackToEstimate()
    {
        // Defense-in-depth: API-валидация не даёт сохранить 0, но если 0m всё же окажется в БД,
        // резолвер не должен выдать нулевую комиссию во все расчёты — трактует как "не задан".
        SetupAccount();
        _settings.Setup(s => s.GetByUserIdAsync(UserId))
            .ReturnsAsync(new UserSettings { UserId = UserId, CommissionRateOverride = 0m });
        _operations.Setup(o => o.GetByAccountIdAsync(AccountId, null, null))
            .ReturnsAsync(new List<Operation>
            {
                Op(OperationType.Buy, AsOf.AddDays(-10), -100_000m),
                Op(OperationType.Fee, AsOf.AddDays(-10), -46m),
            });

        var provider = CreateProvider();
        var result = await provider.GetAsync(AccountId);

        result.Source.Should().Be(CommissionRateSource.EstimatedFromTrades);
        result.Rate.Should().NotBe(0m);
    }
}
