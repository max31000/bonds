using System.Text.RegularExpressions;
using Bonds.Core.Analytics;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Core.Time;
using Bonds.Infrastructure.Universe;

namespace Bonds.Api.Endpoints;

/// <summary>
/// Ручной ввод данных эмитента для скоркарты надёжности (задача 46 часть D; формы — задача 47):
/// рейтинги агентств (бесплатного источника нет) и строки годовой отчётности (когда ГИР БО скрыт/пуст — у
/// банков, лизинга, скрывших отчётность). Всё под общей FallbackPolicy (владелец). После записи кэш скоркарты
/// сбрасывается — изменение видно сразу, а не через час.
/// <para>
/// <b>Единицы отчётности: РУБЛИ</b> (не тысячи, как отдаёт ГИР БО): конверсия ×1000 нужна только на границе
/// парсинга ГИР БО, ручной ввод идёт сразу в рублях. Ручная запись (source='manual') ПРИОРИТЕТНЕЕ автозагрузки
/// за тот же год (<see cref="IIssuerFinancialsRepository.GetLatestByInnsAsync"/>) и не затирается обновлением ГИР БО.
/// </para>
/// </summary>
public static class IssuerEndpoints
{
    public static void MapIssuerEndpoints(this WebApplication app)
    {
        app.MapGet("/api/issuers/{inn}/ratings", GetRatings);
        app.MapPost("/api/issuers/{inn}/ratings", PostRating);
        app.MapDelete("/api/issuers/{inn}/ratings/{agency}", DeleteRating);

        app.MapGet("/api/issuers/{inn}/financials", GetFinancials);
        app.MapPost("/api/issuers/{inn}/financials", PostFinancials);
    }

    public const string Disclaimer =
        "Данные введены вручную владельцем и учитываются в скоркарте надёжности (оценка по открытым данным, " +
        "не кредитный рейтинг и не инвестиционная рекомендация).";

    /// <summary>ИНН юридического лица (10 цифр) или ИП/физлица (12 цифр), ведущие нули значимы.</summary>
    private static readonly Regex InnPattern = new(@"^(\d{10}|\d{12})$", RegexOptions.Compiled);

    /// <summary>Потолок абсолютного значения суммы, ₽ (колонки DECIMAL(20,2) вмещают ~10^18; ввод свыше 10^15 ₽ — опечатка).</summary>
    private const decimal MaxAmountRub = 1_000_000_000_000_000m;

    private const int MaxAgencyLength = 32;
    private const int MaxNoteLength = 512;
    private const int MinPeriod = 2000;

    // ─── Рейтинги ─────────────────────────────────────────────────────────────────────────

    private static async Task<IResult> GetRatings(string inn, IIssuerRatingRepository repo, CancellationToken ct)
    {
        if (!InnPattern.IsMatch(inn)) return AnalyticsSupport.ValidationError("inn должен состоять из 10 или 12 цифр");

        var rows = await repo.GetByInnAsync(inn, ct);
        return Results.Ok(new IssuerRatingsResponseDto
        {
            Inn = inn,
            Ratings = rows.Select(ToDto).ToList(),
            FreshMonths = ReliabilityScoreOptions.ManualRatingFreshMonths,
            Disclaimer = Disclaimer,
        });
    }

    private static async Task<IResult> PostRating(
        string inn, IssuerRatingRequestDto request, IIssuerRatingRepository repo, ReliabilityScoreProvider scoreProvider, CancellationToken ct)
    {
        if (!InnPattern.IsMatch(inn)) return AnalyticsSupport.ValidationError("inn должен состоять из 10 или 12 цифр");

        var agency = request.Agency?.Trim();
        if (string.IsNullOrEmpty(agency) || agency.Length > MaxAgencyLength)
        {
            return AnalyticsSupport.ValidationError($"agency обязателен и не длиннее {MaxAgencyLength} символов");
        }

        var rating = request.Rating?.Trim();
        if (!ReliabilityScoreService.TryNormalizeRating(rating, out _))
        {
            return AnalyticsSupport.ValidationError(
                "rating не распознан: ожидается шкала AAA/AA/A/BBB/BB/B/CCC/CC/C/D (допустимы +/−, суффикс (RU)/ru) или «отозван»");
        }

        if (rating!.Length > 16) return AnalyticsSupport.ValidationError("rating не длиннее 16 символов");

        if (request.RatingDate is not { } ratingDate) return AnalyticsSupport.ValidationError("ratingDate обязательна");
        var today = BusinessClock.MoscowToday();
        if (ratingDate > today) return AnalyticsSupport.ValidationError("ratingDate не может быть в будущем");
        if (ratingDate < today.AddYears(-10)) return AnalyticsSupport.ValidationError("ratingDate старше 10 лет — рейтинг не актуален");

        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note is { Length: > MaxNoteLength }) return AnalyticsSupport.ValidationError($"note не длиннее {MaxNoteLength} символов");

        var entity = new IssuerRating { Inn = inn, Agency = agency, Rating = rating, RatingDate = ratingDate, Note = note };
        await repo.UpsertAsync(entity, ct);
        scoreProvider.Invalidate();

