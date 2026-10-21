using Bonds.Core.Models;

namespace Bonds.Core.Income;

/// <summary>Вид актива единого журнала доходов (задача 52): облигация или фонд (БПИФ).</summary>
public enum AssetKind
{
    Bond,
    Fund,
}

/// <summary>Нормализованный вид записи журнала — общий для облигаций и фондов.</summary>
public enum LedgerEntryKind
{
    Buy,
    Sell,

    /// <summary>Купон облигации / выплата фонда — БРУТТО (налог идёт отдельной записью <see cref="Tax"/>).</summary>
    Income,
    Tax,
    Fee,

    /// <summary>Амортизация облигации — возврат части тела (капитала), не доход.</summary>
    PrincipalReturn,

    /// <summary>Погашение облигации.</summary>
    Redemption,
    SecurityIn,
    SecurityOut,
}

/// <summary>
/// Запись единого журнала доходов. <see cref="AmountRub"/> — РУБЛИ со знаком брокера (покупка −, поступление +,
/// удержание налога −, возврат +) — НЕ переписывается. <see cref="AccruedRub"/> — НКД операции, РУБЛИ на всю
/// операцию, ВЕЛИЧИНА (≥ 0; направление из <see cref="Kind"/>), только Buy/Sell облигаций, иначе null.
/// <see cref="Quantity"/> — штуки/паи. <see cref="SourceId"/> — id строки <c>operations</c>/<c>fund_operations</c>
/// (стабильный порядок внутри одного момента времени). Операции без актива («по счёту») получают
/// <c>AssetId = 0</c> и живут в отдельном списке (<see cref="LedgerEntryMapper.AccountLevelKinds"/>).
/// </summary>
public sealed record LedgerEntry(
    AssetKind AssetKind,
    ulong AssetId,
    DateTime Date,
    LedgerEntryKind Kind,
    decimal AmountRub,
    decimal? Quantity,
    decimal? AccruedRub,
    ulong SourceId);

/// <summary>Маппинг доменных операций в записи единого журнала.</summary>
public static class LedgerEntryMapper
{
    /// <summary>Виды записей, которые учитываются «по счёту», если у операции нет актива (налог/комиссия).</summary>
    public static readonly IReadOnlySet<LedgerEntryKind> AccountLevelKinds =
        new HashSet<LedgerEntryKind> { LedgerEntryKind.Tax, LedgerEntryKind.Fee };

    public static LedgerEntryKind ToKind(OperationType type) => type switch
    {
        OperationType.Buy => LedgerEntryKind.Buy,
        OperationType.Sell => LedgerEntryKind.Sell,
        OperationType.Coupon => LedgerEntryKind.Income,
        OperationType.Amortization => LedgerEntryKind.PrincipalReturn,
        OperationType.Redemption => LedgerEntryKind.Redemption,
        OperationType.Tax => LedgerEntryKind.Tax,
        OperationType.Fee => LedgerEntryKind.Fee,
        OperationType.SecurityIn => LedgerEntryKind.SecurityIn,
        OperationType.SecurityOut => LedgerEntryKind.SecurityOut,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown operation type"),
    };

    public static LedgerEntryKind ToKind(FundOperationType type) => type switch
    {
        FundOperationType.Buy => LedgerEntryKind.Buy,
        FundOperationType.Sell => LedgerEntryKind.Sell,
        FundOperationType.Distribution => LedgerEntryKind.Income,
        FundOperationType.Tax => LedgerEntryKind.Tax,
        FundOperationType.Fee => LedgerEntryKind.Fee,
        FundOperationType.SecurityIn => LedgerEntryKind.SecurityIn,
        FundOperationType.SecurityOut => LedgerEntryKind.SecurityOut,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown fund operation type"),
    };

    /// <summary>Запись облигационного журнала. <c>AssetId</c> = <c>instrument_id</c> (0 — операция без бумаги).</summary>
    public static LedgerEntry FromOperation(Operation op)
    {
        var kind = ToKind(op.Type);
        return new LedgerEntry(
            AssetKind.Bond, op.InstrumentId ?? 0UL, op.Date, kind, op.AmountRub, op.Quantity,
            kind is LedgerEntryKind.Buy or LedgerEntryKind.Sell ? op.AccruedRub : null,
            op.Id);
    }

    /// <summary>Запись фондового журнала. <c>AssetId</c> = <c>fund_id</c> (0 — операция без фонда). НКД у паёв нет.</summary>
    public static LedgerEntry FromFundOperation(FundOperation op) =>
        new(AssetKind.Fund, op.FundId ?? 0UL, op.Date, ToKind(op.Type), op.AmountRub, op.Quantity, null, op.Id);
}
