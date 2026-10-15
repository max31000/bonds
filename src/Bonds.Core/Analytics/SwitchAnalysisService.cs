using System.Globalization;

namespace Bonds.Core.Analytics;

/// <summary>
/// Анализ замены (spec §3/§9, математика v2 — задача 48): сравнение «удерживать позицию A vs продать A и
/// переложиться в B», ТОЛЬКО между конкретными бумагами (позиция портфеля либо бумага watchlist/банка,
/// выбранная пользователем — не скринер по всей вселенной). Чистый сервис, без I/O.
/// <para>
/// <b>Модель v2 (вместо линейной «спред × горизонт» из v1):</b> обе ноги КОМПАУНДЯТСЯ на одном и том же
/// эффективном горизонте и сравниваются по будущей стоимости:
/// <code>
///   y_at         = y × (1 − 13%)                        // после НДФЛ на купоны/дисконт (константа CouponTaxRate)
///   FV_hold      = MV_hold × (1 + y_hold_at)^h_eff
///   proceeds     = MV_hold − комиссия_продажи − слиппедж_продажи − НДФЛ_продажи
///   invested     = proceeds − комиссия_покупки − слиппедж_покупки
///   FV_switch    = invested × (1 + y_target_at)^h_eff
///   NetBenefit   = FV_switch − FV_hold
/// </code>
/// h_eff = min(горизонт пользователя, срок до оферты/погашения ЦЕЛИ), но не меньше 0.25 года — иначе
/// цель «давала бы» 2 года преимущества по доходности, хотя через 0.8 года её погасят/оферта и деньги
/// придётся реинвестировать по рыночной ставке.
/// </para>
/// <para>
/// <b>Единицы:</b> доходности, ставки, спреды — ДОЛИ (0.10 = 10%); деньги — рубли; горизонт, дюрации — годы.
/// </para>
/// <para>
/// <b>НКД (грязная цена).</b> Обе ноги учтены по ГРЯЗНОЙ цене (чистая + НКД): <c>MarketValueRub</c> hold —
/// грязная по правилу MarketValue гайда; покупка цели идёт по грязной цене (чистая + НКД), YTM цели
/// считается движком относительно грязной цены, поэтому вся инвестированная сумма (включая уплаченный НКД)
/// растёт по YTM цели, а уплаченный НКД возвращается первым купоном — отдельно НКД в формулу не входит и
/// не должен вычитаться второй раз.
/// </para>
/// <para>
/// <b>Упрощения (явно):</b> НДФЛ 13% применяется ко ВСЕЙ доходности — и купонной, и дисконтной (сальдирование
/// убытков и ЛДВ вне скоупа, BACKLOG P-4); налог на накопленный доход hold-позиции при её удержании
/// (отложенный до погашения) не моделируется; после оферты/погашения цели деньги считаются реинвестированными
/// под ту же доходность (поэтому сравнение идёт ровно на h_eff); срок до погашения/оферты hold-ноги не
/// ограничивает горизонт.
/// </para>
/// </summary>
public static class SwitchAnalysisService
{
    public const string Disclaimer =
        "Анализ замены сравнивает конкретные бумаги (позиция портфеля либо выбранная бумага), не скринер по " +
        "всей вселенной. Обе ноги считаются со сложным начислением на одном горизонте, по доходности после " +
        "НДФЛ 13% (упрощение: налог применён ко всей доходности — купонной и дисконтной; сальдирование " +
        "убытков, ЛДВ и прогрессивная ставка не учтены). Налог с продажи, комиссии и слиппедж (половина спреда " +
        "стакана) входят в расчёт; если журнал операций неполон или стакан неизвестен — соответствующая " +
        "статья не учитывается и это помечено. Горизонт сокращается до оферты/погашения бумаги-цели. " +
        "Это оценка, не финансовая рекомендация. Перед сделкой проверьте фактические условия у брокера.";

    /// <summary>Дефолтная ставка комиссии за сделку (0.3% — типичный тариф брокера на MVP; настраивается вызывающим слоем).</summary>
    public const decimal DefaultCommissionRate = 0.003m;

