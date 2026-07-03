using Bonds.Core.Models;

namespace Bonds.Core.Analytics;

/// <summary>
/// Цена входа и P&amp;L по одной позиции (plan/14 §A, spec §9 "Таблица позиций") — считает,
/// сколько реально вложено в текущий остаток бумаги и сколько на ней заработано, отдельно от
/// YTM/текущей доходности (те считаются от текущей рыночной цены и не отвечают на вопрос
/// "от моей цены покупки" — см. tooltip у заголовка "Доходность" на фронте, plan/14 §C.3).
/// <para>
/// <b>Метод — average cost (средневзвешенная цена), а не FIFO.</b> Брокерская отчётность
/// Т-Инвестиций для целей НДФЛ использует FIFO (обязательное требование налогового учёта), но
/// для дисплейной метрики "сколько я вложил и сколько заработал" средневзвешенная цена проще
/// для понимания и не требует восстанавливать точный порядок списания лотов. Оба метода дают
/// одинаковый общий P&amp;L при полной истории операций — расходятся только при частичных
/// продажах в разбивке "какой именно лот продан", что для этого продукта не нужно (нет
/// налогового модуля, см. plan/00 "Вне скоупа"). Если это когда-нибудь понадобится для сверки
/// с 2-НДФЛ — потребуется отдельный FIFO-сервис, этот его не заменяет.
/// </para>
/// <para>
/// <b>Знаки:</b> <see cref="Operation.AmountRub"/> уже приходит от брокера со знаком потока
/// (покупка — минус, продажа/купон — плюс) — см. длинный doc-comment в
/// <see cref="PortfolioXirrService.SignedAmount"/> о том, почему это поле не перезаписывается
/// по <see cref="OperationType"/>. Этот сервис использует его так же: без Math.Abs/перезаписи
/// для купонов, и с Math.Abs только там, где нужна именно величина вложенного при покупке.
/// </para>
/// <para>
/// <b>Купоны:</b> суммируются как сумма всех операций <see cref="OperationType.Coupon"/>.
/// <c>TInvestOperationMapper</c> (Bonds.Infrastructure) на момент написания не выделяет налог с
/// купона в отдельный доменный тип — все налоговые операции (включая "TaxCorrectionCoupon")
/// мапятся в общий <see cref="OperationType.Tax"/>, который в этом сервисе не участвует ни в
/// купонах, ни в cost basis. Итог: "получено купонов" здесь — это сумма собственно купонных
/// выплат (брокер обычно платит уже за вычетом НДФЛ, поэтому фактически это часто и есть чистая
/// сумма; расхождение возможно только если налог с купона удержан отдельной операцией — она не
/// вычитается, т.к. неотличима от прочих налоговых корректировок).
/// </para>
/// </summary>
public static class PositionCostBasisService
{
    /// <summary>
    /// Считает cost basis и P&amp;L по одному инструменту.
    /// </summary>
    /// <param name="operations">Журнал операций ПО ОДНОМУ инструменту (любой порядок — сортируется внутри).</param>
    /// <param name="currentQuantity">Текущее количество бумаг в позиции (источник истины — Position.Quantity, не журнал).</param>
    /// <param name="currentMarketValueRub">Текущая рыночная стоимость остатка (Quantity × грязная цена).</param>
    public static PositionCostBasis Calculate(
        IEnumerable<Operation> operations,
        decimal currentQuantity,
        decimal currentMarketValueRub)
    {
        var ordered = operations
            .Where(op => op.Type is OperationType.Buy or OperationType.Sell)
            .OrderBy(op => op.Date)
            .ThenBy(op => op.Id)
            .ToList();

        decimal runningQty = 0m;
        decimal runningCost = 0m; // суммарная стоимость текущего остатка лотов (по средней цене на момент каждой сделки)
        var hasUnknownLots = false;

        foreach (var op in ordered)
        {
            var qty = op.Quantity ?? 0m;
            if (qty <= 0m) continue; // без количества операция не может изменить лот — пропускаем, не падаем

            if (op.Type == OperationType.Buy)
            {
                runningQty += qty;
                runningCost += Math.Abs(op.AmountRub); // AmountRub у покупки отрицательный (отток) — берём величину вложенного
            }
            else // Sell
            {
                if (qty > runningQty)
                {
                    // Продано больше, чем куплено по журналу — история операций неполна
                    // (бумага куплена до начала синка). Клампим, чтобы не уйти в отрицательный остаток.
                    hasUnknownLots = true;
                    qty = runningQty;
                }

                if (runningQty > 0m)
                {
                    var avgCost = runningCost / runningQty;
                    runningCost -= avgCost * qty;
                    runningQty -= qty;
                }
            }
        }

        // Итоговое количество по журналу не сходится с фактическим остатком позиции —
        // журнал операций неполон (частично или полностью куплено до начала истории синка).
        if (Math.Abs(runningQty - currentQuantity) > 1e-6m)
        {
            hasUnknownLots = true;
        }

        var couponsReceivedRub = operations
            .Where(op => op.Type == OperationType.Coupon)
            .Sum(op => op.AmountRub);

        // Нет ни одного известного лота под текущим остатком — среднюю цену посчитать не из чего.
        if (runningQty <= 0m || currentQuantity <= 0m)
        {
            return new PositionCostBasis
            {
                AverageCostRub = null,
                InvestedRub = null,
                UnrealizedPnlRub = null,
                UnrealizedPnlPercent = null,
                CouponsReceivedRub = couponsReceivedRub,
                TotalReturnRub = null,
                TotalReturnPercent = null,
                HasUnknownLots = hasUnknownLots,
            };
        }

        var averageCostRub = runningCost / runningQty;
        var investedRub = averageCostRub * currentQuantity;
        var unrealizedPnlRub = currentMarketValueRub - investedRub;
        var unrealizedPnlPercent = investedRub != 0m ? unrealizedPnlRub / investedRub : (decimal?)null;

        var totalReturnRub = unrealizedPnlRub + couponsReceivedRub;
        var totalReturnPercent = investedRub != 0m ? totalReturnRub / investedRub : (decimal?)null;

        return new PositionCostBasis
        {
            AverageCostRub = averageCostRub,
            InvestedRub = investedRub,
            UnrealizedPnlRub = unrealizedPnlRub,
            UnrealizedPnlPercent = unrealizedPnlPercent,
            CouponsReceivedRub = couponsReceivedRub,
            TotalReturnRub = totalReturnRub,
            TotalReturnPercent = totalReturnPercent,
            HasUnknownLots = hasUnknownLots,
        };
    }
}

