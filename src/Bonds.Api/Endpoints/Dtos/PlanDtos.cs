using Bonds.Core.Policy;

namespace Bonds.Api.Endpoints.Dtos;

/// <summary>
/// Строка плана = будущая лимитная заявка (исполняется только у плана с <c>dryRun = false</c>, задача 65). <b>Единицы:</b> <see cref="QuantityLots"/> — целые лоты,
/// <see cref="QuantityPieces"/> = лоты × <see cref="LotSize"/>; <see cref="LimitPrice"/> — валюта цены за штуку (облигации — ЧИСТАЯ цена, рубли),
/// <see cref="LimitPricePoints"/> — облигации: цена в ПУНКТАХ (процент номинала); <see cref="EstAmount"/>/<see cref="EstFee"/>/<see cref="EstTax"/> — неотрицательные оценки
/// в <see cref="Currency"/>.
/// </summary>
public sealed record PlanLineDto
{
    public required int LineNo { get; init; }
    public required string Broker { get; init; }
    public required ulong AccountId { get; init; }
    public required string InstrumentKey { get; init; }
    public required string Ticker { get; init; }
    public required string Side { get; init; }
    public required long QuantityLots { get; init; }
    public required decimal LotSize { get; init; }
    public required decimal QuantityPieces { get; init; }
    public required decimal LimitPrice { get; init; }
    public decimal? LimitPricePoints { get; init; }
    public required decimal EstAmount { get; init; }
    public required decimal EstFee { get; init; }
    public required decimal EstTax { get; init; }
    public required string Currency { get; init; }
    public required string Reason { get; init; }

    /// <summary>Исполнение строки (задача 65): <c>Pending</c> (заявок нет) или статус последней заявки (<c>TradeOrderStatuses</c>), полностью исполнена — <c>Filled</c>.</summary>
    public string ExecutionStatus { get; init; } = "Pending";

    /// <summary>Исполнено лотов по всем попыткам.</summary>
    public long FilledLots { get; init; }

    /// <summary>Заявки исполнителя по строке (попытки по порядку). Пусто — не исполнялась.</summary>
    public IReadOnlyList<PlanOrderDto> Orders { get; init; } = [];
}

/// <summary>
/// Заявка исполнителя (одна попытка). <b>Единицы:</b> <see cref="LimitPrice"/> — как отправлена брокеру: <see cref="PriceUnit"/> = <c>points</c> (облигации, процент номинала) или
/// <c>currency</c> (валюта за штуку); <see cref="AvgFillPrice"/>/<see cref="ExecutedCommission"/> — в <see cref="Currency"/>. Внутренние id брокера не отдаются.
/// </summary>
public sealed record PlanOrderDto
{
    public required int Attempt { get; init; }
    public required string Status { get; init; }
    public required decimal LimitPrice { get; init; }
    public required string PriceUnit { get; init; }
    public required long LotsRequested { get; init; }
    public required long LotsFilled { get; init; }
    public decimal? AvgFillPrice { get; init; }
    public decimal? ExecutedCommission { get; init; }
    public required string Currency { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime UpdatedAt { get; init; }

    /// <summary>Заявка активна у нас, а снять её автоматически нельзя (ушло уведомление «снимите её в терминале брокера») — нужно действие пользователя.</summary>
    public bool NeedsAttention { get; init; }
}

/// <summary>План сделок. <see cref="Id"/> = 0 у предпросмотра (не сохранён). Суммы <see cref="Summary"/> — в валюте портфеля плана.</summary>
public sealed record PlanDto
{
    public required ulong Id { get; init; }
    public required string Rule { get; init; }
    public required string Trigger { get; init; }
    public required string Status { get; init; }
    public required bool DryRun { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime ExpiresAt { get; init; }
    public DateTime? DecidedAt { get; init; }
    public string? DecidedVia { get; init; }
    public TradePlanSummary? Summary { get; init; }
    public required IReadOnlyList<PlanLineDto> Lines { get; init; }

    /// <summary>Хоть одна заявка плана требует действия пользователя (<see cref="PlanOrderDto.NeedsAttention"/>). План при этом остаётся <c>Submitting</c>.</summary>
    public bool NeedsAttention { get; init; }
}

public sealed record PlansResponseDto
{
    public required IReadOnlyList<PlanDto> Items { get; init; }

    /// <summary>Режим наблюдения пользователя: true — сделки не отправляются (ворота исполнения закрыты, задача 65).</summary>
    public required bool DryRun { get; init; }

    /// <summary>Сколько заявок пользователя активно у брокера (<c>New</c>/<c>Submitted</c>/<c>PartiallyFilled</c>) — НЕЗАВИСИМО от ворот и режима (баннер «Есть активные заявки у брокера»).</summary>
    public int ActiveOrders { get; init; }

    public required string Disclaimer { get; init; }
}

/// <summary>Ответ на создание плана / предпросмотр: <see cref="Plan"/> = null, если правило плана не дало; причины — в <see cref="Notes"/>.</summary>
public sealed record PlanResultDto
{
    public PlanDto? Plan { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
    public required bool DryRun { get; init; }
    public required string Disclaimer { get; init; }
}

/// <summary>POST /api/plans/inflow: <c>amount</c> — сумма в валюте портфеля, <c>portfolio</c> — rub (по умолчанию) или usd.</summary>
public sealed record PlanInflowRequest
{
    public decimal? Amount { get; init; }
    public string? Portfolio { get; init; }
}

/// <summary>POST /api/plans/withdraw: ровно одно из <c>amount</c> (к выводу после налога и комиссии, валюта портфеля) и <c>fraction</c> (ДОЛЯ портфеля, 0.1 = 10%).</summary>
public sealed record PlanWithdrawRequest
{
    public decimal? Amount { get; init; }
    public decimal? Fraction { get; init; }
    public string? Portfolio { get; init; }
    public IReadOnlyList<string>? IncludeBaskets { get; init; }
}

public sealed record PlanExitBasketRequest
{
    public string? Code { get; init; }
}

public sealed record PlanPreviewParams
{
    public decimal? Amount { get; init; }
    public decimal? Fraction { get; init; }
    public string? Portfolio { get; init; }
    public IReadOnlyList<string>? IncludeBaskets { get; init; }
    public string? Code { get; init; }
}

/// <summary>POST /api/plans/preview: построить план без сохранения. <c>rule</c> — значение <see cref="PlanRules"/> (для выхода из корзины — <c>MANUAL</c> + <c>params.code</c>).</summary>
public sealed record PlanPreviewRequest
{
    public string? Rule { get; init; }
    public PlanPreviewParams? Params { get; init; }
}
