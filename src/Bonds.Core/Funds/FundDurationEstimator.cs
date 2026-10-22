using Bonds.Core.Models;

namespace Bonds.Core.Funds;

/// <summary>Предупреждение оценки дюрации фонда: <c>IndexStale</c> — точка индекса старше <see cref="FundOutlookService.IndexMaxAgeDays"/> дней;
/// <c>IndexYieldUnknown</c> — у точки нет доходности, поправка Маколей→модифицированная не применена.</summary>
public static class FundDurationWarnings
{
    public const string IndexStale = "IndexStale";
    public const string IndexYieldUnknown = "IndexYieldUnknown";
}

/// <summary>
/// Оценка МОДИФИЦИРОВАННОЙ дюрации фонда. <see cref="ModifiedDurationYears"/> — в <b>ГОДАХ</b>; <see cref="Source"/> — <c>"manual"</c>,
/// <c>"index:RGBI"</c> (код индекса) или null (оценки нет); <see cref="AsOf"/> — дата точки индекса (для manual — null).
/// Это ОЦЕНКА, а не измерение: фонд активно управляется, дюрация его портфеля ≠ дюрации индекса.
/// </summary>
public sealed record FundDurationEstimate(
    decimal? ModifiedDurationYears,
    string? Source,
    DateOnly? AsOf,
    IReadOnlyList<string> Warnings)
{
    public const string ManualSource = "manual";
    public const string IndexSourcePrefix = "index:";
}

/// <summary>
/// Оценка модифицированной дюрации фонда (задача 57), чистая функция. Порядок:
/// <list type="number">
/// <item>есть ручная <c>funds.duration_override_years</c> (годы) — берём её, источник <c>manual</c>;</item>
/// <item>иначе, если индекс применим к фонду (явный <c>benchmark_index</c> ИЛИ ОФЗ-направленность — то же правило, что у
/// <see cref="FundOutlookService"/> для IndexYield; код — <see cref="FundOutlookService.ResolveIndexCode"/>) и есть точка индекса с дюрацией:
/// <c>D_mod = D_mac / (1 + y/2)</c>, где <c>D_mac</c> — дюрация Маколея индекса (MOEX DURATION в днях ÷ 365 уже при записи → ГОДЫ),
/// <c>y</c> — <c>yield_fraction</c> ТОЙ ЖЕ точки (ДОЛЯ); делитель <c>(1 + y/2)</c> — полугодовой купон ОФЗ. Точка старше
/// <see cref="FundOutlookService.IndexMaxAgeDays"/> дней используется с предупреждением <c>IndexStale</c>;</item>
/// <item>иначе null: фонд флоатеров/коротких бумаг без индекса — «чувствительность неизвестна, задайте вручную».</item>
/// </list>
/// Почему это именно оценка: дюрация индекса RGBI не равна дюрации портфеля конкретного БПИФ (управляющий отклоняется от индекса,
/// держит деньги/короткие бумаги), поэтому в интерфейсе она всегда подписывается как «оценка».
/// </summary>
public static class FundDurationEstimator
{
    /// <summary>Оценка по данным фонда: настройки (override/benchmark) + точка индекса, выбранного через <see cref="FundOutlookService.ResolveIndexCode"/>.</summary>
    public static FundDurationEstimate Estimate(Fund fund, MarketIndexPoint? indexPoint, DateOnly asOf) =>
        Estimate(
            fund.DurationOverrideYears,
            FundIndexApplies(fund),
            FundOutlookService.ResolveIndexCode(fund.BenchmarkIndex),
            indexPoint,
            asOf);

    /// <summary>Индекс применим к фонду для дюрации: явный benchmark или ОФЗ-направленность (иначе RGBI «по умолчанию» к фонду флоатеров не относится).</summary>
    public static bool FundIndexApplies(Fund fund) =>
        !string.IsNullOrWhiteSpace(fund.BenchmarkIndex)
        || FundOutlookService.IsOfzOriented(fund.FocusType, fund.Name, fund.Ticker);

    public static FundDurationEstimate Estimate(
        decimal? overrideYears, bool indexApplies, string indexCode, MarketIndexPoint? indexPoint, DateOnly asOf)
    {
        if (overrideYears is { } manual)
            return new FundDurationEstimate(manual, FundDurationEstimate.ManualSource, null, []);

        if (!indexApplies || indexPoint?.DurationYears is not { } macaulay)
            return new FundDurationEstimate(null, null, null, []);

        var warnings = new List<string>();
        if (asOf.DayNumber - indexPoint.TradeDate.DayNumber > FundOutlookService.IndexMaxAgeDays)
            warnings.Add(FundDurationWarnings.IndexStale);

        decimal modified;
        if (indexPoint.YieldFraction is { } y)
        {
            // D_mod = D_mac / (1 + y/2): полугодовой купон ОФЗ. y — ДОЛЯ (0.1603), D_mac — ГОДЫ.
            modified = macaulay / (1m + y / 2m);
        }
        else
        {
            modified = macaulay;
            warnings.Add(FundDurationWarnings.IndexYieldUnknown);
        }

        return new FundDurationEstimate(
            modified, FundDurationEstimate.IndexSourcePrefix + indexCode, indexPoint.TradeDate, warnings);
    }
}