/// <summary>
/// Cost basis и P&amp;L одной позиции "от цены входа" — дополняет YTM/текущую доходность
/// (которые считаются от ТЕКУЩЕЙ рыночной цены, см. doc-comment на <see cref="PositionCostBasisService"/>).
/// Поля nullable там, где посчитать нечего (пустой журнал/нет остатка) — потребитель (API/UI)
/// должен показать прочерк, а не 0.
/// </summary>
public sealed record PositionCostBasis
{
    /// <summary>Средняя цена входа за бумагу, метод average cost (см. doc-comment сервиса).</summary>
    public decimal? AverageCostRub { get; init; }

    /// <summary>Вложено в текущий остаток = AverageCostRub × текущее количество.</summary>
    public decimal? InvestedRub { get; init; }

    /// <summary>Текущая рыночная стоимость минус вложенное.</summary>
    public decimal? UnrealizedPnlRub { get; init; }

    /// <summary>UnrealizedPnlRub / InvestedRub — доля (0.12 = 12%), НЕ проценты (форматтер фронта делает *100).</summary>
    public decimal? UnrealizedPnlPercent { get; init; }

    /// <summary>Сумма всех купонных операций по бумаге (см. doc-comment сервиса). Не null — 0, если купонов не было.</summary>
    public required decimal CouponsReceivedRub { get; init; }

    /// <summary>UnrealizedPnlRub + CouponsReceivedRub.</summary>
    public decimal? TotalReturnRub { get; init; }

    /// <summary>TotalReturnRub / InvestedRub — доля.</summary>
    public decimal? TotalReturnPercent { get; init; }

    /// <summary>
    /// True, если журнал операций не покрывает весь текущий остаток (бумага куплена/продана до
    /// начала истории синка, либо продано больше, чем куплено по журналу). Метрики выше всё равно
    /// возвращаются (посчитаны по известной части журнала), но потребитель должен пометить их как
    /// приблизительные.
    /// </summary>
    public required bool HasUnknownLots { get; init; }
}
