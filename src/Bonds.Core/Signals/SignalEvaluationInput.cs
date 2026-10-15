using Bonds.Core.Analytics;
using Bonds.Core.Models;

namespace Bonds.Core.Signals;

/// <summary>
/// Одна ОТКРЫТАЯ позиция с привязанными расписаниями — вход правил приближающихся событий
/// (оферта/погашение/амортизация/пересчёт ставки флоатера). Несёт сырые расписания (а не только
/// метрики), потому что правилам нужны конкретные даты.
/// </summary>
public sealed record SignalPositionContext
{
    public required ulong PositionId { get; init; }
    public required ulong InstrumentId { get; init; }
    public required string? Issuer { get; init; }
    public string? Name { get; init; }
    public required DateOnly MaturityDate { get; init; }

    /// <summary>Штук бумаг в позиции. Позиция с Quantity &lt;= 0 (закрытая) не порождает событий.</summary>
    public decimal Quantity { get; init; } = 1m;

    /// <summary>Тип купона инструмента. Пересчёт ставки (FloaterRateReset) — только для <see cref="CouponType.Floating"/>.</summary>
    public CouponType CouponType { get; init; } = CouponType.Fixed;

    public IReadOnlyList<CouponSchedule> Coupons { get; init; } = Array.Empty<CouponSchedule>();
    public IReadOnlyList<AmortizationSchedule> Amortizations { get; init; } = Array.Empty<AmortizationSchedule>();
    public IReadOnlyList<OfferSchedule> Offers { get; init; } = Array.Empty<OfferSchedule>();
}

/// <summary>
/// Рыночный контекст одной ОТКРЫТОЙ позиции для правил SpreadWidening/PriceDrawdown (задача 49): сегодняшние
/// значения из банка облигаций (<c>bond_universe</c>) и история <c>bond_universe_history</c> за прошлые торговые дни.
/// Позиции без банк-записи (неторгуемая бумага) контекста не имеют — правила их пропускают.
/// Единицы: G-спред — ДОЛЯ (0.0035 = 35 б.п.), цена — % от номинала (как MOEX).
/// </summary>
public sealed record PositionMarketContext
{
    public required ulong InstrumentId { get; init; }

    /// <summary>ISIN бумаги — основа EventKey рыночных событий (стабилен, в отличие от локального id). Null — ключ строится по id инструмента.</summary>
    public string? Isin { get; init; }

    /// <summary>Флоатер/индексируемая: G-спред несравним (доходность — текущая, не к погашению) — SpreadWidening пропускается.</summary>
    public bool IsFloater { get; init; }

    /// <summary>Эффективный G-спред сегодня (<c>BondUniverseEntry.EffectiveGspreadFraction</c>), ДОЛЯ. Null — неизвестен.</summary>
    public decimal? GSpreadFraction { get; init; }

    /// <summary>G-спреды прошлых торговых дней (до 20, БЕЗ сегодняшнего), ДОЛИ.</summary>
    public IReadOnlyList<decimal> GSpreadHistory { get; init; } = [];

    /// <summary>Чистая цена сегодня, % от номинала. Null — неизвестна.</summary>
    public decimal? PricePercent { get; init; }

    /// <summary>Чистые цены прошлых торговых дней (до 20, БЕЗ сегодняшней), % от номинала.</summary>
    public IReadOnlyList<decimal> PriceHistory { get; init; } = [];
}

/// <summary>
/// Дефолт-агрегат эмитента позиции (<c>security_details</c>, по ВСЕМ выпускам эмитента) для правила
/// <see cref="Bonds.Core.Models.SignalType.IssuerDefaultEvent"/> (задача 49). Строится только для эмитентов, у которых
/// хотя бы один флаг = 1 («неизвестно» = null — НЕ дефолт, события не рождает).
/// </summary>
public sealed record IssuerDefaultContext
{
    /// <summary>EMITTER_ID справочника эмитентов MOEX — основа EventKey.</summary>
    public required int EmitentId { get; init; }

    /// <summary>Инструмент одной из открытых позиций этого эмитента (ссылка карточки события).</summary>
    public required ulong InstrumentId { get; init; }

    /// <summary>Название эмитента для карточки события.</summary>
    public string? Subject { get; init; }

    public bool HasDefault { get; init; }
    public bool HasTechnicalDefault { get; init; }
}

/// <summary>
/// Вход для <see cref="SignalsEngine.Evaluate"/> — плоские модели, никаких репозиториев/БД/HTTP.
/// Сборка — ответственность Infrastructure (<c>SyncCycleService</c>), holdings — через
/// <c>PortfolioHoldingsBuilder</c>.
/// </summary>
public sealed record SignalEvaluationInput
{
    public required ulong AccountId { get; init; }
    public required DateOnly AsOf { get; init; }

    /// <summary>Открытые позиции с расписаниями (правила оферт/погашения/амортизации/ресета).</summary>
    public IReadOnlyList<SignalPositionContext> Positions { get; init; } = Array.Empty<SignalPositionContext>();

    /// <summary>Holdings (рыночная стоимость + эмитент) — вход правила концентрации.</summary>
    public IReadOnlyList<PortfolioHolding> Holdings { get; init; } = Array.Empty<PortfolioHolding>();

    /// <summary>
    /// Реальные свободные деньги счёта в РУБЛЯХ (<c>accounts.cash_rub</c> по данным брокера). Null —
    /// баланс ещё не синкался: правило молчит (а не выдумывает остаток по журналу).
    /// </summary>
    public decimal? CashRub { get; init; }

    /// <summary>Рыночные контексты открытых позиций (задача 49): вход SpreadWidening/PriceDrawdown. Пусто — правила молчат.</summary>
    public IReadOnlyList<PositionMarketContext> MarketContexts { get; init; } = Array.Empty<PositionMarketContext>();

    /// <summary>Эмитенты открытых позиций с признаком дефолта/техдефолта (задача 49): вход IssuerDefaultEvent.</summary>
    public IReadOnlyList<IssuerDefaultContext> IssuerDefaults { get; init; } = Array.Empty<IssuerDefaultContext>();

    /// <summary>TargetAllocation счёта (может быть пустым). Используется только для персональных лимитов концентрации.</summary>
    public IReadOnlyList<TargetAllocation> TargetAllocations { get; init; } = Array.Empty<TargetAllocation>();

    public SignalEngineOptions Options { get; init; } = new();
}
