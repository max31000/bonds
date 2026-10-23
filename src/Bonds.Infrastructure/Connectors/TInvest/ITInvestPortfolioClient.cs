namespace Bonds.Infrastructure.Connectors.TInvest;

/// <summary>
/// Обёртка над официальным gRPC SDK <c>Tinkoff.InvestApi</c> — "истина про счёт"
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

    /// <summary>
    /// Снимок портфеля: облигационные позиции (другие типы инструментов отфильтрованы) + денежные
    /// итоги счёта (свободные деньги, полная стоимость по оценке брокера) — один вызов GetPortfolio.
    /// </summary>
    Task<TInvestPortfolio> GetPortfolioAsync(string accountId, CancellationToken ct = default);

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
    /// Имя тарифа пользователя (<c>Users.GetInfo</c>) — ТОЛЬКО для отображения в
    /// настройках (контекст для пользователя, "у вас тариф X"). T-Invest НЕ отдаёт числовую ставку
    /// комиссии тарифа нигде в API — этот метод не участвует в расчёте эффективной ставки
    /// (см. <see cref="Bonds.Core.Interfaces.ICommissionRateProvider"/>, который использует только
    /// override/оценку из журнала/дефолт). Ошибка гRPC или отсутствие токена — null, не исключение
    /// наружу (тариф не критичен для работы продукта).
    /// </summary>
    Task<TInvestTariffInfo?> GetUserTariffAsync(CancellationToken ct = default);

    /// <summary>
    /// Задача 46: ОДИН bulk-вызов <c>Instruments.Bonds()</c> (<c>INSTRUMENT_STATUS_BASE</c>) → пары (ISIN, risk_level)
    /// всех облигаций, доступных для торговли через API. <c>Bond.risk_level</c> подтверждён официальным proto
    /// (instruments.proto:502, enum 0–3). Бумаги без ISIN пропускаются. Токен не настроен → <see cref="InvalidOperationException"/>
    /// (как у остальных методов клиента), сбой gRPC → исключение наружу (вызывающий шаг синка логирует и пропускает).
    /// </summary>
    Task<IReadOnlyList<TInvestBondRiskLevel>> GetBondRiskLevelsAsync(CancellationToken ct = default);

    /// <summary>
    /// Задача 51: справочник фонда по FIGI — <c>EtfBy(FIGI)</c> (isin/ticker/name/focus_type/asset_uid), затем
    /// <c>GetAssetBy(asset_uid)</c> → <c>AssetEtf.TotalExpense</c> (TER; конверсия в ДОЛЮ — <see cref="TInvestNumeric.ExpenseRatioToFraction"/>).
    /// Сбой второго вызова — TER null, не ошибка. Фонд не найден — null; токен не настроен → <see cref="InvalidOperationException"/>.
    /// </summary>
    Task<TInvestFundInfo?> GetFundInfoAsync(string figi, CancellationToken ct = default);

    /// <summary>
    /// Задача 51: выплаты фонда на пай за период (<c>GetDividends</c>; <c>DividendNet</c> — рубли за пай). Ошибка gRPC →
    /// пустой список + LogWarning (рыночные данные необязательны). Токен не настроен → <see cref="InvalidOperationException"/>.
    /// </summary>
    Task<IReadOnlyList<TInvestFundDistribution>> GetFundDistributionsAsync(string figi, DateOnly from, DateOnly to, CancellationToken ct = default);

    /// <summary>
    /// Задача 55: справочник валюты по FIGI — <c>CurrencyBy(FIGI)</c> (<c>IsoCurrencyName</c>, ticker, name, lot). Валюта не найдена /
    /// сбой gRPC — null (+LogWarning); токен не настроен → <see cref="InvalidOperationException"/>.
    /// </summary>
    Task<TInvestCurrencyInfo?> GetCurrencyInfoAsync(string figi, CancellationToken ct = default);

    /// <summary>
    /// Задача 61: ВСЕ листинги облигации/фонда по тикеру или ISIN (<paramref name="query"/>, совпадение точное, регистр не важен) — по одному на
    /// <c>classCode</c>. <c>FindInstrument</c> → для каждого совпавшего <c>BondBy</c>/<c>EtfBy</c> по uid (лот, шаг цены, флаги, валюта). Листинг,
    /// чьи детали получить не удалось, пропускается (+LogWarning). Выбор режима торгов — <see cref="Bonds.Core.Trading.TradingListingSelector"/>.
    /// Токен не настроен → <see cref="InvalidOperationException"/>, сбой самого поиска → исключение наружу.
    /// </summary>
    Task<IReadOnlyList<TInvestInstrumentListing>> GetInstrumentListingsAsync(string query, CancellationToken ct = default);

    /// <summary>
    /// Задача 61: число выплат (<c>GetDividends</c>, <c>DividendNet &gt; 0</c>) по инструменту (FIGI или uid) за период; <b>null — не удалось
    /// узнать</b> (сбой gRPC), в отличие от <see cref="GetFundDistributionsAsync"/>, который при сбое отдаёт пустой список: «нет выплат» и «не знаем» здесь
    /// различаются, иначе сбой превратился бы в <c>pays_distributions = 0</c>.
    /// </summary>
    Task<int?> CountDistributionsAsync(string instrumentId, DateOnly from, DateOnly to, CancellationToken ct = default);
}

/// <summary>
/// Листинг облигации/фонда на одном режиме торгов (задача 61). <see cref="MinPriceIncrementRaw"/> — шаг цены КАК ОТДАЁТ БРОКЕР: у облигации в
/// ПУНКТАХ (процент номинала, как цены <c>GetLastPrices</c>), у фонда — в рублях за пай; <see cref="Nominal"/> + <see cref="NominalCurrency"/> — текущий номинал
/// облигации и его валюта (null у фонда). Сетка цен облигации T-Invest — в пунктах: рублёвый шаг считается только при номинале в RUB.
/// </summary>
public sealed record TInvestInstrumentListing
{
    public required string Uid { get; init; }
    public required string Figi { get; init; }
    public required string Ticker { get; init; }
    public required string ClassCode { get; init; }
    public string? Isin { get; init; }

    /// <summary>true — облигация, false — фонд (<c>Etf</c>).</summary>
    public required bool IsBond { get; init; }

    public decimal Lot { get; init; }
    public decimal MinPriceIncrementRaw { get; init; }
    public decimal? Nominal { get; init; }

    /// <summary>ISO-код валюты номинала ВЕРХНИМ регистром (null у фонда); может отличаться от <see cref="Currency"/> (валюта торгов).</summary>
    public string? NominalCurrency { get; init; }

    /// <summary>ISO-код ВЕРХНИМ регистром.</summary>
    public required string Currency { get; init; }

    public bool ApiTradeAvailable { get; init; }
    public bool BuyAvailable { get; init; }
    public bool SellAvailable { get; init; }
}

/// <summary>Брокерская оценка риска одной облигации (ISIN → <see cref="BrokerRiskLevel"/>).</summary>
public sealed record TInvestBondRiskLevel(string Isin, Bonds.Core.Models.BrokerRiskLevel RiskLevel);

/// <summary>Тариф пользователя T-Invest — только контекст для UI, не влияет на расчёты.</summary>
public sealed record TInvestTariffInfo
{
    /// <summary>Имя тарифа (например, "Инвестор", "Трейдер") — как отдаёт GetInfoResponse.Tariff.</summary>
    public required string Tariff { get; init; }

    /// <summary>Признак премиального статуса (GetInfoResponse.PremStatus) — доп. контекст, не используется в расчётах.</summary>
    public required bool PremStatus { get; init; }
}