        return Results.Ok(ToDto(entity));
    }

    private static async Task<IResult> DeleteRating(
        string inn, string agency, IIssuerRatingRepository repo, ReliabilityScoreProvider scoreProvider, CancellationToken ct)
    {
        if (!InnPattern.IsMatch(inn)) return AnalyticsSupport.ValidationError("inn должен состоять из 10 или 12 цифр");

        var deleted = await repo.DeleteAsync(inn, agency.Trim(), ct);
        if (!deleted) throw new Middleware.NotFoundException($"Рейтинг {agency} эмитента {inn} не найден");

        scoreProvider.Invalidate();
        return Results.NoContent();
    }

    private static IssuerRatingDto ToDto(IssuerRating r) => new()
    {
        Agency = r.Agency,
        Rating = r.Rating,
        RatingDate = r.RatingDate,
        Note = r.Note,
        IsStale = r.RatingDate < BusinessClock.MoscowToday().AddMonths(-ReliabilityScoreOptions.ManualRatingFreshMonths),
    };

    // ─── Отчётность ───────────────────────────────────────────────────────────────────────

    private static async Task<IResult> GetFinancials(
        string inn, IIssuerFinancialsRepository repo, CancellationToken ct)
    {
        if (!InnPattern.IsMatch(inn)) return AnalyticsSupport.ValidationError("inn должен состоять из 10 или 12 цифр");

        var byInn = await repo.GetLatestByInnsAsync([inn], yearsPerInn: 10, ct);
        var statuses = await repo.GetStatusesAsync([inn], ct);

        return Results.Ok(new IssuerFinancialsResponseDto
        {
            Inn = inn,
            GirBoStatus = statuses.TryGetValue(inn, out var status) ? status.Status : null,
            Years = byInn.TryGetValue(inn, out var rows) ? rows.Select(ToDto).ToList() : [],
            Disclaimer = Disclaimer,
        });
    }

    private static async Task<IResult> PostFinancials(
        string inn, IssuerFinancialsRequestDto request, IIssuerFinancialsRepository repo, ReliabilityScoreProvider scoreProvider, CancellationToken ct)
    {
        if (!InnPattern.IsMatch(inn)) return AnalyticsSupport.ValidationError("inn должен состоять из 10 или 12 цифр");

        var currentYear = BusinessClock.MoscowToday().Year;
        if (request.Period is not { } period || period < MinPeriod || period > currentYear)
        {
            return AnalyticsSupport.ValidationError($"period (отчётный год) обязателен, от {MinPeriod} до {currentYear}");
        }

        var amounts = new (string Name, decimal? Value, bool NonNegative)[]
        {
            ("revenue", request.Revenue, true),
            ("operatingProfit", request.OperatingProfit, false),
            ("interestPaid", request.InterestPaid, true),
            ("netProfit", request.NetProfit, false),
            ("equity", request.Equity, false),
            ("totalAssets", request.TotalAssets, true),
            ("currentAssets", request.CurrentAssets, true),
            ("currentLiabilities", request.CurrentLiabilities, true),
            ("cash", request.Cash, true),
            ("longDebt", request.LongDebt, true),
            ("shortDebt", request.ShortDebt, true),
            ("retainedEarnings", request.RetainedEarnings, false),
        };

        if (amounts.All(a => a.Value is null))
        {
            return AnalyticsSupport.ValidationError("укажите хотя бы одну строку отчётности (суммы — в рублях)");
        }

        foreach (var (name, value, nonNegative) in amounts)
        {
            if (value is not { } v) continue;
            if (Math.Abs(v) > MaxAmountRub) return AnalyticsSupport.ValidationError($"{name}: значение вне допустимого диапазона (суммы — в рублях, не в тысячах)");
            if (nonNegative && v < 0m) return AnalyticsSupport.ValidationError($"{name} не может быть отрицательным");
        }

        var entity = new IssuerFinancials
        {
            Inn = inn,
            Period = period,
            Source = IssuerFinancialsSources.Manual,
            FetchedAt = DateTime.UtcNow,
            Revenue = request.Revenue,
            OperatingProfit = request.OperatingProfit,
            InterestPaid = request.InterestPaid,
            NetProfit = request.NetProfit,
            Equity = request.Equity,
            TotalAssets = request.TotalAssets,
            CurrentAssets = request.CurrentAssets,
            CurrentLiabilities = request.CurrentLiabilities,
            Cash = request.Cash,
            LongDebt = request.LongDebt,
            ShortDebt = request.ShortDebt,
            RetainedEarnings = request.RetainedEarnings,
            HasAudit = request.HasAudit,
        };

        await repo.UpsertAsync([entity], ct);
        scoreProvider.Invalidate();

        return Results.Ok(ToDto(entity));
    }

    private static IssuerFinancialsYearDto ToDto(IssuerFinancials f) => new()
    {
        Period = f.Period,
        Source = f.Source,
        Revenue = f.Revenue,
        OperatingProfit = f.OperatingProfit,
        InterestPaid = f.InterestPaid,
        NetProfit = f.NetProfit,
        Equity = f.Equity,
        TotalAssets = f.TotalAssets,
        CurrentAssets = f.CurrentAssets,
        CurrentLiabilities = f.CurrentLiabilities,
        Cash = f.Cash,
        LongDebt = f.LongDebt,
        ShortDebt = f.ShortDebt,
        RetainedEarnings = f.RetainedEarnings,
        HasAudit = f.HasAudit,
    };
}

