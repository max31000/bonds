using Bonds.Core.Models;

namespace Bonds.Core.Calculation;

/// <summary>
/// Фасад движка расчётов (plan/05 Часть A–C): собирает НКД, грязную цену, YTM/доходность
/// к оферте, дюрации, выпуклость, PVBP и G-спред в единый <see cref="BondMetrics"/>, уважая
/// все обязательные краевые случаи (флоатер, индексируемая бумага, амортизация, оферта,
/// неполные данные — plan/05 Часть B). Чистая функция, без I/O: вся информация приходит
/// через <see cref="BondMetricsCalculatorInput"/>, никаких обращений к репозиториям/сети.
/// <para>
/// Контракт: метод никогда не бросает исключение из-за качества бизнес-данных (отсутствующая
/// цена, неполный график, нулевые купоны и т.п.) — вместо этого возвращает результат с
/// соответствующими флагами/null-полями (spec §4.4). Он, однако, НЕ валидирует входные данные
/// на бизнес-уровне (например, не проверяет, что ISIN существует или что FaceValue
/// положителен в "разумных" пределах) — это ответственность вызывающего слоя.
/// </para>
/// </summary>
public static class BondMetricsCalculator
{
    public static BondMetrics Calculate(BondMetricsCalculatorInput input)
    {
        var notes = new List<string>();
        var isFloater = input.CouponType == CouponType.Floating;
        var isIndexed = input.CouponType == CouponType.Indexed;
        var isFloaterLike = isFloater || isIndexed;

        if (input.DataIncomplete)
        {
            notes.Add("Данные по инструменту неполные (MOEX bondization вернул неполный график или SECID не сопоставлен) — метрики недостоверны.");
        }

        // ─── Цена и НКД ─────────────────────────────────────────────────────
        var cleanPrice = input.CleanPrice;
        decimal? accrued = input.AccruedInterestFromSource;
        var accruedEstimatedByEngine = false;

        if (accrued is null)
        {
            accrued = AccruedInterestCalculator.Calculate(input.AsOf, input.Coupons);
            accruedEstimatedByEngine = accrued is not null;
            if (accrued is null)
            {
                notes.Add("НКД не из источника и не удалось посчитать фолбэком (нет данных о следующем купоне).");
            }
        }

        decimal? dirtyPrice = null;
        if (cleanPrice is not null && accrued is not null)
        {
            dirtyPrice = AccruedInterestCalculator.DirtyPrice(cleanPrice.Value, accrued.Value);
        }
        else if (cleanPrice is null)
        {
            notes.Add("Чистая цена недоступна — производные метрики (YTM, дюрация, PVBP, G-спред) не считаются.");
        }

        // ─── Горизонт: оферта или погашение (spec §7.3) ────────────────────
        var horizon = OfferCutoffResolver.Resolve(input.AsOf, input.MaturityDate, input.Offers);
        if (horizon.IsOffer)
        {
            notes.Add($"Метрики рассчитаны к ближайшей неисполненной оферте {horizon.Date:yyyy-MM-dd}, не к погашению (spec §7.3).");
        }

        // ─── Текущая доходность (всегда, когда возможно — нужна и флоатеру, и как доп. метрика обычной бумаге) ───
        decimal? currentYield = dirtyPrice is not null
            ? CurrentYieldCalculator.Calculate(input.AsOf, dirtyPrice.Value, input.Coupons)
            : null;

        // ─── Флоатер / индексируемая бумага: YTM не считаем (spec §6 «Краевые случаи») ───
        if (isFloaterLike)
        {
            notes.Add(isFloater
                ? "Плавающий купон — YTM не рассчитывается; показана текущая купонная доходность."
                : "Индексируемая бумага — будущий номинал/купон зависят от инфляции, YTM не рассчитывается; показана текущая доходность (оценка).");

            return new BondMetrics
            {
                InstrumentId = input.InstrumentId,
                AsOf = input.AsOf,
                CleanPrice = cleanPrice ?? 0m,
                AccruedInterest = accrued ?? 0m,
                DirtyPrice = dirtyPrice ?? 0m,
                AccruedInterestEstimatedByEngine = accruedEstimatedByEngine,
                CurrentYield = currentYield,
                YtmEffective = null,
                YtmSimple = null,
                MacaulayDuration = null,
                ModifiedDuration = null,
                Convexity = null,
                Pvbp = null,
                GSpread = null,
                HorizonDate = horizon.Date,
                IsFloater = isFloater,
                IsIndexed = isIndexed,
                IsEstimated = true,
                DataIncomplete = input.DataIncomplete,
                CalculatedToOffer = horizon.IsOffer,
                HasAmortization = input.HasAmortization,
                YtmConvergedByNewton = null,
                Notes = notes,
            };
        }

        // ─── Фикс-купонная (возможно, амортизируемая, возможно, с офертой) бумага ───
        if (dirtyPrice is null)
        {
            return new BondMetrics
            {
                InstrumentId = input.InstrumentId,
                AsOf = input.AsOf,
                CleanPrice = cleanPrice ?? 0m,
                AccruedInterest = accrued ?? 0m,
                DirtyPrice = 0m,
                AccruedInterestEstimatedByEngine = accruedEstimatedByEngine,
                CurrentYield = currentYield,
                HorizonDate = horizon.Date,
                IsFloater = false,
                IsIndexed = false,
                IsEstimated = true,
                DataIncomplete = true,
                CalculatedToOffer = horizon.IsOffer,
                HasAmortization = input.HasAmortization,
                Notes = notes,
            };
        }

        var cleanPriceValue = cleanPrice!.Value; // dirtyPrice не null ⇒ cleanPrice не null (см. сборку выше)
        var dirtyPriceValue = dirtyPrice.Value;

        var cashFlow = BondCashFlowBuilder.Build(input.FaceValue, input.AsOf, horizon.Date, input.Coupons, input.Amortizations);

        var hasUnknownFlows = cashFlow.Any(c => !c.IsKnown);
        if (hasUnknownFlows)
        {
            notes.Add("В денежном потоке есть купоны с неизвестной суммой в пределах горизонта — YTM не рассчитывается.");
        }

        var ytm = hasUnknownFlows ? null : YtmCalculator.Calculate(dirtyPriceValue, input.AsOf, cashFlow);
        var isEstimated = input.DataIncomplete || hasUnknownFlows;

        decimal? macaulay = null;
        decimal? modified = null;
        decimal? convexity = null;
        decimal? pvbp = null;
        decimal? gSpread = null;

        if (ytm is not null)
        {
            var couponsPerYear = CouponFrequencyEstimator.EstimateCouponsPerYear(input.Coupons);
            var duration = DurationCalculator.Calculate(dirtyPriceValue, ytm.Value.EffectiveYield, input.AsOf, cashFlow, couponsPerYear);

            if (duration is not null)
            {
                macaulay = duration.Value.MacaulayDurationYears;
                modified = duration.Value.ModifiedDuration;
                convexity = duration.Value.Convexity;
                pvbp = duration.Value.Pvbp;

                if (input.CurveSnapshot is not null)
                {
                    gSpread = GSpreadCalculator.GSpread(ytm.Value.EffectiveYield, macaulay.Value, input.CurveSnapshot);
                }
            }
        }
        else if (cashFlow.Count == 0)
        {
            notes.Add("Денежный поток до горизонта пуст (нет купонов/амортизаций в графике) — YTM и производные метрики не рассчитаны.");
            isEstimated = true;
        }
        else if (!hasUnknownFlows)
        {
            notes.Add("YTM не сошёлся (решатель не нашёл корень даже с фолбэком на бисекцию) — проверьте входные данные.");
            isEstimated = true;
        }

        return new BondMetrics
        {
            InstrumentId = input.InstrumentId,
            AsOf = input.AsOf,
            CleanPrice = cleanPriceValue,
            AccruedInterest = accrued ?? 0m,
            DirtyPrice = dirtyPriceValue,
            AccruedInterestEstimatedByEngine = accruedEstimatedByEngine,
            CurrentYield = currentYield,
            YtmEffective = ytm?.EffectiveYield,
            YtmSimple = ytm?.SimpleYield,
            MacaulayDuration = macaulay,
            ModifiedDuration = modified,
            Convexity = convexity,
            Pvbp = pvbp,
            GSpread = gSpread,
            HorizonDate = horizon.Date,
            IsFloater = false,
            IsIndexed = false,
            IsEstimated = isEstimated,
            DataIncomplete = input.DataIncomplete,
            CalculatedToOffer = horizon.IsOffer,
            HasAmortization = input.HasAmortization,
            YtmConvergedByNewton = ytm?.ConvergedByNewton,
            Notes = notes,
        };
    }
}