    /// <summary>
    /// Ставка НДФЛ на купоны/дисконт для доходностей обеих ног — ДОЛЯ (0.13). Упрощение: применяется ко всей
    /// доходности — купонной и дисконтной; сальдирование и ЛДВ вне скоупа, BACKLOG P-4. Совпадает со ставкой
    /// налога продажи <see cref="SaleTaxEstimator.NdflRate"/> (один источник).
    /// </summary>
    public const decimal CouponTaxRate = AfterTaxYield.TaxRate;

    /// <summary>Нижняя граница эффективного горизонта при усечении сроком цели, годы (не превращать анализ в «ноль дней»).</summary>
    public const decimal MinEffectiveHorizonYears = 0.25m;

    /// <summary>Порог |Маколей цели − Маколей hold|, годы: больше — предупреждение «процентный риск изменится».</summary>
    public const decimal DurationMismatchThresholdYears = 1.5m;

    /// <summary>Максимальный срок окупаемости, годы: дольше — <see cref="SwitchAnalysisResult.BreakEvenYears"/> = null.</summary>
    public const decimal MaxBreakEvenYears = 30m;

    private const decimal DaysPerYear = 365m;

    /// <summary>
    /// Сравнивает удержание <paramref name="holdPosition"/> с переходом в <paramref name="targetPosition"/>.
    /// <paramref name="horizonYears"/> — желаемый горизонт пользователя (настройка), реально используется
    /// эффективный (см. doc-comment класса). <paramref name="asOf"/> — «сегодня» (МСК), от неё считается срок
    /// до оферты/погашения цели (<see cref="SwitchCandidate.TargetHorizonDate"/>); null — усечение горизонта
    /// не применяется. Ставки комиссий — доля от суммы сделки (вызывающий слой берёт из настроек тарифа).
    /// </summary>
    public static SwitchAnalysisResult Compare(
        SwitchCandidate holdPosition,
        SwitchCandidate targetPosition,
        decimal horizonYears,
        decimal sellCommissionRate = DefaultCommissionRate,
        decimal buyCommissionRate = DefaultCommissionRate,
        DateOnly? asOf = null)
    {
        if (horizonYears <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(horizonYears), "Horizon must be positive.");
        }

        var hasUsableYields = holdPosition.EffectiveYield.HasValue && targetPosition.EffectiveYield.HasValue;
        var holdYield = holdPosition.EffectiveYield ?? 0m;
        var targetYield = targetPosition.EffectiveYield ?? 0m;
        var holdYieldAfterTax = AfterTax(holdYield);
        var targetYieldAfterTax = AfterTax(targetYield);

        // ── Эффективный горизонт ────────────────────────────────────────────────────────────────
        var effectiveHorizon = horizonYears;
        var limitedByTarget = false;
        if (asOf is { } today && targetPosition.TargetHorizonDate is { } targetDate)
        {
            var yearsToTarget = (targetDate.DayNumber - today.DayNumber) / DaysPerYear;
            if (yearsToTarget < horizonYears)
            {
                effectiveHorizon = Math.Min(horizonYears, Math.Max(yearsToTarget, MinEffectiveHorizonYears));
                limitedByTarget = effectiveHorizon < horizonYears;
            }
        }

        // ── Издержки ног ────────────────────────────────────────────────────────────────────────
        var marketValue = holdPosition.MarketValueRub;
        var sellCommissionRub = marketValue * sellCommissionRate;
        var holdHalfSpread = NormalizeHalfSpread(holdPosition.HalfSpreadFraction);
        var targetHalfSpread = NormalizeHalfSpread(targetPosition.HalfSpreadFraction);
        var slippageUnknown = holdHalfSpread is null || targetHalfSpread is null;
        var sellSlippageRub = marketValue * (holdHalfSpread ?? 0m);

