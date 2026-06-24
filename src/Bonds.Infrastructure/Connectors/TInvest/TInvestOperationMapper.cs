using Bonds.Core.Models;

namespace Bonds.Infrastructure.Connectors.TInvest;

/// <summary>
/// Маппинг строкового <see cref="TInvestOperation.OperationType"/> (имя значения protobuf-enum
/// <c>Tinkoff.InvestApi.V1.OperationType</c>, см. README.md — верификация контракта §12.2)
/// в доменный <see cref="OperationType"/>. Чистая функция — тестируется без gRPC (mock-фикстуры).
/// </summary>
public static class TInvestOperationMapper
{
    /// <summary>
    /// Возвращает доменный тип операции, либо null — если операция не релевантна журналу
    /// XIRR/денежного потока этого продукта (например, служебные комиссии за перевод между
    /// счетами, налоговые корректировки задвоенных начислений и т.п. — таких в protobuf-enum
    /// T-Invest ~50 значений, большинство не применимо к простому облигационному портфелю
    /// одного счёта; см. полный список в README.md). Вызывающий код (BondSyncService) решает,
    /// сохранять ли неизвестные типы как <see cref="Bonds.Core.Models.OperationType.Fee"/>
    /// (консервативный fallback, чтобы не терять деньги из журнала) или игнорировать.
    /// </summary>
    public static OperationType? Map(string tInvestOperationType) => tInvestOperationType switch
    {
        "Buy" or "BuyCard" or "BuyMargin" => OperationType.Buy,
        "Sell" or "SellCard" or "SellMargin" => OperationType.Sell,
        "Coupon" => OperationType.Coupon,
        "BondRepayment" => OperationType.Amortization,
        "BondRepaymentFull" => OperationType.Redemption,
        "BondTax" or "BondTaxProgressive" or "Tax" or "TaxProgressive"
            or "TaxCorrection" or "TaxCorrectionProgressive" or "TaxCorrectionCoupon"
            or "DividendTax" or "DividendTaxProgressive" or "BenefitTax" or "BenefitTaxProgressive" => OperationType.Tax,
        "BrokerFee" or "ServiceFee" or "MarginFee" or "CashFee" or "OutFee" or "OtherFee"
            or "AdviceFee" or "SuccessFee" or "TrackMfee" or "TrackPfee" or "OutStampDuty" => OperationType.Fee,
        // Прочие типы (переводы между своими счетами/секциями, вариационная маржа по фьючерсам,
        // дивиденды по акциям — вне скоупа продукта §3, прочие неприменимые к облигационному
        // портфелю одного счёта типы) — намеренно не маппятся, см. summary выше.
        _ => null,
    };
}
