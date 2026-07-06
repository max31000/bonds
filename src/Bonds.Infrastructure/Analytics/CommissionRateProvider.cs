using Bonds.Core.Analytics;
using Bonds.Core.Interfaces;
using Bonds.Core.Interfaces.Repositories;
using Microsoft.Extensions.Logging;

namespace Bonds.Infrastructure.Analytics;

/// <summary>
/// Реализация <see cref="ICommissionRateProvider"/> (plan/22 часть C) — единая точка резолва
/// эффективной ставки комиссии для счёта, приоритет описан в doc-comment интерфейса:
/// override из настроек пользователя → оценка из журнала (<see cref="CommissionRateEstimator"/>) →
/// <see cref="SwitchAnalysisService.DefaultCommissionRate"/>.
/// <para>
/// Счёт → пользователь резолвится через <see cref="IAccountRepository.GetByIdAsync(ulong)"/>
/// (без явного UserId — вызывающий HTTP-слой уже проверил принадлежность счёта через JWT перед
/// тем, как узнать AccountId, см. <c>PositionsEndpoints.ResolveAccountIdAsync</c>). Счёт не найден —
/// деградация на дефолт, не исключение (та же философия, что у остальных аналитических эндпоинтов
/// при отсутствии данных).
/// </para>
/// </summary>
public sealed class CommissionRateProvider : ICommissionRateProvider
{
    private readonly IAccountRepository _accounts;
    private readonly IUserSettingsRepository _settings;
    private readonly IOperationRepository _operations;
    private readonly ILogger<CommissionRateProvider> _logger;
    private readonly Func<DateTime> _now;

    public CommissionRateProvider(
        IAccountRepository accounts,
        IUserSettingsRepository settings,
        IOperationRepository operations,
        ILogger<CommissionRateProvider> logger)
        : this(accounts, settings, operations, logger, () => DateTime.UtcNow)
    {
    }

    /// <summary>Конструктор с внедряемыми часами — только для юнит-тестов (детерминированный <c>asOf</c> для оценки из журнала).</summary>
    internal CommissionRateProvider(
        IAccountRepository accounts,
        IUserSettingsRepository settings,
        IOperationRepository operations,
        ILogger<CommissionRateProvider> logger,
        Func<DateTime> now)
    {
        _accounts = accounts;
        _settings = settings;
        _operations = operations;
        _logger = logger;
        _now = now;
    }

    public async Task<ResolvedCommissionRate> GetAsync(ulong accountId, CancellationToken ct = default)
    {
        var account = await _accounts.GetByIdAsync(accountId);
        if (account is null)
        {
            _logger.LogWarning("CommissionRateProvider: account {AccountId} not found, falling back to default rate", accountId);
            return new ResolvedCommissionRate(SwitchAnalysisService.DefaultCommissionRate, CommissionRateSource.Default, null);
        }

        var settings = await _settings.GetByUserIdAsync(account.UserId);
        if (settings?.CommissionRateOverride is decimal overrideRate)
        {
            return new ResolvedCommissionRate(overrideRate, CommissionRateSource.UserOverride, null);
        }

        var operations = (await _operations.GetByAccountIdAsync(accountId)).ToList();
        var estimate = CommissionRateEstimator.Estimate(operations, _now());
        if (estimate is not null)
        {
            return new ResolvedCommissionRate(estimate.Rate, CommissionRateSource.EstimatedFromTrades, estimate);
        }

        return new ResolvedCommissionRate(SwitchAnalysisService.DefaultCommissionRate, CommissionRateSource.Default, null);
    }
}
