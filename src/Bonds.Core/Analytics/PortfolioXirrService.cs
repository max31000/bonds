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
    /// <paramref name="asOf"/>. Знак потока берётся напрямую из <see cref="Operation.AmountRub"/>
    /// — это единственный источник истины (см. doc-comment на <see cref="SignedAmount"/> почему
    /// тип операции больше не используется для перезаписи знака). Терминальный поток
    /// (<paramref name="currentMarketValueRub"/>) добавляется как приток на <paramref name="asOf"/> —
    /// таким же образом, как если бы позиция была продана по рынку сегодня (стандартная конвенция
    /// расчёта XIRR "по факту + недореализованная стоимость").
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
    /// Знак суммы операции для XIRR — берётся НАПРЯМУЮ из <see cref="Operation.AmountRub"/>,
    /// без перезаписи по <see cref="Operation.Type"/>.
    /// <para>
    /// <b>Пересмотрено при ревью этапов 04-06.</b> Раньше эта функция игнорировала фактический
    /// знак <c>AmountRub</c> через <c>Math.Abs()</c> и принудительно переписывала его по типу
    /// операции (Buy/Tax/Fee → минус, остальное → плюс). Это было избыточно и рискованно:
    /// синк T-Invest (<see cref="Bonds.Infrastructure.Sync.BondSyncService"/>, этап 04) пишет
    /// <c>AmountRub = op.PaymentRub</c> — поле <c>Payment</c> протобуф-контракта T-Invest,
    /// которое брокер **уже отдаёт со знаком потока** (см.
    /// Bonds.Infrastructure/Connectors/TInvest/README.md, раздел "Журнал операций": "Payment
    /// (MoneyValue) — сумма операции со знаком (потоки)"), без какой-либо последующей модификации
    /// знака в коде синка. Иметь два независимых места, определяющих знак одной и той же величины
    /// (брокер — здесь, и тип операции — там), — источник скрытого расхождения: если синк
    /// когда-нибудь некорректно проставит знак, проверка по Abs()+тип молча скрыла бы эту ошибку
    /// вместо того, чтобы её проявить. Кроме того, принудительная перезапись была математически
    /// неверна для корректирующих операций: <see cref="Bonds.Infrastructure.Connectors.TInvest.TInvestOperationMapper"/>
    /// мапит "TaxCorrection"/"TaxCorrectionProgressive"/"TaxCorrectionCoupon" в тот же
    /// <see cref="OperationType.Tax"/> — а корректировка налога может быть и возвратом
    /// (положительной), которую принудительный минус превращал бы в отток ошибочно.
    /// Единственный источник истины по знаку теперь — брокер (через <c>AmountRub</c> как
    /// есть); тип операции используется только для исключения нерелевантных типов (сейчас —
    /// не используется вовсе, оставлен параметр для обратной совместимости сигнатуры).
    /// </para>
    /// </summary>
    private static decimal SignedAmount(Operation op) => op.AmountRub;
}
