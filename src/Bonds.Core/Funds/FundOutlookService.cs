using System.Globalization;
using Bonds.Core.Analytics;
using Bonds.Core.Models;

namespace Bonds.Core.Funds;

/// <summary>Уверенность в оценке ожидаемой доходности фонда (эвристика, не статистика).</summary>
public enum FundOutlookConfidence { Low, Medium, High }

/// <summary>Предупреждения оценки: <c>TerUnknown</c> — комиссия фонда неизвестна (принята 0); <c>IndexStale</c> — точка индекса старше
/// <see cref="FundOutlookService.IndexMaxAgeDays"/> дней; <c>FewDistributions</c> — мало выплат для метода по выплатам;
/// <c>ShortHistory</c> — истории цен меньше года (окно аннуализировано).</summary>
public enum FundOutlookWarning { TerUnknown, IndexStale, FewDistributions, ShortHistory }

/// <summary>
/// Вход оценки. Деньги — РУБЛИ (цена — за пай), доли — ДОЛИ. <see cref="IndexCode"/>/<see cref="IndexPoint"/> — индекс, ВЫБРАННЫЙ вызывающим
/// через <see cref="FundOutlookService.ResolveIndexCode"/> (последняя точка с известной доходностью, может быть null).
/// </summary>
public sealed record FundOutlookInput(
    FundForecastMethod RequestedMethod,
    string? FocusType,
    string? Name,
    string? Ticker,
    decimal? TerFraction,
    decimal? PriceRub,
    decimal Quantity,
    decimal MarketValueRub,
    IReadOnlyList<FundPricePoint> Prices,
    IReadOnlyList<FundDistribution> Distributions,
    string IndexCode,
    bool HasExplicitBenchmark,
    MarketIndexPoint? IndexPoint,
    DateOnly AsOf);

/// <summary>
/// Оценка ожидаемой годовой доходности фонда. <see cref="Method"/> — реально применённый (или попытка, если оценки нет —
/// тогда <see cref="UnavailableReason"/> непустой, а числовые поля null). Все доли — ДОЛИ годовых, деньги — РУБЛИ.
/// Это ОЦЕНКА, а не обещание: у фондов с переменным купоном выплаты следуют за ключевой ставкой.
/// </summary>
public sealed record FundOutlook(
    FundForecastMethod RequestedMethod,
    FundForecastMethod Method,
    decimal? ExpectedAnnualYieldFraction,
    decimal? AfterTaxYieldFraction,
    string BasisLabel,
    decimal? ExpectedIncome1yRub,
    decimal? ExpectedMonthlyIncomeRub,
    FundOutlookConfidence Confidence,
    DateOnly AsOf,
    IReadOnlyList<FundOutlookWarning> Warnings,
    string? UnavailableReason);

/// <summary>
/// Оценка ожидаемой доходности фонда (задача 53), чистая функция. Методы (<see cref="FundForecastMethod"/>):
/// <list type="bullet">
/// <item><b>Distributions</b> — <c>Σ выплат на пай за последние 92 дня × (365/92) / цена</c> (выплаты уже после TER), нужна ≥1 выплата
/// в окне; в Auto выбирается при ≥ 2 выплатах за 6 мес. Месячный доход = последняя выплата × кол-во паёв.</item>
/// <item><b>IndexYield</b> — <c>доходность индекса (YIELD/100) − TER</c> (TER неизвестен → 0 + <c>TerUnknown</c>); точка старше
/// 10 дней используется, но с <c>IndexStale</c> и Low-уверенностью. В Auto — если задан benchmark или фонд ОФЗ-направленности.</item>
/// <item><b>TrailingReturn</b> — фактическая доходность за 12 мес.; если года истории нет — окно от первой цены (≥ 90 дней), аннуализированное.</item>
/// </list>
/// Явный метод ≠ Auto не подменяется (нет данных — null + причина). Auto, выбравший IndexYield, но не нашедший точки индекса,
/// откатывается на TrailingReturn (документированный фолбэк только для Auto). После-налоговая — <see cref="AfterTaxYield.Apply"/>
/// (единая ставка НДФЛ; упрощение — налог ко всей доходности).
/// </summary>
public static class FundOutlookService
{
    public const int DistributionWindowDays = 92;
    public const int AutoLookbackMonths = 6;
    public const int AutoMinDistributions = 2;
    public const int IndexMaxAgeDays = 10;
    public const int MinTrailingHistoryDays = 90;
    public const int FullTrailingHistoryDays = 180;
    public const string DefaultIndexCode = "RGBI";