        // Налог продажи — существующий SaleTaxEstimator на чистую выручку ПОСЛЕ комиссии и слиппеджа
        // (слиппедж — реальная недополученная цена, налоговая база уменьшается). null = журнал неполон.
        var netProceedsBeforeTax = marketValue - sellCommissionRub - sellSlippageRub;
        var saleTax = SaleTaxEstimator.Estimate(netProceedsBeforeTax, holdPosition.InvestedRub, holdPosition.HasUnknownLots);
        var saleTaxKnown = saleTax is not null;
        var saleTaxRub = saleTax?.TaxRub ?? 0m;

        var holdFutureValue = marketValue * Compound(holdYieldAfterTax, effectiveHorizon);

        var primary = Switch(saleTaxRub);
        // Сценарий «без налога продажи» — старое поле NetBenefitRub (до налога продажи, но уже после НДФЛ на купоны,
        // комиссий и слиппеджа); с налогом — NetBenefitAfterTaxRub (null, если налог оценить нельзя).
        var withoutSaleTax = saleTaxRub == 0m ? primary : Switch(0m);

        var netBenefitRub = withoutSaleTax.FutureValue - holdFutureValue;
        var netBenefitAfterTaxRub = primary.FutureValue - holdFutureValue;
        var totalSwitchCostRub = sellCommissionRub + primary.BuyCommissionRub;

        // Чистая выгода, годовая ДОЛЯ: (FV_switch/FV_hold)^(1/h) − 1 (с налогом продажи, если известен).
        decimal? annualized = null;
        if (holdFutureValue > 0m && primary.FutureValue > 0m)
        {
            annualized = (decimal)(Math.Pow((double)(primary.FutureValue / holdFutureValue), 1.0 / (double)effectiveHorizon) - 1.0);
        }

        // Валовая выгода: чистая разница доходностей после НДФЛ на купоны, компаундированная на ПОЛНОЙ стоимости
        // hold ДО комиссий, слиппеджа и налога продажи — чтобы строка разбивки «валовая − издержки = чистая» читалась.
        var grossGainRub = marketValue * (Compound(targetYieldAfterTax, effectiveHorizon) - Compound(holdYieldAfterTax, effectiveHorizon));

        var durationMismatch = false;
        string? durationWarning = null;
        if (holdPosition.MacaulayDurationYears is { } holdDuration && targetPosition.MacaulayDurationYears is { } targetDuration
            && Math.Abs(targetDuration - holdDuration) > DurationMismatchThresholdYears)
        {
            durationMismatch = true;
            durationWarning = string.Create(CultureInfo.InvariantCulture,
                $"Процентный риск изменится: дюрация {holdDuration:0.0} → {targetDuration:0.0} лет");
        }

        return new SwitchAnalysisResult
        {
            HoldPositionId = holdPosition.PositionId,
            TargetPositionId = targetPosition.PositionId,
            HorizonYears = horizonYears,
            EffectiveHorizonYears = effectiveHorizon,
            HorizonLimitedByTarget = limitedByTarget,
            TargetHorizonDate = limitedByTarget ? targetPosition.TargetHorizonDate : null,
            TargetHorizonIsOffer = limitedByTarget && targetPosition.TargetHorizonIsOffer,
            SellCommissionRub = sellCommissionRub,
            BuyCommissionRub = primary.BuyCommissionRub,
            TotalSwitchCostRub = totalSwitchCostRub,
            SellSlippageRub = sellSlippageRub,
            BuySlippageRub = primary.BuySlippageRub,
            SlippageUnknown = slippageUnknown,
            SellTaxEstimateRub = saleTax?.TaxRub,
            HoldYieldAfterTaxFraction = holdYieldAfterTax,
            TargetYieldAfterTaxFraction = targetYieldAfterTax,
            SpreadFraction = hasUsableYields ? targetYield - holdYield : null,
            CapitalRub = primary.InvestedRub,
            ProceedsRub = primary.ProceedsRub,
            HoldFutureValueRub = holdFutureValue,
            SwitchFutureValueRub = primary.FutureValue,
            GrossGainRub = hasUsableYields ? grossGainRub : null,
            NetBenefitRub = netBenefitRub,
            NetBenefitAfterTaxRub = saleTaxKnown ? netBenefitAfterTaxRub : null,
            AnnualizedBenefitFraction = hasUsableYields ? annualized : null,
            IsSwitchFavorable = hasUsableYields && netBenefitAfterTaxRub > 0m,
            BreakEvenYears = BreakEven(marketValue, primary.InvestedRub, holdYieldAfterTax, targetYieldAfterTax),
            YieldDataIncomplete = !hasUsableYields,
            HoldMacaulayDurationYears = holdPosition.MacaulayDurationYears,
            TargetMacaulayDurationYears = targetPosition.MacaulayDurationYears,
            DurationMismatch = durationMismatch,
            DurationMismatchWarning = durationWarning,
            Disclaimer = Disclaimer,
        };

