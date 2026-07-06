namespace Bonds.Core.Analytics;

/// <summary>
/// Задача 23 — честная матрица замен: серверный перебор ВСЕХ пар «держать hold vs переложиться в
/// target» вместо фронтового цикла (топ-3 слабых × топ-2 целей, максимум 6 запросов, см. doc-comment
/// старого <c>useRecommendationsStore.buildReplacementRequests</c>). Кандидаты hold — все сравнимые
/// позиции портфеля; кандидаты target — сравнимые позиции + watchlist-бумаги (сборка holdings —
/// ответственность вызывающего слоя, <c>PortfolioHoldingsBuilder</c>, этот сервис — чистая функция
/// над уже построенным списком кандидатов).
/// <para>
/// <b>Правило сравнимости</b> (то же, что <see cref="PositionComparisonService"/>): floater/indexed/
/// dataIncomplete не участвуют НИ КАК hold, НИ КАК target — доходность для них принципиально
/// несравнима с YTM (spec §6). Такие holdings отфильтровываются ДО перебора, попадание в матрицу
/// не сообщается отдельной причиной — это тот же "вне сравнения" список, что и на экране
/// «Слабые звенья».
/// </para>
/// <para>
/// <b>Горизонт пары</b> = max(min(hold.DaysToHorizon, target.DaysToHorizon), 1) / 365 — то же, что
/// было в удалённой фронтовой <c>horizonYearsFor</c> (ближайший из двух горизонтов, минимум 1 день,
/// чтобы <see cref="SwitchAnalysisService.Compare"/> не упал на нулевом горизонте).
/// </para>
/// <para>
/// <b>Окно дюраций</b> ±1.5 года (см. <see cref="ComparableDurationWindowYears"/>) — пары вне окна
/// НЕ отбрасываются молча, а попадают в <see cref="ReplacementMatrixResult.RejectedPairs"/> с
/// причиной <see cref="RejectedPairReason.DurationMismatch"/> (план явно требует не прятать их).
/// Пары без дюрации у одной из сторон (null) — окно не проверяется, дюрация не блокирует сравнение
/// (та же терпимость к null, что у старого фронтового кода).
/// </para>
/// <para>
/// <b>targetYield ≤ holdYield</b> — пара не включается ВООБЩЕ (ни в bestPairs, ни в rejectedPairs) —
/// таких пар большинство (C(n,2)×2 минус выгодное направление) и они тривиальны (план §A.4).
/// </para>
/// <para>
/// <b>annualizedBenefitFraction</b> = NetBenefitRub / CapitalRub / HorizonYears — доля (не процент),
/// пересчёт чистой выгоды в "годовую доходность самой операции замены" на капитале, который реально
/// перекладывается (netProceedsAfterSale, та же база, что у <see cref="SwitchAnalysisService"/> для
/// спреда) — сравнимая по масштабу с YTM бумаг, чтобы "выгода 100 ₽ за 4 мес" не читалась вслепую.
/// Null, если CapitalRub или HorizonYears равны 0 (защита от деления на 0 — на практике капитал
/// позиции с MarketValueRub=0 уже НЕ должен образовывать пару, см. фильтр выше, это последний рубеж).
/// </para>
/// <para>
/// <b>Предохранитель</b>: типичный портфель ~15 бумаг даёт ~15×16=240 упорядоченных пар (n×(n-1) с
/// учётом watchlist-целей) — считается мгновенно, отдельных лимитов не требуется. Если суммарное
/// число пар (best+rejected) превышает <see cref="MaxPairsSafetyThreshold"/>, в КАЖДУЮ из категорий
/// (best/rejected) возвращается не более <see cref="MaxPairsPerCategory"/> — best сортирован по
/// NetBenefit убыв. (лучшие не теряются), rejected — по |NetBenefit| убыв. (важные причины впереди).
/// </para>
/// <para>
/// <b>Задача 25 — налог продажи hold-позиции:</b> <see cref="MatrixPair.SellTaxEstimateRub"/>
/// (через <see cref="SaleTaxEstimator"/> поверх <see cref="MatrixCandidate.HoldInvestedRub"/>/
/// <see cref="MatrixCandidate.HoldHasUnknownLots"/> — cost basis нужен только у HOLD-стороны,
/// продаётся именно она) и <see cref="MatrixPair.NetBenefitAfterTaxRub"/> = NetBenefitRub −
/// SellTaxEstimateRub (null, если налог не оценён). <b>Ранжирование и фильтр "выгодных" — по
/// NetBenefitAfterTaxRub ?? NetBenefitRub</b> (после налога, если он оценим; иначе — прежнее
/// поведение до налога, не 0 и не отбрасываем пару). Экономика упрощения: налог при перекладке НЕ
/// исчезает бесследно, а платится РАНЬШЕ, чем он был бы уплачен при обычном погашении/продаже в
/// будущем (при котором тоже возник бы НДФЛ с той же прибыли) — строго говоря, это стоимость
/// ДОСРОЧНОЙ реализации прибыли, а не абсолютная потеря капитала. Для MVP-оценки порядка величины
/// налог вычитается ЦЕЛИКОМ (консервативно, как если бы альтернатива держать-до-погашения налога
/// вообще не порождала) — see disclaimer.
/// </para>
/// </summary>
public static class ReplacementMatrixService
{
    /// <summary>Окно сопоставимости модифицированной дюрации hold/target, в годах (plan/17 §A.2, сохранено из фронта).</summary>
    public const decimal ComparableDurationWindowYears = 1.5m;