    /// <summary>Код индекса для фонда: явный benchmark (заглавными) или <see cref="DefaultIndexCode"/>.</summary>
    public static string ResolveIndexCode(string? benchmarkIndex) =>
        string.IsNullOrWhiteSpace(benchmarkIndex) ? DefaultIndexCode : benchmarkIndex.Trim().ToUpperInvariant();

    /// <summary>Фонд ОФЗ-направленности: focus_type fixed_income И имя/тикер содержит «ОФЗ»/«OFZ».</summary>
    public static bool IsOfzOriented(string? focusType, string? name, string? ticker)
    {
        if (!string.Equals(focusType?.Trim(), "fixed_income", StringComparison.OrdinalIgnoreCase)) return false;
        bool Has(string? s) => s is not null &&
            (s.Contains("ОФЗ", StringComparison.OrdinalIgnoreCase) || s.Contains("OFZ", StringComparison.OrdinalIgnoreCase));
        return Has(name) || Has(ticker);
    }

    public static FundOutlook Compute(FundOutlookInput input)
    {
        var method = input.RequestedMethod;
        if (method != FundForecastMethod.Auto) return Evaluate(input, method);

        var recent = input.Distributions.Count(d => d.RecordDate > input.AsOf.AddMonths(-AutoLookbackMonths) && d.RecordDate <= input.AsOf);
        if (recent >= AutoMinDistributions)
        {
            var viaDist = Evaluate(input, FundForecastMethod.Distributions);
            if (viaDist.ExpectedAnnualYieldFraction is not null) return viaDist;
        }

        if (input.HasExplicitBenchmark || IsOfzOriented(input.FocusType, input.Name, input.Ticker))
        {
            var viaIndex = Evaluate(input, FundForecastMethod.IndexYield);
            if (viaIndex.ExpectedAnnualYieldFraction is not null) return viaIndex;
        }

        return Evaluate(input, FundForecastMethod.TrailingReturn);
    }

    private static FundOutlook Evaluate(FundOutlookInput i, FundForecastMethod method) => method switch
    {
        FundForecastMethod.Distributions => ByDistributions(i),
        FundForecastMethod.IndexYield => ByIndex(i),
        _ => ByTrailing(i),
    };

    private static FundOutlook ByDistributions(FundOutlookInput i)
    {
        var warnings = new List<FundOutlookWarning>();
        var windowStart = i.AsOf.AddDays(-DistributionWindowDays);
        var inWindow = i.Distributions.Where(d => d.RecordDate > windowStart && d.RecordDate <= i.AsOf).ToList();
        var recent6m = i.Distributions.Count(d => d.RecordDate > i.AsOf.AddMonths(-AutoLookbackMonths) && d.RecordDate <= i.AsOf);

        if (i.PriceRub is not { } price || price <= 0m)
            return Unavailable(i, FundForecastMethod.Distributions, "нет текущей цены пая");
        if (inWindow.Count == 0)
            return Unavailable(i, FundForecastMethod.Distributions, "нет выплат за последние 3 месяца");

        if (recent6m < AutoMinDistributions) warnings.Add(FundOutlookWarning.FewDistributions);

        var sum = inWindow.Sum(d => d.AmountPerShareRub);
        var y = sum * (365m / DistributionWindowDays) / price;
        var last = i.Distributions.Where(d => d.RecordDate <= i.AsOf).OrderByDescending(d => d.RecordDate).First();
        var confidence = inWindow.Count >= 3 ? FundOutlookConfidence.High : FundOutlookConfidence.Medium;
        if (warnings.Contains(FundOutlookWarning.FewDistributions)) confidence = FundOutlookConfidence.Low;

        return Build(i, FundForecastMethod.Distributions, y, "по последним выплатам за 3 мес.", confidence, warnings,
            monthlyRub: last.AmountPerShareRub * i.Quantity);
    }