        // Нога «переложиться»: выручка → покупка (комиссия + слиппедж от суммы покупки) → рост на h_eff.
        SwitchLeg Switch(decimal saleTaxToDeduct)
        {
            var proceeds = marketValue - sellCommissionRub - sellSlippageRub - saleTaxToDeduct;
            var buyCommission = proceeds * buyCommissionRate;
            var buySlippage = proceeds * (targetHalfSpread ?? 0m);
            var invested = proceeds - buyCommission - buySlippage;
            return new SwitchLeg(proceeds, buyCommission, buySlippage, invested,
                invested * Compound(targetYieldAfterTax, effectiveHorizon));
        }
    }

    /// <summary>Доходность после НДФЛ: положительная — ×(1 − <see cref="CouponTaxRate"/>); отрицательная налога не создаёт.</summary>
    public static decimal AfterTax(decimal yieldFraction) => AfterTaxYield.Apply(yieldFraction);

    /// <summary>
    /// (1 + y)^years. Возведение в дробную степень — через double (в decimal нет Pow); погрешность ~1e-12 относительно,
    /// для рублёвых сумм несущественна. База ограничена снизу нулём (защита от NaN при абсурдной доходности ≤ −100%).
    /// </summary>
    private static decimal Compound(decimal yieldFraction, decimal years) =>
        (decimal)Math.Pow((double)Math.Max(1m + yieldFraction, 0m), (double)years);

    /// <summary>Отрицательный полуспред (перевёрнутый стакан) — данные неконсистентны, берём 0, а не «выгоду» от слиппеджа.</summary>
    private static decimal? NormalizeHalfSpread(decimal? halfSpread) => halfSpread is { } h ? Math.Max(h, 0m) : null;

    /// <summary>
    /// Минимальный горизонт h, при котором FV_switch ≥ FV_hold: invested × (1+b)^h ≥ MV × (1+a)^h ⇒
    /// h ≥ ln(MV/invested) / ln((1+b)/(1+a)), где a, b — доходности после НДФЛ hold/цели. Аналитически.
    /// Если издержек нет (invested ≥ MV) и b ≥ a — 0. Null: цель не обгоняет hold (b ≤ a), либо окупаемость
    /// дальше <see cref="MaxBreakEvenYears"/>. Не ограничен сроком цели: после её оферты/погашения предполагается
    /// реинвестиция под ту же доходность.
    /// </summary>
    private static decimal? BreakEven(decimal marketValue, decimal invested, decimal holdYieldAfterTax, decimal targetYieldAfterTax)
    {
        if (marketValue <= 0m || invested <= 0m) return null;
        if (invested >= marketValue) return targetYieldAfterTax >= holdYieldAfterTax ? 0m : null;
        if (targetYieldAfterTax <= holdYieldAfterTax) return null;

        var years = Math.Log((double)(marketValue / invested)) /
                    Math.Log((double)((1m + targetYieldAfterTax) / (1m + holdYieldAfterTax)));
        if (double.IsNaN(years) || double.IsInfinity(years) || years > (double)MaxBreakEvenYears) return null;
        return (decimal)years;
    }

    private readonly record struct SwitchLeg(
        decimal ProceedsRub, decimal BuyCommissionRub, decimal BuySlippageRub, decimal InvestedRub, decimal FutureValue);
}

