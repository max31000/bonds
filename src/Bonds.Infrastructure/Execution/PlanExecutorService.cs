using Bonds.Core.Policy;
using Bonds.Infrastructure.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bonds.Infrastructure.Execution;

/// <summary>
/// Фоновый исполнитель планов (задача 65): тик <c>Execution:TickInterval</c> (20 с) → <see cref="PlanExecutionEngine.RunTickAsync"/> в отдельном scope. Работает всегда,
/// но заявки отправляет только при открытых воротах (по умолчанию закрыты: <c>Execution__Enabled=false</c>); без исполнимых планов тик — один SQL-запрос.
/// Сбой тика — <c>LogWarning</c> и пропуск, процесс не падает. Часы торгов проверяет сам движок по рынку каждой строки.
/// </summary>
public sealed class PlanExecutorService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<ExecutionOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<PlanExecutorService> _logger;

    public PlanExecutorService(IServiceScopeFactory scopeFactory, IOptionsMonitor<ExecutionOptions> options, TimeProvider time, ILogger<PlanExecutorService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Plan executor started (Execution:Enabled = {Enabled})", _options.CurrentValue.Enabled);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var engine = scope.ServiceProvider.GetRequiredService<PlanExecutionEngine>();
                var result = await engine.RunTickAsync(_time.GetUtcNow().UtcDateTime, stoppingToken);
                if (result.OrdersPosted + result.OrdersCancelled + result.PlansStarted + result.PlansFinished > 0)
                {
                    _logger.LogInformation(
                        "Plan executor tick: {Posted} posted, {Cancelled} cancelled, {Started} plan(s) started, {Finished} finished",
                        result.OrdersPosted, result.OrdersCancelled, result.PlansStarted, result.PlansFinished);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Plan executor tick failed");
            }

            try
            {
                await Task.Delay(_options.CurrentValue.TickInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}

/// <summary>Синк портфеля после исполнения: запускает полный цикл в фоне (<see cref="ISyncCycleRunner"/> сам отклоняет параллельный запуск).</summary>
public sealed class PortfolioSyncTrigger : IPortfolioSyncTrigger
{
    private readonly ISyncCycleRunner _runner;
    private readonly ILogger<PortfolioSyncTrigger> _logger;

    public PortfolioSyncTrigger(ISyncCycleRunner runner, ILogger<PortfolioSyncTrigger> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    public void Request() => _ = Task.Run(async () =>
    {
        try
        {
            await _runner.RunCycleAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Post-execution portfolio sync failed");
        }
    });
}
