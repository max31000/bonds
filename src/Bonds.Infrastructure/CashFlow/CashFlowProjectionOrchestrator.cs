using Bonds.Core.CashFlow;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Microsoft.Extensions.Logging;

namespace Bonds.Infrastructure.CashFlow;

/// <summary>
/// Оркестратор проекции денежного потока (plan/06 Часть A) — координирует репозитории,
/// собирает <see cref="PositionCashFlowInput"/> для каждой позиции счёта, вызывает чистый
/// <see cref="CashFlowProjectionService"/> и персистирует результат через
/// <see cref="IProjectedCashFlowRepository.ReplaceForPositionAsync"/>. Аналог
/// <c>BondSyncService</c> (этап 04) по духу: координация в Infrastructure, расчёт — в Core.
/// Не вызывается из HTTP (этап 08) и не планируется по расписанию (этап 07) — программный вызов.
/// </summary>
public sealed class CashFlowProjectionOrchestrator
{
    private readonly IPositionRepository _positions;
    private readonly IInstrumentRepository _instruments;
    private readonly ICouponScheduleRepository _coupons;
    private readonly IAmortizationScheduleRepository _amortizations;
    private readonly IOfferScheduleRepository _offers;
    private readonly IProjectedCashFlowRepository _projectedCashFlows;
    private readonly ILogger<CashFlowProjectionOrchestrator> _logger;

    public CashFlowProjectionOrchestrator(
        IPositionRepository positions,
        IInstrumentRepository instruments,
        ICouponScheduleRepository coupons,
        IAmortizationScheduleRepository amortizations,
        IOfferScheduleRepository offers,
        IProjectedCashFlowRepository projectedCashFlows,
        ILogger<CashFlowProjectionOrchestrator> logger)
    {
        _positions = positions;
        _instruments = instruments;
        _coupons = coupons;
        _amortizations = amortizations;
        _offers = offers;
        _projectedCashFlows = projectedCashFlows;
        _logger = logger;
    }

    /// <summary>
    /// Пересчитывает и сохраняет проекцию для всех позиций счёта. Идемпотентно: каждая позиция
    /// полностью заменяется (<see cref="IProjectedCashFlowRepository.ReplaceForPositionAsync"/>),
    /// повторный вызов безопасен. Один сбойный инструмент не должен ронять пересчёт остальных
    /// позиций (та же устойчивость к частичным сбоям, что и у <c>BondSyncService</c>).
    /// </summary>
    public async Task<ProjectionRunResult> ProjectAccountAsync(
        ulong accountId,
        DateOnly asOf,
        CashFlowHorizonMode horizonMode = CashFlowHorizonMode.ToNearestOffer,
        CancellationToken ct = default)
    {
        var result = new ProjectionRunResult();
        var positions = await _positions.GetByAccountIdAsync(accountId);

        foreach (var position in positions)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var flows = await ProjectPositionAsync(position, asOf, horizonMode);
                await _projectedCashFlows.ReplaceForPositionAsync(position.Id, flows);
                result.PositionsProjected++;
                result.FlowsWritten += flows.Count;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to project cash flow for position {PositionId} — skipping", position.Id);
                result.Errors.Add($"Позиция {position.Id}: ошибка проекции потока ({ex.GetType().Name})");
            }
        }

        return result;
    }

    /// <summary>Строит проекцию для одной позиции без персистентности (для предпросмотра/тестов вызывающего кода).</summary>
    public async Task<IReadOnlyList<ProjectedCashFlow>> ProjectPositionAsync(
        Position position,
        DateOnly asOf,
        CashFlowHorizonMode horizonMode = CashFlowHorizonMode.ToNearestOffer)
    {
        var instrument = await _instruments.GetByIdAsync(position.InstrumentId);
        if (instrument is null)
        {
            throw new InvalidOperationException($"Instrument {position.InstrumentId} not found for position {position.Id}");
        }

        var coupons = (await _coupons.GetByInstrumentIdAsync(position.InstrumentId)).ToList();
        var amortizations = (await _amortizations.GetByInstrumentIdAsync(position.InstrumentId)).ToList();
        var offers = (await _offers.GetByInstrumentIdAsync(position.InstrumentId)).ToList();

        var input = new PositionCashFlowInput
        {
            PositionId = position.Id,
            InstrumentId = instrument.Id,
            Quantity = position.Quantity,
            FaceValue = instrument.FaceValue,
            AsOf = asOf,
            MaturityDate = instrument.MaturityDate,
            CouponType = instrument.CouponType,
            IsOutOfScopeCurrency = instrument.IsOutOfScopeCurrency,
            Coupons = coupons,
            Amortizations = amortizations,
            Offers = offers,
            HorizonMode = horizonMode,
        };

        return CashFlowProjectionService.Project(input);
    }
}

/// <summary>Итог одного запуска пересчёта проекции по счёту — для логирования/отображения.</summary>
public sealed class ProjectionRunResult
{
    public int PositionsProjected { get; set; }
    public int FlowsWritten { get; set; }
    public List<string> Errors { get; } = [];

    public bool HasErrors => Errors.Count > 0;
}
