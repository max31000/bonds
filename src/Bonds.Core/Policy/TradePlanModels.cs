namespace Bonds.Core.Policy;

/// <summary>Правила, порождающие планы (<c>trade_plans.rule</c>).</summary>
public static class PlanRules
{
    public const string P1Sweep = "P1_SWEEP";
    public const string P2Inflow = "P2_INFLOW";
    public const string P3bLiqToBonds = "P3B_LIQ_TO_BONDS";
    public const string P4Rebalance = "P4_REBALANCE";
    public const string P5Withdraw = "P5_WITHDRAW";
    public const string P7FreedomTopUp = "P7_FREEDOM_TOPUP";
    public const string Manual = "MANUAL";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        P1Sweep, P2Inflow, P3bLiqToBonds, P4Rebalance, P5Withdraw, P7FreedomTopUp, Manual,
    };
}

/// <summary>Статусы плана (<c>trade_plans.status</c>). Исполнение (задача 65): <c>Approved/AutoApproved</c> с <c>dry_run = 0</c> → <c>Submitting</c> → <c>Done</c> | <c>PartiallyDone</c> | <c>Failed</c>.</summary>
public static class PlanStatuses
{
    public const string Draft = "Draft";
    public const string PendingApproval = "PendingApproval";
    public const string AutoApproved = "AutoApproved";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Expired = "Expired";
    public const string Submitting = "Submitting";
    public const string Done = "Done";
    public const string Failed = "Failed";

    /// <summary>Исполнение завершено, часть строк исполнена (остальные отклонены/не прошли проверки/сняты).</summary>
    public const string PartiallyDone = "PartiallyDone";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Draft, PendingApproval, AutoApproved, Approved, Rejected, Expired, Submitting, Done, Failed, PartiallyDone,
    };

    /// <summary>«Открытые» планы: ещё ждут решения или (в боевом режиме) исполнения. По ним ищутся дубликаты и считается резерв денег.</summary>
    public static readonly IReadOnlyList<string> Open = [Draft, PendingApproval, AutoApproved, Approved, Submitting];

    /// <summary>
    /// Статусы, резервирующие деньги под покупки при любом <c>dry_run</c>. Плюс <c>Approved</c> с <c>dry_run = 0</c> (задача 67, #2: живой план, одобренный вечером, ждёт
    /// открытия торгов — его деньги заняты; условие по <c>dry_run</c> — в <c>SumReservedBuysAsync</c>). Approved dry-run деньги НЕ резервирует (он никогда не исполнится).
    /// </summary>
    public static readonly IReadOnlyList<string> ReservingCash = [Draft, PendingApproval, AutoApproved, Submitting];
}

/// <summary>Сторона строки плана.</summary>
public static class PlanSides
{
    public const string Buy = "Buy";
    public const string Sell = "Sell";
}

/// <summary>Кто принял решение по плану (<c>trade_plans.decided_via</c>).</summary>
public static class DecidedVia
{
    public const string Web = "web";
    public const string Telegram = "telegram";
    public const string Auto = "auto";
}

/// <summary>Источник плана (<c>trade_plans.source</c>, задача 68). null — правила или ручной запрос пользователя.</summary>
public static class PlanSources
{
    /// <summary>Предложен ИИ-агентом через MCP-инструмент <c>propose_plan</c>: статус всегда <c>PendingApproval</c>, решает только пользователь.</summary>
    public const string Agent = "agent";
}

/// <summary>
/// Строка плана = одна будущая лимитная заявка (в этой фазе — только данные). <b>Единицы:</b> <see cref="QuantityLots"/> — ЦЕЛЫЕ лоты
/// (штуки = лоты × <see cref="LotSize"/>); <see cref="LimitPrice"/> — валюта цены за 1 штуку (для облигаций — ЧИСТАЯ цена в рублях за штуку);
/// <see cref="LimitPricePoints"/> — только облигации: цена в ПУНКТАХ (процент номинала) по сетке <c>min_price_increment_points</c>, её и
/// будет отправлять исполнитель; <see cref="EstAmount"/>, <see cref="EstFee"/>, <see cref="EstTax"/> — неотрицательные оценки в
/// <see cref="Currency"/> — ВАЛЮТЕ ЦЕНЫ бумаги (сумма сделки с НКД, комиссия, налог продажи); в валюту портфеля их приводят по <see cref="PolicyQuote.FxToPortfolio"/>
/// (итоги и резерв в <see cref="TradePlanSummary"/> уже в валюте портфеля).
/// </summary>
public sealed class TradePlanLine
{
    public ulong PlanId { get; set; }
    public int LineNo { get; set; }
    public string Broker { get; set; } = string.Empty;
    public ulong AccountId { get; set; }
    public string InstrumentKey { get; set; } = string.Empty;
    public string Ticker { get; set; } = string.Empty;

    /// <summary>См. <see cref="PlanSides"/>.</summary>
    public string Side { get; set; } = PlanSides.Buy;

    public long QuantityLots { get; set; }
    public decimal LotSize { get; set; } = 1m;
    public decimal LimitPrice { get; set; }
    public decimal? LimitPricePoints { get; set; }
    public decimal EstAmount { get; set; }
    public decimal EstFee { get; set; }
    public decimal EstTax { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;

    /// <summary>Штуки = лоты × размер лота.</summary>
    public decimal QuantityPieces => QuantityLots * LotSize;
}

/// <summary>План сделок (<c>trade_plans</c>) — данные. Исполняет его только <c>PlanExecutionEngine</c> (задача 65) и только в live-режиме при <see cref="DryRun"/> = false.</summary>
public sealed class TradePlan
{
    public ulong Id { get; set; }
    public ulong UserId { get; set; }