    /// <summary>Минимальный горизонт пары в днях — защита от нулевого/отрицательного горизонта у бумаги с сегодняшним погашением/офертой.</summary>
    public const int MinHorizonDays = 1;

    /// <summary>Если суммарное число пар превышает этот порог — включается предохранитель (см. doc-comment класса).</summary>
    public const int MaxPairsSafetyThreshold = 2000;

    /// <summary>Максимум пар в каждой категории (best/rejected) при сработавшем предохранителе.</summary>
    public const int MaxPairsPerCategory = 500;

    /// <summary>
    /// Задача 25: расширяет <see cref="SwitchAnalysisService.Disclaimer"/> оговоркой про налог —
    /// тот дисклеймер, унаследованный от плана 17/23, говорит "налог не учтён"; здесь это уже
    /// неверно (см. doc-comment класса про SellTaxEstimateRub/NetBenefitAfterTaxRub), поэтому
    /// матрица использует СВОЙ текст, а не общий с SwitchAnalysisService.
    /// </summary>
    public const string Disclaimer =
        "Анализ замены сравнивает только текущие позиции портфеля (не скринер по всей вселенной бумаг). " +
        "Налог при продаже (НДФЛ 13% на разницу цены покупки/продажи к средней цене входа, не FIFO " +
        "брокера) учтён ОЦЕНОЧНО в ранжировании и выгоде после налога — без сальдирования прибылей/" +
        "убытков по другим позициям, без льготы долгосрочного владения (3 года) и без прогрессии до " +
        "15%; налог при перекладке не исчезает, а платится раньше, чем при удержании до погашения — " +
        "для оценки он вычитается целиком (консервативно). При неполном журнале операций налог не " +
        "оценивается (пара ранжируется по выгоде до налога, не считается нулевым). Выгода спреда " +
        "оценивается линейно (спред × горизонт) на капитале после комиссии продажи; комиссия каждой " +
        "сделки учтена один раз. Перед сделкой проверьте фактические условия у брокера.";

    /// <summary>Один кандидат матрицы — hold ИЛИ target (позиция портфеля либо watchlist-бумага без позиции).</summary>
    public sealed record MatrixCandidate
    {
        public required ulong PositionId { get; init; }
        public required ulong InstrumentId { get; init; }
        public string? Name { get; init; }
        public string? Issuer { get; init; }
        public required decimal MarketValueRub { get; init; }
        public decimal? EffectiveYield { get; init; }
        public decimal? ModifiedDuration { get; init; }
        public required int DaysToHorizon { get; init; }

        /// <summary>true — кандидат из watchlist (без позиции в портфеле), см. plan/23 п.1.</summary>
        public bool IsWatchlist { get; init; }

        /// <summary>
        /// Задача 25: вложено в текущий остаток по средней цене входа (<see cref="PositionCostBasis.InvestedRub"/>,
        /// plan/14) — используется, ТОЛЬКО когда кандидат выступает HOLD-стороной пары (продаётся
        /// именно он). Null у watchlist-кандидатов (нет позиции — нечего продавать, см. doc-comment
        /// <see cref="Infrastructure.Analytics.PortfolioHoldingsBuilder.BuildForInstrumentsAsync"/>
        /// в вызывающем коде) и там, где cost basis не посчитан.
        /// </summary>
        public decimal? HoldInvestedRub { get; init; }