/// <summary>Тело POST /api/issuers/{inn}/ratings. Upsert по (inn, agency).</summary>
public sealed record IssuerRatingRequestDto
{
    /// <summary>Агентство («АКРА», «Эксперт РА», «НКР», «НРА»), ≤ 32 символов.</summary>
    public string? Agency { get; init; }

    /// <summary>Рейтинг: AAA/AA/A/BBB/BB/B/CCC/CC/C/D (допустимы +/−, суффикс (RU)/ru) или «отозван».</summary>
    public string? Rating { get; init; }

    /// <summary>Дата присвоения/подтверждения (не в будущем). Рейтинг старше 18 месяцев «протух» и не даёт баллов.</summary>
    public DateOnly? RatingDate { get; init; }

    public string? Note { get; init; }
}

public sealed record IssuerRatingDto
{
    public required string Agency { get; init; }
    public required string Rating { get; init; }
    public required DateOnly RatingDate { get; init; }
    public string? Note { get; init; }

    /// <summary>true — рейтинг старше срока годности и в скоркарте считается «нет данных».</summary>
    public required bool IsStale { get; init; }
}

public sealed record IssuerRatingsResponseDto
{
    public required string Inn { get; init; }
    public required IReadOnlyList<IssuerRatingDto> Ratings { get; init; }

    /// <summary>Срок годности рейтинга, месяцы.</summary>
    public required int FreshMonths { get; init; }
    public required string Disclaimer { get; init; }
}

/// <summary>Тело POST /api/issuers/{inn}/financials. ВСЕ суммы — РУБЛИ; не заданное поле = «строка не заполнена».
/// Запись заменяет прежний ручной ввод за этот год целиком.</summary>
public sealed record IssuerFinancialsRequestDto
{
    /// <summary>Отчётный год (2000..текущий).</summary>
    public int? Period { get; init; }

    /// <summary>Выручка (форма 2, 2110), ₽.</summary>
    public decimal? Revenue { get; init; }

    /// <summary>Прибыль (убыток) от продаж (2200), ₽.</summary>
    public decimal? OperatingProfit { get; init; }

    /// <summary>Проценты к уплате (2330), ₽.</summary>
    public decimal? InterestPaid { get; init; }

    /// <summary>Чистая прибыль (убыток) (2400), ₽.</summary>
    public decimal? NetProfit { get; init; }

    /// <summary>Капитал и резервы (1300), ₽.</summary>
    public decimal? Equity { get; init; }

    /// <summary>Баланс — активы всего (1600), ₽.</summary>
    public decimal? TotalAssets { get; init; }

    /// <summary>Оборотные активы (1200), ₽.</summary>
    public decimal? CurrentAssets { get; init; }

    /// <summary>Краткосрочные обязательства (1500), ₽.</summary>
    public decimal? CurrentLiabilities { get; init; }

    /// <summary>Денежные средства (1250), ₽.</summary>
    public decimal? Cash { get; init; }

    /// <summary>Долгосрочные заёмные средства (1410), ₽.</summary>
    public decimal? LongDebt { get; init; }

    /// <summary>Краткосрочные заёмные средства (1510), ₽.</summary>
    public decimal? ShortDebt { get; init; }

    /// <summary>Нераспределённая прибыль (1370), ₽.</summary>
    public decimal? RetainedEarnings { get; init; }

    public bool? HasAudit { get; init; }
}

/// <summary>Один отчётный год (суммы — РУБЛИ), <see cref="Source"/> — "girbo" | "manual".</summary>
public sealed record IssuerFinancialsYearDto
{
    public required int Period { get; init; }
    public required string Source { get; init; }
    public decimal? Revenue { get; init; }
    public decimal? OperatingProfit { get; init; }
    public decimal? InterestPaid { get; init; }
    public decimal? NetProfit { get; init; }
    public decimal? Equity { get; init; }
    public decimal? TotalAssets { get; init; }
    public decimal? CurrentAssets { get; init; }
    public decimal? CurrentLiabilities { get; init; }
    public decimal? Cash { get; init; }
    public decimal? LongDebt { get; init; }
    public decimal? ShortDebt { get; init; }
    public decimal? RetainedEarnings { get; init; }
    public bool? HasAudit { get; init; }
}

public sealed record IssuerFinancialsResponseDto
{
    public required string Inn { get; init; }

    /// <summary>Статус последней проверки ГИР БО: ok|not_found|hidden|cb|error; null — не проверялся.</summary>
    public string? GirBoStatus { get; init; }

    /// <summary>До 10 последних лет, новейший первым; за год с ручной и автоматической записью — ручная.</summary>
    public required IReadOnlyList<IssuerFinancialsYearDto> Years { get; init; }
    public required string Disclaimer { get; init; }
}
