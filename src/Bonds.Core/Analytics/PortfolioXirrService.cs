using Bonds.Core.Calculation;
using Bonds.Core.Models;

namespace Bonds.Core.Analytics;

/// <summary>
/// XIRR портфеля (plan/06 B1, spec §6.9) — внутренняя норма доходности по журналу
/// <see cref="Operation"/> владельца (фактические налоги/комиссии из T-Invest) плюс терминальный
/// поток текущей рыночной стоимости. Чистый сервис над <see cref="XirrCalculator"/> движка
/// (этап 05) — не пересчитывает решатель, только выставляет конвенцию знака денежного потока
/// и собирает терминальную точку. Сборка журнала из <see cref="Bonds.Core.Interfaces.Repositories.IOperationRepository"/>
/// — ответственность вызывающего слоя (Infrastructure).
/// </summary>
public static class PortfolioXirrService
{
    /// <summary>
    /// Считает XIRR портфеля/позиции по журналу операций + терминальная текущая стоимость на
    /// <paramref name="asOf"/>. Конвенция знака (spec §6.9, §5 doc-comment <see cref="Operation"/>):
    /// Buy/Tax/Fee — отток (минус), Sell/Coupon/Amortization/Redemption — приток (плюс).
    /// Терминальный поток (<paramref name="currentMarketValueRub"/>) добавляется как приток на
    /// <paramref name="asOf"/> — таким же образом, как если бы позиция была продана по рынку
    /// сегодня (стандартная конвенция расчёта XIRR "по факту + недореализованная стоимость").
    /// </summary>
    /// <returns>Null, если операций недостаточно или решатель не сходится (см. <see cref="XirrCalculator"/>).</returns>
    public static XirrCalculator.XirrResult? Calculate(
        IEnumerable<Operation> operations,
        decimal currentMarketValueRub,
        DateOnly asOf)
    {
        var flows = new List<XirrCalculator.CashFlow>();

        foreach (var op in operations)
        {
            var signedAmount = SignedAmount(op);
            if (signedAmount == 0m) continue;

            flows.Add(new XirrCalculator.CashFlow(DateOnly.FromDateTime(op.Date), signedAmount));
        }

        if (currentMarketValueRub != 0m)
        {
            flows.Add(new XirrCalculator.CashFlow(asOf, currentMarketValueRub));
        }

        return XirrCalculator.Calculate(flows);
    }

    /// <summary>
    /// Выставляет знак суммы операции по конвенции XIRR. <see cref="Operation.AmountRub"/> хранится
    /// "как пришло от брокера" (см. doc-comment модели) — T-Invest обычно уже отдаёт отрицательные
    /// суммы для платежей со счёта (покупка/налог/комиссия) и положительные для поступлений, но
    /// чтобы не зависеть от этого соглашения брокера, нормализуем явно по знаку через Abs() и
    /// тип операции — это снимает неоднозначность и делает расчёт детерминированным независимо
    /// от того, как именно T-Invest проставил знак на конкретном типе операции.
    /// </summary>
    private static decimal SignedAmount(Operation op)
    {
        var magnitude = Math.Abs(op.AmountRub);

        return op.Type switch
        {
            OperationType.Buy => -magnitude,
            OperationType.Tax => -magnitude,
            OperationType.Fee => -magnitude,
            OperationType.Sell => magnitude,
            OperationType.Coupon => magnitude,
            OperationType.Amortization => magnitude,
            OperationType.Redemption => magnitude,
            _ => 0m,
        };
    }
}