    private static FundOutlook ByIndex(FundOutlookInput i)
    {
        var warnings = new List<FundOutlookWarning>();
        if (i.IndexPoint?.YieldFraction is not { } indexYield)
            return Unavailable(i, FundForecastMethod.IndexYield, $"нет данных по индексу {i.IndexCode}");

        if (i.AsOf.DayNumber - i.IndexPoint.TradeDate.DayNumber > IndexMaxAgeDays) warnings.Add(FundOutlookWarning.IndexStale);

        decimal ter;
        string label;
        if (i.TerFraction is { } t)
        {
            ter = t;
            label = $"доходность индекса {i.IndexCode} {Pct(indexYield)} − комиссия фонда {Pct(t)}";
        }
        else
        {
            ter = 0m;
            warnings.Add(FundOutlookWarning.TerUnknown);
            label = $"доходность индекса {i.IndexCode} {Pct(indexYield)} (комиссия фонда неизвестна)";
        }

        var confidence = warnings.Count > 0 ? FundOutlookConfidence.Low : FundOutlookConfidence.Medium;
        return Build(i, FundForecastMethod.IndexYield, indexYield - ter, label, confidence, warnings, monthlyRub: null);
    }

    private static FundOutlook ByTrailing(FundOutlookInput i)
    {
        var warnings = new List<FundOutlookWarning>();
        var w12 = FundPerformanceCalculator.ComputeWindow(i.Prices, i.Distributions, i.AsOf.AddMonths(-12), i.AsOf);
        if (w12 is { AnnualizedFraction: { } a12 })
            return Build(i, FundForecastMethod.TrailingReturn, a12, "фактическая доходность за 12 мес.",
                FundOutlookConfidence.Medium, warnings, monthlyRub: null);

        if (i.Prices.Count == 0)
            return Unavailable(i, FundForecastMethod.TrailingReturn, "нет истории цен — мало данных");

        var first = i.Prices.Min(p => p.TradeDate);
        var span = i.AsOf.DayNumber - first.DayNumber;
        if (span < MinTrailingHistoryDays)
            return Unavailable(i, FundForecastMethod.TrailingReturn, $"мало данных: история цен {span} дн. (нужно не менее {MinTrailingHistoryDays})");

        var w = FundPerformanceCalculator.ComputeWindow(i.Prices, i.Distributions, first, i.AsOf);
        if (w is not { AnnualizedFraction: { } annualized }) return Unavailable(i, FundForecastMethod.TrailingReturn, "мало данных");

        warnings.Add(FundOutlookWarning.ShortHistory);
        return Build(i, FundForecastMethod.TrailingReturn, annualized,
            $"фактическая доходность за {w.Days} дн., пересчитанная в годовую",
            span >= FullTrailingHistoryDays ? FundOutlookConfidence.Medium : FundOutlookConfidence.Low, warnings, monthlyRub: null);
    }

    private static FundOutlook Build(
        FundOutlookInput i, FundForecastMethod method, decimal y, string label,
        FundOutlookConfidence confidence, List<FundOutlookWarning> warnings, decimal? monthlyRub) =>
        new(i.RequestedMethod, method, y, AfterTaxYield.Apply(y), label, i.MarketValueRub * y, monthlyRub,
            confidence, i.AsOf, warnings, null);

    private static FundOutlook Unavailable(FundOutlookInput i, FundForecastMethod method, string reason) =>
        new(i.RequestedMethod, method, null, null, string.Empty, null, null, FundOutlookConfidence.Low, i.AsOf, [], reason);

    /// <summary>ДОЛЯ → строка «16.03%» (×100 только для человекочитаемой подписи; числовые поля остаются долями).</summary>
    private static string Pct(decimal fraction) => (fraction * 100m).ToString("0.00", CultureInfo.InvariantCulture) + "%";
}