        /// <summary>Задача 25: <see cref="PositionCostBasis.HasUnknownLots"/> — журнал операций не покрывает весь остаток. True по умолчанию (watchlist без cost basis — считаем "неизвестно", не "ноль").</summary>
        public bool HoldHasUnknownLots { get; init; } = true;
    }

    /// <summary>Почему пара не попала в bestPairs (но видима в rejectedPairs).</summary>
    public enum RejectedPairReason
    {
        /// <summary>Чистая выгода ≤ 0 (комиссии перехода не окупаются спредом на горизонте пары).</summary>
        NotProfitable,

        /// <summary>Дюрации hold/target вне окна ±<see cref="ComparableDurationWindowYears"/> лет.</summary>
        DurationMismatch,
    }

    public sealed record MatrixPair
    {
        public required ulong HoldPositionId { get; init; }
        public required ulong HoldInstrumentId { get; init; }
        public string? HoldName { get; init; }

        public required ulong TargetPositionId { get; init; }
        public required ulong TargetInstrumentId { get; init; }
        public string? TargetName { get; init; }
        public required bool IsWatchlistTarget { get; init; }

        public required decimal SpreadFraction { get; init; }
        public required decimal CapitalRub { get; init; }
        public required decimal HorizonYears { get; init; }
        public required decimal GrossGainRub { get; init; }
        public required decimal SellCommissionRub { get; init; }
        public required decimal BuyCommissionRub { get; init; }
        public required decimal NetBenefitRub { get; init; }

        /// <summary>NetBenefitRub / CapitalRub / HorizonYears — доля (не процент), см. doc-comment класса.</summary>
        public decimal? AnnualizedBenefitFraction { get; init; }

        public required decimal CommissionRateUsed { get; init; }
        public required Interfaces.CommissionRateSource CommissionRateSource { get; init; }

        /// <summary>
        /// Задача 25: оценка НДФЛ от продажи HOLD-позиции (<see cref="SaleTaxEstimator"/>, 13% с
        /// прибыли к средней цене входа). Null — cost basis hold-позиции недоступен/журнал неполон
        /// (см. doc-comment класса) — "оценить нельзя", НЕ "налога нет".
        /// </summary>
        public decimal? SellTaxEstimateRub { get; init; }

        /// <summary>Задача 25: NetBenefitRub − SellTaxEstimateRub — выгода после налога. Null, если SellTaxEstimateRub недоступен (см. doc-comment класса про ранжирование NetBenefitAfterTaxRub ?? NetBenefitRub).</summary>
        public decimal? NetBenefitAfterTaxRub { get; init; }
    }

    public sealed record RejectedPair
    {
        public required ulong HoldPositionId { get; init; }
        public required ulong HoldInstrumentId { get; init; }
        public string? HoldName { get; init; }

        public required ulong TargetPositionId { get; init; }
        public required ulong TargetInstrumentId { get; init; }
        public string? TargetName { get; init; }
        public required bool IsWatchlistTarget { get; init; }

        public required RejectedPairReason Reason { get; init; }

        /// <summary>Заполнен для <see cref="RejectedPairReason.NotProfitable"/> — сколько (отрицательная выгода в рублях).</summary>
        public decimal? NetBenefitRub { get; init; }
    }

    public sealed record ReplacementMatrixResult
    {
        public required IReadOnlyList<MatrixPair> BestPairs { get; init; }
        public required IReadOnlyList<RejectedPair> RejectedPairs { get; init; }

        /// <summary>Сколько пар прошло targetYield > holdYield фильтр (best.Count + rejected.Count ДО предохранителя) — для пустого состояния фронта («рассмотрено N пар»).</summary>
        public required int TotalConsideredPairs { get; init; }
        public required string Disclaimer { get; init; }
    }

