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

    public const string Disclaimer = SwitchAnalysisService.Disclaimer;

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

                if (result.NetBenefitRub <= 0m)
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
                        NetBenefitRub = result.NetBenefitRub,
                    });
                    continue;
                }

                var netProceedsAfterSale = hold.MarketValueRub - result.SellCommissionRub;
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
                });
            }
        }

        bestPairs = bestPairs.OrderByDescending(p => p.NetBenefitRub).ToList();
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