/// <summary>Один из участников сравнения «удерживать vs переложиться» (spec §3/§9).</summary>
public sealed record SwitchCandidate
{
    public required ulong PositionId { get; init; }

    /// <summary>Стоимость, ГРЯЗНАЯ (цена + НКД), рубли. Для hold — вся позиция; для цели в расчёте не используется (капитал идёт от hold).</summary>
    public required decimal MarketValueRub { get; init; }

    /// <summary>YTM либо CurrentYield (для флоатера), ДОЛЯ — см. <see cref="PositionComparisonService"/>. Null — доходность не определена.</summary>
    public decimal? EffectiveYield { get; init; }

    /// <summary>Дюрация Маколея, годы (единая мера «срока» репо) — для предупреждения о смене процентного риска. Null — неизвестна.</summary>
    public decimal? MacaulayDurationYears { get; init; }

    /// <summary>Половина спреда стакана, ДОЛЯ ((offer−bid)/(offer+bid), <c>LiquidityScoreCalculator.HalfSpreadFraction</c>) —
    /// оценка слиппеджа одной стороны сделки. Null — стакан не виден (слиппедж не учтён, результат помечается).</summary>
    public decimal? HalfSpreadFraction { get; init; }

    /// <summary>Только для цели: дата ближайшей неисполненной оферты (иначе погашения) — ограничивает горизонт сравнения.</summary>
    public DateOnly? TargetHorizonDate { get; init; }

    /// <summary>Только для цели: <see cref="TargetHorizonDate"/> — оферта (true) или погашение (false).</summary>
    public bool TargetHorizonIsOffer { get; init; }

    /// <summary>Только для hold: вложено в продаваемое количество по средней цене входа, рубли
    /// (<see cref="PositionCostBasis.InvestedRub"/>). Null — неизвестно (налог продажи не оценивается).</summary>
    public decimal? InvestedRub { get; init; }

    /// <summary>Только для hold: журнал операций не покрывает весь остаток (<see cref="PositionCostBasis.HasUnknownLots"/>) —
    /// налог продажи не оценивается. Дефолт true: без cost basis налог оценить нельзя.</summary>
    public bool HasUnknownLots { get; init; } = true;
}

/// <summary>Итог анализа замены v2: оценка выгоды/проигрыша перехода с обязательным дисклеймером. Деньги — рубли, доходности — ДОЛИ, горизонты/дюрации — годы.</summary>
public sealed record SwitchAnalysisResult
{
    public required ulong HoldPositionId { get; init; }
    public required ulong TargetPositionId { get; init; }

    /// <summary>Горизонт, запрошенный пользователем (настройка/запрос), годы.</summary>
    public required decimal HorizonYears { get; init; }

    /// <summary>Горизонт, на котором реально сравниваются ноги: min(HorizonYears, срок до оферты/погашения цели), не меньше 0.25.</summary>
    public required decimal EffectiveHorizonYears { get; init; }

    /// <summary>true — горизонт урезан сроком цели; тогда заданы <see cref="TargetHorizonDate"/> и <see cref="TargetHorizonIsOffer"/>.</summary>
    public required bool HorizonLimitedByTarget { get; init; }
    public DateOnly? TargetHorizonDate { get; init; }
    public bool TargetHorizonIsOffer { get; init; }

    public required decimal SellCommissionRub { get; init; }
    public required decimal BuyCommissionRub { get; init; }

    /// <summary>Комиссии обеих сделок (продажа + покупка) — слиппедж и налог сюда НЕ входят (отдельные статьи).</summary>
    public required decimal TotalSwitchCostRub { get; init; }

    /// <summary>Слиппедж продажи hold: MV × полуспред hold, рубли (0, если стакан неизвестен).</summary>
    public required decimal SellSlippageRub { get; init; }