    /// <summary>
    /// Перебирает все пары (hold × target) среди сравнимых кандидатов и считает полную разбивку
    /// каждой через <see cref="SwitchAnalysisService.Compare"/>. holdCandidates и targetCandidates
    /// могут пересекаться по составу (обычные позиции сравниваются друг с другом в обе стороны) —
    /// пара с HoldPositionId == TargetPositionId (совпадающая позиция сама с собой) пропускается.
    /// </summary>
    public static ReplacementMatrixResult BuildMatrix(
        IReadOnlyList<MatrixCandidate> holdCandidates,
        IReadOnlyList<MatrixCandidate> targetCandidates,
        decimal sellCommissionRate,
        decimal buyCommissionRate,
        Interfaces.CommissionRateSource commissionRateSource)
    {
        var bestPairs = new List<MatrixPair>();
        var rejectedPairs = new List<RejectedPair>();
        var totalConsidered = 0;

        foreach (var hold in holdCandidates)
        {
            foreach (var target in targetCandidates)
            {
                // Watchlist-таргет никогда не совпадает по PositionId с hold (PositionId=0 —
                // синтетическое значение, см. doc-comment PortfolioHoldingsBuilder.BuildForInstrumentsAsync),
                // но на всякий случай сверяем и InstrumentId — бумага не может быть заменой сама себе.
                if (target.PositionId == hold.PositionId && !target.IsWatchlist) continue;
                if (target.InstrumentId == hold.InstrumentId) continue;

                var holdYield = hold.EffectiveYield;
                var targetYield = target.EffectiveYield;

                // Plan/23 §A.4: targetYield <= holdYield пары не включаются вообще — тривиальны, их
                // слишком много (n×(n-1) минус выгодное направление).
                if (holdYield is null || targetYield is null || targetYield <= holdYield) continue;

                totalConsidered++;

                var durationMismatch = hold.ModifiedDuration is not null && target.ModifiedDuration is not null
                    && Math.Abs(target.ModifiedDuration.Value - hold.ModifiedDuration.Value) > ComparableDurationWindowYears;

                if (durationMismatch)
                {
                    rejectedPairs.Add(new RejectedPair
                    {
                        HoldPositionId = hold.PositionId,
                        HoldInstrumentId = hold.InstrumentId,
                        HoldName = hold.Name ?? hold.Issuer,
                        TargetPositionId = target.PositionId,
                        TargetInstrumentId = target.InstrumentId,
                        TargetName = target.Name ?? target.Issuer,
                        IsWatchlistTarget = target.IsWatchlist,
                        Reason = RejectedPairReason.DurationMismatch,
                        NetBenefitRub = null,
                    });
                    continue;
                }

                var horizonYears = HorizonYearsFor(hold.DaysToHorizon, target.DaysToHorizon);

                var holdSwitchCandidate = new SwitchCandidate
                {
                    PositionId = hold.PositionId,
                    MarketValueRub = hold.MarketValueRub,
                    EffectiveYield = holdYield,
                };
                var targetSwitchCandidate = new SwitchCandidate
                {
                    PositionId = target.PositionId,
                    MarketValueRub = target.MarketValueRub,
                    EffectiveYield = targetYield,
                };

                var result = SwitchAnalysisService.Compare(
                    holdSwitchCandidate, targetSwitchCandidate, horizonYears, sellCommissionRate, buyCommissionRate);

                var netProceedsAfterSale = hold.MarketValueRub - result.SellCommissionRub;

                // Задача 25: налог на продажу HOLD-позиции (только hold — именно она продаётся;
                // target нужен, только чтобы купить, продажи там нет). Null — cost basis
                // недоступен/журнал неполон, см. doc-comment класса.
                var sellTaxEstimate = SaleTaxEstimator.Estimate(
                    netProceedsAfterSale, hold.HoldInvestedRub, hold.HoldHasUnknownLots);
                var sellTaxEstimateRub = sellTaxEstimate?.TaxRub;
                var netBenefitAfterTaxRub = sellTaxEstimateRub is decimal tax ? result.NetBenefitRub - tax : (decimal?)null;

                // Задача 25: фильтр/ранжирование "выгодных" — по netBenefitAfterTaxRub, если налог
                // оценим, иначе (журнал неполон) по прежнему до-налоговому netBenefitRub — не
                // отбрасываем пару и не подменяем неизвестный налог нулём (см. doc-comment класса).
                var rankingBenefitRub = netBenefitAfterTaxRub ?? result.NetBenefitRub;

                if (rankingBenefitRub <= 0m)
                {
                    rejectedPairs.Add(new RejectedPair
                    {
                        HoldPositionId = hold.PositionId,
                        HoldInstrumentId = hold.InstrumentId,
                        HoldName = hold.Name ?? hold.Issuer,
                        TargetPositionId = target.PositionId,
                        TargetInstrumentId = target.InstrumentId,
                        TargetName = target.Name ?? target.Issuer,
                        IsWatchlistTarget = target.IsWatchlist,
                        Reason = RejectedPairReason.NotProfitable,
                        NetBenefitRub = rankingBenefitRub,
                    });
                    continue;
                }

                var spreadFraction = targetYield.Value - holdYield.Value;
                var grossGainRub = netProceedsAfterSale * spreadFraction * horizonYears;

                decimal? annualizedBenefitFraction = (netProceedsAfterSale > 0m && horizonYears > 0m)
                    ? result.NetBenefitRub / netProceedsAfterSale / horizonYears
                    : null;

                bestPairs.Add(new MatrixPair
                {
                    HoldPositionId = hold.PositionId,
                    HoldInstrumentId = hold.InstrumentId,
                    HoldName = hold.Name ?? hold.Issuer,
                    TargetPositionId = target.PositionId,
                    TargetInstrumentId = target.InstrumentId,
                    TargetName = target.Name ?? target.Issuer,
                    IsWatchlistTarget = target.IsWatchlist,
                    SpreadFraction = spreadFraction,
                    CapitalRub = netProceedsAfterSale,
                    HorizonYears = horizonYears,
                    GrossGainRub = grossGainRub,
                    SellCommissionRub = result.SellCommissionRub,
                    BuyCommissionRub = result.BuyCommissionRub,
                    NetBenefitRub = result.NetBenefitRub,
                    AnnualizedBenefitFraction = annualizedBenefitFraction,
                    CommissionRateUsed = sellCommissionRate,
                    CommissionRateSource = commissionRateSource,
                    SellTaxEstimateRub = sellTaxEstimateRub,
                    NetBenefitAfterTaxRub = netBenefitAfterTaxRub,
                });
            }
        }

        // Задача 25: ранжирование bestPairs — по netBenefitAfterTaxRub, если оценим, иначе по
        // до-налоговому netBenefitRub (см. doc-comment класса). rejectedPairs (NotProfitable) уже
        // несёт after-tax-или-pretax значение в NetBenefitRub (см. rankingBenefitRub выше).
        bestPairs = bestPairs.OrderByDescending(p => p.NetBenefitAfterTaxRub ?? p.NetBenefitRub).ToList();
        rejectedPairs = rejectedPairs.OrderByDescending(p => Math.Abs(p.NetBenefitRub ?? 0m)).ToList();

        // Plan/23 §A.4: предохранитель — при большом портфеле возвращаем топ-N по каждой категории,
        // не считаем результат недостоверным (TotalConsideredPairs всё равно отражает полное число).
        if (bestPairs.Count + rejectedPairs.Count > MaxPairsSafetyThreshold)
        {
            bestPairs = bestPairs.Take(MaxPairsPerCategory).ToList();
            rejectedPairs = rejectedPairs.Take(MaxPairsPerCategory).ToList();
        }

        return new ReplacementMatrixResult
        {
            BestPairs = bestPairs,
            RejectedPairs = rejectedPairs,
            TotalConsideredPairs = totalConsidered,
            Disclaimer = Disclaimer,
        };
    }

    /// <summary>
    /// Горизонт сравнения замены (лет) = ближайший из двух горизонтов (min daysToHorizon hold/target) —
    /// перенесено без изменений из удалённой фронтовой <c>horizonYearsFor</c> (plan/17 §A.2): критично
    /// для корректности линейного расчёта <see cref="SwitchAnalysisService.Compare"/>.
    /// </summary>
    public static decimal HorizonYearsFor(int holdDaysToHorizon, int targetDaysToHorizon)
    {
        var minDays = Math.Min(holdDaysToHorizon, targetDaysToHorizon);
        return Math.Max(minDays, MinHorizonDays) / 365m;
    }

    /// <summary>Правило сравнимости (plan/17 §A.1, то же, что PositionComparisonService/фронтовый isOutOfComparison): floater/indexed/dataIncomplete несравнимы по доходности.</summary>
    public static bool IsComparable(bool isFloater, bool isIndexed, bool dataIncomplete) =>
        !isFloater && !isIndexed && !dataIncomplete;
}
