namespace Bonds.Core.Policy;

/// <summary>
/// Лимиты авто-подтверждения (раздел <c>ExecutionLimits</c> appsettings/env, НЕ из БД). <b>Единицы:</b> рубли и доллары за ЗАЯВКУ / за СУТКИ
/// (московские сутки). <see cref="FreedomAutoUsd"/> = 0 — на Freedom авто-подтверждение выключено.
/// </summary>
public sealed class ExecutionLimitsOptions
{
    public const string SectionName = "ExecutionLimits";

    /// <summary>Максимум суммы ОДНОЙ заявки (строки плана) для <c>AutoApproved</c>, рубли.</summary>
    public decimal AutoPerOrderRub { get; set; } = 25_000m;

    /// <summary>Максимум Σ авто-подтверждённых планов за московские сутки, рубли.</summary>
    public decimal AutoPerDayRub { get; set; } = 50_000m;

    /// <summary>Лимит авто-подтверждения для Freedom, доллары. 0 — выключено.</summary>
    public decimal FreedomAutoUsd { get; set; }
}

/// <summary>
/// Режим исполнения. <see cref="DryRun"/> (дефолт true) копируется в <c>trade_plans.dry_run</c>. <b>В фазе 2 план ВСЕГДА dry-run</b>:
/// исполнителя в коде нет, поэтому значение false принудительно игнорируется (<see cref="EffectiveDryRun"/>).
/// </summary>
public sealed class ExecutionOptions
{
    public const string SectionName = "Execution";

    public bool DryRun { get; set; } = true;

    /// <summary>Фаза 2: исполнения не существует, поэтому dry-run включён безусловно (даже если в конфиге указано false).</summary>
    public bool EffectiveDryRun => true;
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
