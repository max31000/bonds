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
        // Переводы бумаг между счетами/брокерами (имена enum сверены рефлексией по SDK 0.6.22):
        // не покупка/продажа — меняют количество без движения денег.
        "InputSecurities" => OperationType.SecurityIn,
        "OutputSecurities" => OperationType.SecurityOut,
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

/// <summary>
/// Задача 51: маппинг <see cref="TInvestOperation.OperationType"/> (имя значения protobuf-enum
/// <c>Tinkoff.InvestApi.V1.OperationType</c>, имена сверены по SDK 0.6.22.1) в <see cref="FundOperationType"/>
/// для операций по фондам. Чистая функция. Отличие от <see cref="TInvestOperationMapper"/>: выплаты фонда приходят как
/// дивидендные типы (<c>Dividend</c>, <c>DividendTransfer</c>, <c>DivExt</c>) и мапятся в <see cref="FundOperationType.Distribution"/>;
/// купонов/погашений у фонда нет. Налоговые типы (включая <c>DividendTax*</c>) → Tax, комиссионные → Fee.
/// </summary>
public static class TInvestFundOperationMapper
{
    public static FundOperationType? Map(string tInvestOperationType) => tInvestOperationType switch
    {
        "Buy" or "BuyCard" or "BuyMargin" => FundOperationType.Buy,
        "Sell" or "SellCard" or "SellMargin" => FundOperationType.Sell,
        "Dividend" or "DividendTransfer" or "DivExt" => FundOperationType.Distribution,
        "InputSecurities" => FundOperationType.SecurityIn,
        "OutputSecurities" => FundOperationType.SecurityOut,
        "BondTax" or "BondTaxProgressive" or "Tax" or "TaxProgressive"
            or "TaxCorrection" or "TaxCorrectionProgressive" or "TaxCorrectionCoupon"
            or "DividendTax" or "DividendTaxProgressive" or "BenefitTax" or "BenefitTaxProgressive" => FundOperationType.Tax,
        "BrokerFee" or "ServiceFee" or "MarginFee" or "CashFee" or "OutFee" or "OtherFee"
            or "AdviceFee" or "SuccessFee" or "TrackMfee" or "TrackPfee" or "OutStampDuty" => FundOperationType.Fee,
        _ => null,
    };
}

/// <summary>
/// Задача 55: маппинг <see cref="TInvestOperation.OperationType"/> (имя значения protobuf-enum <c>Tinkoff.InvestApi.V1.OperationType</c>,
/// имена сверены по SDK 0.6.22.1 — XML-документация) в <see cref="CurrencyOperationType"/> для операций по валюте
/// (<see cref="TInvestOperation.IsCurrency"/>). Чистая функция. Покупка/продажа валюты (<c>Buy*</c>/<c>Sell*</c>; <c>DeliveryBuy</c>/<c>DeliverySell</c> —
/// поставочные фьючерсы, не берутся) → Buy/Sell; налоговые → Tax; комиссионные → Fee.
/// <para>
/// <b>Income (проценты на остаток) намеренно НЕ мапится</b>: подходящие по смыслу <c>OverIncome</c> («доход от овернайта») и
/// <c>Overnight</c> («доход по РЕПО овернайт») в SDK описаны как доходы по денежным операциям счёта, а не по валютному инструменту,
/// и приходят без привязки к валюте — уверенности, что они относятся к валютной позиции, нет. <c>AccruingVarmargin</c>/
/// <c>WritingOffVarmargin</c> — вариационная маржа по фьючерсам — не берутся. Поддержка <see cref="CurrencyOperationType.Income"/>
/// в журнале и отчёте есть и включится добавлением типа сюда.
/// </para>
/// </summary>
public static class TInvestCurrencyOperationMapper
{
    public static CurrencyOperationType? Map(string tInvestOperationType) => tInvestOperationType switch
    {
        "Buy" or "BuyCard" or "BuyMargin" => CurrencyOperationType.Buy,
        "Sell" or "SellCard" or "SellMargin" => CurrencyOperationType.Sell,
        "BondTax" or "BondTaxProgressive" or "Tax" or "TaxProgressive"
            or "TaxCorrection" or "TaxCorrectionProgressive" or "TaxCorrectionCoupon"
            or "DividendTax" or "DividendTaxProgressive" or "BenefitTax" or "BenefitTaxProgressive" => CurrencyOperationType.Tax,
        "BrokerFee" or "ServiceFee" or "MarginFee" or "CashFee" or "OutFee" or "OtherFee"
            or "AdviceFee" or "SuccessFee" or "TrackMfee" or "TrackPfee" or "OutStampDuty" => CurrencyOperationType.Fee,
        _ => null,
    };
}

/// <summary>
/// Задача 61: маппинг <see cref="TInvestOperation.OperationType"/> (имя значения protobuf-enum <c>Tinkoff.InvestApi.V1.OperationType</c>, имена сверены
/// по SDK 0.6.22.1) в <see cref="CashMovementType"/> — пополнения и выводы денег по счёту. Чистая функция.
/// <list type="bullet">
/// <item><c>Input</c> («Пополнение брокерского счёта»), <c>InputAcquiring</c> (пополнение картой), <c>InputSwift</c> (SWIFT-перевод) → Deposit.</item>
/// <item><c>Output</c> («Вывод денежных средств»), <c>OutputAcquiring</c>, <c>OutputSwift</c> → Withdrawal.</item>
/// </list>
/// Намеренно НЕ мапятся: <c>InputSecurities</c>/<c>OutputSecurities</c> (перевод БУМАГ, не денег), <c>InpMulti</c>/<c>OutMulti</c> (внутренний перевод
/// между своими счетами/секциями), <c>OutputPenalty</c> (штраф) — не внешние пополнения и выводы капитала.
/// </summary>
public static class TInvestCashMovementMapper
{
    public static CashMovementType? Map(string tInvestOperationType) => tInvestOperationType switch
    {
        "Input" or "InputAcquiring" or "InputSwift" => CashMovementType.Deposit,
        "Output" or "OutputAcquiring" or "OutputSwift" => CashMovementType.Withdrawal,
        _ => null,
    };
}