    /// <summary>Слиппедж покупки цели: выручка × полуспред цели, рубли (0, если стакан неизвестен).</summary>
    public required decimal BuySlippageRub { get; init; }

    /// <summary>true — у одной из ног (или обеих) нет bid/offer: слиппедж этой ноги принят за 0 и НЕ учтён.</summary>
    public required bool SlippageUnknown { get; init; }

    /// <summary>Оценка НДФЛ продажи hold (<see cref="SaleTaxEstimator"/>), рубли. Null — журнал операций неполон (налог в расчёт не вошёл).</summary>
    public decimal? SellTaxEstimateRub { get; init; }

    /// <summary>Доходность hold после НДФЛ (y × 0.87), ДОЛЯ.</summary>
    public required decimal HoldYieldAfterTaxFraction { get; init; }

    /// <summary>Доходность цели после НДФЛ (y × 0.87), ДОЛЯ.</summary>
    public required decimal TargetYieldAfterTaxFraction { get; init; }

    /// <summary>Спред ДО-налоговых эффективных доходностей (цель − hold), ДОЛЯ. Null — доходность одной из сторон не определена.</summary>
    public decimal? SpreadFraction { get; init; }

    /// <summary>Капитал, реально вложенный в цель: выручка минус комиссия и слиппедж покупки, рубли.</summary>
    public required decimal CapitalRub { get; init; }

    /// <summary>Выручка продажи hold после комиссии, слиппеджа и налога продажи (до издержек покупки), рубли.</summary>
    public required decimal ProceedsRub { get; init; }

    /// <summary>FV_hold = MV × (1 + y_hold_at)^h_eff, рубли.</summary>
    public required decimal HoldFutureValueRub { get; init; }

    /// <summary>FV_switch = invested × (1 + y_target_at)^h_eff, рубли (с налогом продажи, если он оценён).</summary>
    public required decimal SwitchFutureValueRub { get; init; }

    /// <summary>MV × ((1+y_target_at)^h − (1+y_hold_at)^h) — разница доходностей после НДФЛ на полной стоимости hold, ДО издержек. Null — доходность не определена.</summary>
    public decimal? GrossGainRub { get; init; }

    /// <summary>FV_switch − FV_hold БЕЗ налога продажи (но после НДФЛ на купоны, комиссий и слиппеджа), рубли.</summary>
    public required decimal NetBenefitRub { get; init; }

    /// <summary>FV_switch − FV_hold С налогом продажи, рубли. Null — налог продажи оценить нельзя (<see cref="SellTaxEstimateRub"/> = null).</summary>
    public decimal? NetBenefitAfterTaxRub { get; init; }

    /// <summary>(FV_switch/FV_hold)^(1/h_eff) − 1, годовая ДОЛЯ. Null — доходность не определена.</summary>
    public decimal? AnnualizedBenefitFraction { get; init; }

    /// <summary>Выгодна ли замена: известные доходности и FV_switch &gt; FV_hold (с налогом продажи, если он оценён).</summary>
    public required bool IsSwitchFavorable { get; init; }

    /// <summary>Минимальный горизонт (годы), на котором замена окупает издержки; null — не окупается (цель не доходнее после налогов) или дольше 30 лет.</summary>
    public decimal? BreakEvenYears { get; init; }

    /// <summary>true — у одной из позиций нет применимой доходности (флоатер без CurrentYield/несошедшийся YTM); результат недостоверен.</summary>
    public required bool YieldDataIncomplete { get; init; }

    public decimal? HoldMacaulayDurationYears { get; init; }
    public decimal? TargetMacaulayDurationYears { get; init; }

    /// <summary>|Маколей цели − Маколей hold| &gt; 1.5 года — процентный риск после замены заметно другой.</summary>
    public required bool DurationMismatch { get; init; }

    /// <summary>Текст предупреждения («Процентный риск изменится: дюрация X → Y лет»), если <see cref="DurationMismatch"/>.</summary>
    public string? DurationMismatchWarning { get; init; }

    public required string Disclaimer { get; init; }
}
