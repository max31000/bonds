namespace Bonds.Core.Policy;

/// <summary>
/// Лимиты авто-подтверждения (раздел <c>ExecutionLimits</c> appsettings/env, НЕ из БД). <b>Единицы:</b> рубли и доллары за ЗАЯВКУ / за СУТКИ
/// (московские сутки). <see cref="FreedomAutoUsd"/> = 0 — на Freedom авто-подтверждение выключено.
/// </summary>
public sealed class ExecutionLimitsOptions
{
    public const string SectionName = "ExecutionLimits";

    /// <summary>Максимум суммы ОДНОЙ заявки (строки плана) для <c>AutoApproved</c>, рубли: сумма строки в валюте цены × курс, ВКЛЮЧАЯ комиссию (задача 67, #12).</summary>
    public decimal AutoPerOrderRub { get; set; } = 25_000m;

    /// <summary>Максимум Σ авто-подтверждённых планов за московские сутки, рубли (строки × курс, с комиссией — задача 67, #12).</summary>
    public decimal AutoPerDayRub { get; set; } = 50_000m;

    /// <summary>Лимит авто-подтверждения для Freedom, доллары. 0 — выключено.</summary>
    public decimal FreedomAutoUsd { get; set; }
}

/// <summary>
/// Исполнитель планов (раздел <c>Execution</c>, задача 65). <b>Выключен по умолчанию на трёх уровнях</b>: глобально <see cref="Enabled"/> = false
/// (env <c>Execution__Enabled</c>), у пользователя режим <c>observe</c>, без проверенного торгового токена исполнение невозможно; плюс пауза <c>/stop</c>.
/// Живой режим пользователя вычисляет <see cref="Bonds.Core.Execution.ExecutionGate"/> (ВСЕ четыре условия), а не это свойство.
/// <b>Единицы:</b> доли — ДОЛИ (0.005 = 0.5%), время — <see cref="TimeSpan"/>.
/// </summary>
public sealed class ExecutionOptions
{
    public const string SectionName = "Execution";

    /// <summary>Глобальный выключатель (env <c>Execution__Enabled</c>). false — ни одна заявка не отправляется никому; висящие заявки исполнитель только снимает.</summary>
    public bool Enabled { get; set; }

    /// <summary>Историческое поле фазы 2 (<c>Execution:DryRun</c>), на исполнение НЕ влияет — оставлено ради совместимости конфигов.</summary>
    public bool DryRun { get; set; } = true;

    /// <summary>
    /// Глобальная часть режима: при <see cref="Enabled"/> = false — всегда dry-run. Даже при true конкретный пользователь исполняется только в live
    /// (см. <see cref="Bonds.Core.Execution.ExecutionGate"/>), поэтому false здесь ещё не значит «заявки отправляются».
    /// </summary>
    public bool EffectiveDryRun => !Enabled;

    /// <summary>
    /// Допустимое отклонение mid стакана от УТВЕРЖДЁННОГО лимита для фондов/акций, ДОЛЯ (0.002 = 0.2%, значение дизайна §5 «Жёсткие лимиты»). Симметрично (вверх и
    /// вниз); тот же предел ограничивает перевыставление.
    /// </summary>
    public decimal MaxPriceDriftFraction { get; set; } = 0.002m;

    /// <summary>То же для облигаций (сравнение в пунктах), ДОЛЯ (0.005 = 0.5%, дизайн §5). Симметрично.</summary>
    public decimal MaxBondPriceDriftFraction { get; set; } = 0.005m;

