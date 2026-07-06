namespace Bonds.Core.Analytics;

/// <summary>
/// Задача 25 — оценка НДФЛ при продаже бумаги с прибылью относительно цены входа (capital gains,
/// см. TODO (1) в doc-comment <see cref="ITaxModel"/>). Чистый сервис, без I/O — не реализует
/// <see cref="ITaxModel"/> напрямую: тот интерфейс — точка расширения для ПОЛНОГО налогового
/// моделирования (сальдирование + ЛДВ + мультисчёт/ИИС, см. его doc-comment), а этот класс —
/// сознательно упрощённая ОЦЕНКА ПОРЯДКА ВЕЛИЧИНЫ для одной сделки продажи вне контекста
/// налогового периода целиком. Когда/если ITaxModel будет реализован, этот сервис может стать его
/// внутренним строительным блоком (или быть вытеснен) — на MVP это отдельный сервис с doc-ссылкой,
/// сам интерфейс не трогается и его нет смысла реализовывать частично.
/// <para>
/// <b>Зафиксированные упрощения (обязательно к прочтению перед использованием результата):</b>
/// <list type="bullet">
/// <item>Метод — average cost (средневзвешенная цена входа, <see cref="PositionCostBasisService"/>),
/// НЕ FIFO, который использует брокер для фактического расчёта НДФЛ. Итоговая сумма налога за ВСЮ
/// историю по бумаге у обоих методов совпадает, но при частичной продаже разбивка "какой лот ушёл"
/// может отличаться от факта — см. doc-comment PositionCostBasisService.</item>
/// <item>БЕЗ сальдирования прибылей и убытков по другим позициям счёта в налоговом периоде (ст. 214.1
/// НК РФ) — считается только эта сделка изолированно, как если бы это была единственная операция
/// года.</item>
/// <item>БЕЗ льготы долгосрочного владения (ЛДВ, ст. 219.1 НК РФ — "трёхлетняя льгота" для бумаг,
/// приобретённых после установленной даты и удерживаемых от 3 лет).</item>
/// <item>БЕЗ переноса убытков прошлых периодов на будущее (ст. 220.1 НК РФ).</item>
/// <item>Ставка ПЛОСКАЯ 13% (<see cref="NdflRate"/>) — БЕЗ прогрессии до 15% с дохода свыше порога
/// (п. 1 ст. 224 НК РФ), которая зависит от совокупного годового дохода налогоплательщика, не
/// известного этому сервису.</item>
/// </list>
/// Итог: результат — прикидка "порядка величины" налога с ОДНОЙ сделки продажи, не точная сумма,
/// которую удержит брокер, и не налоговая консультация.
/// </para>
/// </summary>
public static class SaleTaxEstimator
{
    /// <summary>
    /// Ставка НДФЛ на доход физлица от операций с ценными бумагами — плоская 13% (п. 1 ст. 224 НК РФ),
    /// БЕЗ прогрессии до 15% с дохода свыше годового порога (см. doc-comment класса — упрощение MVP).
    /// </summary>
    public const decimal NdflRate = 0.13m;

    /// <summary>
    /// Считает оценку налога с продажи. <paramref name="netProceedsRub"/> — чистая выручка от
    /// продажи (ПОСЛЕ комиссии брокера, та же величина, что <see cref="IfSoldNowResult.NetProceedsRub"/>
    /// или <c>netProceedsAfterSale</c> в <see cref="SwitchAnalysisService"/>/<see cref="ReplacementMatrixService"/>).
    /// <paramref name="investedRub"/> — вложено в продаваемое количество по средней цене входа
    /// (<see cref="PositionCostBasis.InvestedRub"/>, задача 14). <paramref name="hasUnknownLots"/> —
    /// <see cref="PositionCostBasis.HasUnknownLots"/>: журнал операций не покрывает весь остаток.
    /// <para>
    /// Возвращает <c>null</c> (НЕ ноль), если <paramref name="hasUnknownLots"/> = true или
    /// <paramref name="investedRub"/> = null — семантика "оценить нельзя, журнал операций неполон",
    /// не "налога нет". Убыток (выручка меньше вложенного) даёт налог 0 (не отрицательный — налоговая
    /// база капитального убытка по этой сделке изолированно всегда floor'ится в 0, см. упрощение "без
    /// сальдирования" выше).
    /// </para>
    /// </summary>
    public static SaleTaxEstimate? Estimate(decimal netProceedsRub, decimal? investedRub, bool hasUnknownLots)
    {
        if (hasUnknownLots || investedRub is null)
        {
            return null;
        }

        var taxableGainRub = Math.Max(0m, netProceedsRub - investedRub.Value);
        var taxRub = taxableGainRub * NdflRate;

        return new SaleTaxEstimate
        {
            TaxableGainRub = taxableGainRub,
            TaxRub = taxRub,
            IsEstimate = true,
        };
    }
}

/// <summary>
/// Результат оценки НДФЛ с продажи одной позиции — см. doc-comment <see cref="SaleTaxEstimator"/>
/// для зафиксированных упрощений (average cost, без сальдирования/ЛДВ, плоские 13%).
/// </summary>
public sealed record SaleTaxEstimate
{
    /// <summary>max(0, чистая выручка − вложено) — налогооблагаемая база этой сделки изолированно, не может быть отрицательной.</summary>
    public required decimal TaxableGainRub { get; init; }

    /// <summary>TaxableGainRub × <see cref="SaleTaxEstimator.NdflRate"/> — оценка суммы налога, в рублях.</summary>
    public required decimal TaxRub { get; init; }

    /// <summary>Всегда true — маркер для потребителя, что это оценка (average cost, без сальдирования/ЛДВ, плоская ставка), не точная сумма брокера.</summary>
    public required bool IsEstimate { get; init; }
}