    /// <summary>См. <see cref="PlanRules"/>.</summary>
    public string Rule { get; set; } = PlanRules.Manual;

    public string Trigger { get; set; } = string.Empty;

    /// <summary>См. <see cref="PlanStatuses"/>.</summary>
    public string Status { get; set; } = PlanStatuses.Draft;

    public bool DryRun { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? DecidedVia { get; set; }

    /// <summary>Кто предложил план (задача 68): null — правила или пользователь, <see cref="PlanSources.Agent"/> — ИИ-агент (<c>propose_plan</c>, всегда ждёт решения).</summary>
    public string? Source { get; set; }

    /// <summary>Случайные 8 байт base64url (11 символов): зашиты в callback_data кнопок Telegram, сверяются при нажатии (задача 64). Не отдаётся в API.</summary>
    public string? BotNonce { get; set; }

    /// <summary>Пользователь попросил снять висящие заявки плана (кнопка «Отменить», задача 65): исполнитель снимает их на ближайшем тике.</summary>
    public bool CancelRequested { get; set; }

    /// <summary>JSON <see cref="TradePlanSummary"/> (camelCase).</summary>
    public string? SummaryJson { get; set; }

    /// <summary>Когда владелец отметил «деньги выведены» (<c>/withdrawn</c> или кнопка в вебе): удержание <see cref="TradePlanSummary.CashHold"/> снято. null — действует.</summary>
    public DateTime? CashHoldReleasedAt { get; set; }

    public List<TradePlanLine> Lines { get; set; } = [];
}

/// <summary>Строка, которую правило не смогло включить в план (бумага пропущена), с причиной.</summary>
public sealed record PlanSkip(string Ticker, string Reason);

/// <summary>
/// Итоги плана (<c>summary_json</c>). Все суммы — в <see cref="Currency"/> портфеля плана (rub — рубли, usd — доллары). Это ОЦЕНКА по
/// ценам стакана на момент построения: ни комиссия, ни налог (средняя цена входа, плоские 13%, без сальдирования и ЛДВ) не точны.
/// </summary>
public sealed record TradePlanSummary
{
    public string Portfolio { get; init; } = string.Empty;
    public string Currency { get; init; } = string.Empty;

    /// <summary>Свободные деньги портфеля до плана (за вычетом резерва под открытые планы).</summary>
    public decimal CashBefore { get; init; }

    /// <summary>Свободные деньги после исполнения всех строк плана по оценке (покупки + комиссия − продажи + налог).</summary>
    public decimal CashAfter { get; init; }

    public decimal BuyTotal { get; init; }

    /// <summary>
    /// Сколько денег план резервирует под покупки: Σ (сумма + комиссия) строк Buy в валюте ПОРТФЕЛЯ (<see cref="Currency"/>), строки в другой валюте цены приведены по курсу.
    /// Именно это число суммирует <c>SumReservedBuysAsync</c> (строки хранятся в валюте цены, складывать их в SQL нельзя). Нет поля (старый план) — резерв 0.
    /// </summary>
    public decimal ReservedBuy { get; init; }
    public decimal SellTotal { get; init; }
    public decimal FeeTotal { get; init; }
    public decimal TaxTotal { get; init; }

    /// <summary>P5: запрошенная сумма «к выводу после налога и комиссии».</summary>
    public decimal? RequestedNet { get; init; }

    /// <summary>P5: сколько будет доступно к выводу после налога и комиссии (свободные деньги + продажи − комиссия − налог).</summary>
    public decimal? NetToWithdraw { get; init; }

    /// <summary>P5: сколько не хватило, если продать больше нечего (null — хватило).</summary>
    public decimal? Shortfall { get; init; }

    /// <summary>
    /// Задача 67 (#3): сколько денег план держит под ВЫВОД, валюта портфеля (P5 — запрошенная сумма за вычетом нехватки; выход из корзины — чистая выручка
    /// продаж). Вывод в банк делается вручную, поэтому эти деньги не должен «увести» P1/P2/P4: <c>SumReservedBuysAsync</c> учитывает удержание, пока оно
    /// действует (<see cref="WithdrawHold"/>). 0 — удержания нет.
    /// </summary>
    public decimal CashHold { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];
    public IReadOnlyList<PlanSkip> Skipped { get; init; } = [];

    /// <summary>true — построено вне торговых часов: цены стакана могут быть устаревшими.</summary>
    public bool PricesPossiblyStale { get; init; }

    /// <summary>Заметка о режиме («dry-run: заявки не отправляются»; после approve — «не исполнен: dry-run»).</summary>
    public string? ExecutionNote { get; init; }
}

/// <summary>Результат правила, ещё не сохранённый: черновик плана (<see cref="Plan"/> null — плана нет) + заметки для лога.</summary>
public sealed record PolicyOutcome(TradePlanDraft? Plan, IReadOnlyList<string> Notes)
{
    public static PolicyOutcome None(params string[] notes) => new(null, notes);
}

/// <summary>Черновик плана до сохранения: правило, причина, статус по лимитам, строки и итоги.</summary>
public sealed record TradePlanDraft(
    string Rule,
    string Trigger,
    string Status,
    DateTime ExpiresAtUtc,
    IReadOnlyList<TradePlanLine> Lines,
    TradePlanSummary Summary);
