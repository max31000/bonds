namespace Bonds.Infrastructure.Connectors.TInvest;

/// <summary>
/// Обёртка над официальным gRPC SDK <c>Tinkoff.InvestApi</c> (plan/04 Часть B) — "истина про счёт"
/// (позиции, операции, текущие цены/НКД). Вынесена за интерфейс, чтобы:
/// (1) тесты подставляли мок без реальных gRPC-вызовов (нет токена в этой среде, и в CI его
///     не будет — plan/04 Часть B п.7 "безопасность токена"); (2) <see cref="Bonds.Core.Sync.BondSyncService"/>
/// оставался независимым от деталей протобуф-контракта SDK.
/// Контракт T-Invest по облигациям верифицирован отражением сборки SDK (см. README.md рядом) —
/// амортизация/оферты в T-Invest отдаются только как флаги (Bond.AmortizationFlag/CallDate),
/// БЕЗ полного графика; полные графики берём из MOEX (часть A), это не баг, а ожидаемое поведение.
/// </summary>
public interface ITInvestPortfolioClient
{
    /// <summary>Идентификатор брокерского счёта, привязанного к токену (первый/единственный счёт — MVP §2).</summary>
    Task<string?> GetPrimaryAccountIdAsync(CancellationToken ct = default);

    /// <summary>Позиции портфеля (только облигации — другие типы инструментов отфильтрованы).</summary>
    Task<IReadOnlyList<TInvestPortfolioPosition>> GetBondPositionsAsync(string accountId, CancellationToken ct = default);

    /// <summary>
    /// Журнал операций постранично по курсору (GetOperationsByCursor — поддерживает инкрементальный
    /// синк: <paramref name="from"/> ограничивает диапазон снизу для повторных вызовов без полной
    /// перезагрузки истории, plan/04 Часть B п.6 "идемпотентность и инкрементальность").
    /// </summary>
    Task<IReadOnlyList<TInvestOperation>> GetOperationsAsync(string accountId, DateTime? from, CancellationToken ct = default);

    /// <summary>Маппинг FIGI облигации → ISIN (для резолва SECID через MOEX, часть A).</summary>
    Task<string?> GetIsinByFigiAsync(string figi, CancellationToken ct = default);

    /// <summary>Текущая цена/лучшие цены спроса-предложения по набору FIGI (открытые позиции).</summary>
    Task<IReadOnlyDictionary<string, TInvestQuote>> GetQuotesAsync(IReadOnlyCollection<string> figis, CancellationToken ct = default);

    /// <summary>
    /// Имя тарифа пользователя (<c>Users.GetInfo</c>, plan/22 часть B) — ТОЛЬКО для отображения в
    /// настройках (контекст для пользователя, "у вас тариф X"). T-Invest НЕ отдаёт числовую ставку
    /// комиссии тарифа нигде в API — этот метод не участвует в расчёте эффективной ставки
    /// (см. <see cref="Bonds.Core.Interfaces.ICommissionRateProvider"/>, который использует только
    /// override/оценку из журнала/дефолт). Ошибка гRPC или отсутствие токена — null, не исключение
    /// наружу (тариф не критичен для работы продукта).
    /// </summary>
    Task<TInvestTariffInfo?> GetUserTariffAsync(CancellationToken ct = default);
}

/// <summary>Тариф пользователя T-Invest (plan/22 часть B) — только контекст для UI, не влияет на расчёты.</summary>
public sealed record TInvestTariffInfo
{
    /// <summary>Имя тарифа (например, "Инвестор", "Трейдер") — как отдаёт GetInfoResponse.Tariff.</summary>
    public required string Tariff { get; init; }

    /// <summary>Признак премиального статуса (GetInfoResponse.PremStatus) — доп. контекст, не используется в расчётах.</summary>
    public required bool PremStatus { get; init; }
}