    /// <summary>Не исполнена за это время — отмена и перевыставление ближе к противоположной стороне стакана.</summary>
    public TimeSpan RepriceAfter { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Максимум попыток (выставлений) одной строки, включая первую.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>За столько до закрытия сессии висящие заявки снимаются, новые не отправляются.</summary>
    public TimeSpan CancelBeforeClose { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Стакан старше этого — «несвежая котировка», строка не отправляется.</summary>
    public TimeSpan MaxQuoteAge { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Заявка в статусе New (записана, ответ брокера не получен) дольше этого и не найдена у брокера — Failed без повторной отправки.</summary>
    public TimeSpan UnconfirmedOrderTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Период тика исполнителя.</summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Порог сверки комиссии: |факт − модель| больше этого (в валюте строки) — предупреждение в бот.</summary>
    public decimal FeeMismatchThreshold { get; set; } = 0.01m;

    /// <summary>
    /// Сколько тиков подряд откладывать итоговую сверку комиссии, если брокер прислал исполнение с комиссией 0, а модель ждёт ненулевую (комиссия у T-Invest
    /// приходит с задержкой): каждый тик состояние перечитывается; после стольких попыток сверка идёт по тому, что есть.
    /// </summary>
    public int MaxFeeRechecks { get; set; } = 3;

    /// <summary>
    /// То же для Freedom (задача 66): комиссия есть только в выписке движений денег (<c>commission_for_trades</c>) и появляется с задержкой — сверка откладывается,
    /// пока по всем сделкам строки не придут строки комиссии, но не больше стольких тиков (90 × 20 с ≈ 30 мин). Окно исчерпано, а комиссии нет — строка закрывается
    /// с пометкой «комиссия не сверена» (без ложного предупреждения о расхождении); план закрывается после этого.
    /// </summary>
    public int FreedomMaxFeeRechecks { get; set; } = 90;

    /// <summary>
    /// Freedom: максимум попыток (выставлений) строки, включая первую (T-Invest — <see cref="MaxAttempts"/>). Меньше, чем у T-Invest: у Freedom на US/EU есть ФИКСИРОВАННАЯ
    /// комиссия за заявку ($1.2 в тарифах Standard/Investor, <c>FreedomFeeModel</c>) — она берётся с КАЖДОЙ заявки, по которой было исполнение, поэтому каждое
    /// перевыставление частично исполненной строки стоит ещё $1.2 (на сумме ~$100 это ~1.2%). Две попытки — первая по утверждённому лимиту, вторая ближе к
    /// противоположной стороне (в пределах дрейфа).
    /// </summary>
    public int FreedomMaxAttempts { get; set; } = 2;

    /// <summary>
    /// Freedom: ожидание исполнения до перевыставления (T-Invest — <see cref="RepriceAfter"/>). Дольше, чем у T-Invest: по той же причине (фикс за заявку) выгоднее
    /// дольше ждать исполнения по лимиту, чем платить за новую заявку; стакан UCITS-фондов на EU тонкий, исполнение по лимиту приходит минутами.
    /// </summary>
    public TimeSpan FreedomRepriceAfter { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Предел дрейфа для строки: облигации (цена в пунктах) — <see cref="MaxBondPriceDriftFraction"/>, остальное — <see cref="MaxPriceDriftFraction"/>.</summary>
    public decimal DriftFor(bool isBond) => isBond ? MaxBondPriceDriftFraction : MaxPriceDriftFraction;
}

/// <summary>Окно торгов в МОСКОВСКОМ времени (Россия без перевода часов: MSK = UTC+3 круглый год). Будни, [Open, Close).</summary>
public sealed class TradingSessionOptions
{
    public TradingSessionOptions() { }

    public TradingSessionOptions(TimeSpan open, TimeSpan close)
    {
        Open = open;
        Close = close;
    }

    public TimeSpan Open { get; set; }
    public TimeSpan Close { get; set; }
}

/// <summary>
/// Параметры движка правил (раздел <c>Policy</c>). <b>Единицы:</b> доли — ДОЛИ (0.015 = 1.5 п.п.), деньги — рубли/доллары, время — MSK.
/// Часы торгов — ориентиры без учёта праздничных дней биржи (в праздник стакан пуст, и правило пропустит бумагу «нет стакана»).
/// </summary>
public sealed class PolicyOptions
{
    public const string SectionName = "Policy";

    /// <summary>Сколько живёт <c>PendingApproval</c> (потом <c>Expired</c>); также ограничено закрытием сессии рынка.</summary>
    public TimeSpan PendingTtl { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>P3b срабатывает при вес корзины <c>liquidity</c> &gt; этой ДОЛИ (и состоянии Above/HardAbove).</summary>
    public decimal LiquidityOverflowWeight { get; set; } = 0.20m;

    /// <summary>P3b: after-tax доходность облигации должна превышать after-tax ожидаемую доходность фонда ликвидности минимум на эту ДОЛЮ годовых (0.015 = 1.5 п.п.).</summary>
    public decimal LiquidityPremiumFraction { get; set; } = 0.015m;

    /// <summary>День недели, когда запускается P3b (после синка).</summary>
    public DayOfWeek LiquidityCheckDay { get; set; } = DayOfWeek.Monday;

    /// <summary>P7: минимум свободных долларов на Freedom для доливки.</summary>
    public decimal MinFreedomTopUpUsd { get; set; } = 50m;

    /// <summary>P7: тикер для сумм до точки безубыточности (<c>FreedomFeeModel.CheaperRoute</c> = ITS-фонды).</summary>
    public string FreedomSmallTopUpTicker { get; set; } = "SPYM.US";

    /// <summary>P7: тикер для больших сумм (Europe: 0.12% + $1.2).</summary>
    public string FreedomLargeTopUpTicker { get; set; } = "VUAA.EU";

    /// <summary>Сколько штук-лотов одной облигации покупает разбор кандидатов (как у <c>source=recommended</c>: 1 лот на бумагу — диверсификация).</summary>
    public int MaxBondLotsPerCandidate { get; set; } = 1;

    /// <summary>Сколько лучших (по доходности после налога) облигаций вселенной получают стакан и метаданные брокера для подбора (остальные не запрашиваются: ограничение числа обращений к брокеру).</summary>
    public int BondCandidatePool { get; set; } = 25;

    /// <summary>Не больше стольких запросов метаданных брокера за один подбор кандидатов.</summary>
    public int MaxMetaQueriesPerRun { get; set; } = 30;

    /// <summary>
    /// Минимальная пауза между планами одного правила и одной причины в ЛЮБОМ статусе (в том числе отклонённых и истёкших). Пока исполнителя нет (dry-run), без неё
    /// каждый цикл после истечения плана рождал бы такой же новый; с ней неподтверждённый план возвращается не чаще, чем раз в эту паузу.
    /// </summary>
    public TimeSpan RepeatCooldown { get; set; } = TimeSpan.FromHours(6);

    /// <summary>Пополнения старше этого окна не порождают P2 автоматически (иначе первый запуск «распределил» бы всю историю).</summary>
    public TimeSpan DepositLookback { get; set; } = TimeSpan.FromDays(3);

    /// <summary>Тикеры (без «@») фондов управляющей компании брокера: комиссия известна без журнала. Сторонние фонды в авто — только с известной комиссией из журнала.</summary>
    public List<string> OwnFunds { get; set; } = ["TMON", "TSAV", "TBRU", "TOFZ", "TPAY"];

    /// <summary>
    /// Задача 67 (#1): тикеры фондов (без «@», регистр не важен), которым авто-исполнение разрешено, даже если признак «без выплат» неизвестен или фонд
    /// платит (решение владельца в конфиге, НЕ в БД). Класс бумаги (фонд) и корзина «liquidity» проверяются всё равно — см. <see cref="AutoWhitelist"/>.
    /// По умолчанию пусто: авто — только фонды с <c>pays_distributions = false</c>.
    /// </summary>
    public List<string> AutoFundWhitelist { get; set; } = [];

    /// <summary>
    /// Задача 67 (#3): сколько РАБОЧИХ дней (пн–пт по МСК, праздники не учитываются) после исполнения плана вывода деньги остаются удержанными (вывод в банк
    /// делается вручную). Раньше удержание снимает владелец: <c>/withdrawn</c> в боте или кнопка «Деньги выведены» на странице «Планы».
    /// </summary>
    public int WithdrawHoldTtl { get; set; } = 3;

    /// <summary>
    /// Задача 67 (#7): данные брокера старше этого (последний успешный синк) — «устарели»: P3b, P4 и распределение по дефициту (P2) не строятся, P1 не паркует.
    /// </summary>
    public TimeSpan MaxSyncAge { get; set; } = TimeSpan.FromHours(26);

    /// <summary>
    /// Санити-проверка стакана (<see cref="LinePricer"/>): бумага пропускается, если (ask−bid)/mid больше этой ДОЛИ — фонды, акции, ETF (0.02 = 2%). Широкий спред =
    /// пустой/«дырявый» стакан, mid по нему не цена.
    /// </summary>
    public decimal MaxSpreadFraction { get; set; } = 0.02m;

    /// <summary>То же для облигаций (спред считается в пунктах относительно mid): облигации торгуются шире фондов, поэтому порог 3%.</summary>
    public decimal MaxBondSpreadFraction { get; set; } = 0.03m;

    /// <summary>Санити-проверка: |mid − last| / last больше этой ДОЛИ (0.05 = 5%) — бумага пропускается; проверка только при известной цене last.</summary>
    public decimal MaxLastDeviationFraction { get; set; } = 0.05m;

    /// <summary>Мосбиржа: 10:00–18:40 МСК.</summary>
    public TradingSessionOptions Moex { get; set; } = new(new TimeSpan(10, 0, 0), new TimeSpan(18, 40, 0));

    /// <summary>СПБ-режим <c>SPBRU</c>: по умолчанию как Мосбиржа (уточняется конфигом).</summary>
    public TradingSessionOptions SpbRu { get; set; } = new(new TimeSpan(10, 0, 0), new TimeSpan(18, 40, 0));

    /// <summary>Тикеры <c>*.US</c>: пересечение летнего и зимнего окон NYSE по МСК (17:30–23:00).</summary>
    public TradingSessionOptions FreedomUs { get; set; } = new(new TimeSpan(17, 30, 0), new TimeSpan(23, 0, 0));

    /// <summary>Тикеры <c>*.EU</c> (Xetra): пересечение летнего и зимнего окон по МСК (11:00–18:30).</summary>
    public TradingSessionOptions FreedomEu { get; set; } = new(new TimeSpan(11, 0, 0), new TimeSpan(18, 30, 0));
}
