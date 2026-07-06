using System.Security.Claims;
using Bonds.Api.Middleware;
using Bonds.Core.Analytics;
using Bonds.Core.Interfaces;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Core.Signals;
using Bonds.Infrastructure.Connectors.TInvest;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Bonds.Api.Endpoints;

/// <summary>
/// GET/PUT /api/settings — пороги триггеров (Signals Engine, этап 07) и базовая валюта.
/// PUT /api/settings/tinvest-token — ввод/замена read-only токена T-Invest (plan/08).
/// <para>
/// <b>Безопасность токена (spec §11, plan/08 "Токен T-Invest через UI").</b> Токен шифруется
/// через <see cref="IDataProtectionProvider"/> (встроенный механизм ASP.NET Core — не требует
/// нового секрета в конфиге, в отличие от ручного AES с собственным ключом) и сохраняется в
/// <c>user_settings.tinvest_token_encrypted</c>. GET никогда не возвращает токен — только
/// булевый статус "задан" и маску из последних 4 символов (<c>TInvestTokenLast4</c>,
/// сохраняется отдельно в открытом виде ИСКЛЮЧИТЕЛЬНО для маски, это не секрет сам по себе).
/// Токен не логируется ни на одном пути (ни в этом файле, ни в <c>ErrorHandlingMiddleware</c>,
/// который логирует только <c>ex.Message</c>/тип исключения, не тела запросов).
/// </para>
/// <para>
/// <b>Валидация перед сохранением (plan/13 часть C).</b> PUT делает пробный вызов T-Invest
/// (<see cref="ITInvestTokenValidator"/>) ДО шифрования/записи в БД — невалидный токен не
/// сохраняется и отвечает 422 с человекочитаемым сообщением, чтобы пользователь узнавал об
/// опечатке/протухшем токене сразу, а не по тихо деградировавшему синку.
/// </para>
/// </summary>
public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/settings", GetSettings);
        app.MapPut("/api/settings", PutSettings);
        app.MapPut("/api/settings/tinvest-token", PutTInvestToken);
    }

    /// <summary>Валидация override (plan/22 часть D): null допустим (сброс), иначе строго 0 &lt; x &lt; 0.05 — ставка ≥5% явно опечатка (ввод в % на фронте, доля на бэкенде).</summary>
    private const decimal MaxCommissionRateOverride = 0.05m;

    private static async Task<IResult> GetSettings(
        ClaimsPrincipal principal,
        IUserRepository userRepo,
        IUserSettingsRepository settingsRepo,
        IAccountRepository accountRepo,
        IOperationRepository operationRepo,
        ICommissionRateProvider commissionRateProvider,
        ITInvestPortfolioClient tInvestClient,
        IOptions<SignalEngineOptions> defaultSignalOptions,
        CancellationToken ct)
    {
        var userId = ResolveUserId(principal);
        var user = await userRepo.GetByIdAsync(userId);
        if (user is null) throw new NotFoundException("Пользователь не найден");

        var settings = await settingsRepo.GetByUserIdAsync(userId);
        var context = await BuildCommissionContextAsync(
            principal, accountRepo, operationRepo, commissionRateProvider, tInvestClient, ct);

        return Results.Ok(BuildSettingsDto(user, settings, defaultSignalOptions.Value, context));
    }

    private static async Task<IResult> PutSettings(
        SettingsUpdateRequestDto request,
        ClaimsPrincipal principal,
        IUserRepository userRepo,
        IUserSettingsRepository settingsRepo,
        IAccountRepository accountRepo,
        IOperationRepository operationRepo,
        ICommissionRateProvider commissionRateProvider,
        ITInvestPortfolioClient tInvestClient,
        IOptions<SignalEngineOptions> defaultSignalOptions,
        CancellationToken ct)
    {
        var userId = ResolveUserId(principal);
        var user = await userRepo.GetByIdAsync(userId);
        if (user is null) throw new NotFoundException("Пользователь не найден");

        if (request.CommissionRateOverride is decimal overrideRate
            && (overrideRate <= 0m || overrideRate >= MaxCommissionRateOverride))
        {
            return Results.Json(
                new
                {
                    error = $"Ставка комиссии должна быть в диапазоне (0, {MaxCommissionRateOverride:0.##}) — похоже на опечатку",
                    type = "ValidationException",
                },
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        if (request.BaseCurrency is not null)
        {
            user.BaseCurrency = request.BaseCurrency;
            await userRepo.UpdateAsync(user);
        }

        var existing = await settingsRepo.GetByUserIdAsync(userId) ?? new UserSettings { UserId = userId };

        existing.UpcomingEventDaysThreshold = request.UpcomingEventDaysThreshold ?? existing.UpcomingEventDaysThreshold;
        existing.UninvestedCashThresholdRub = request.UninvestedCashThresholdRub ?? existing.UninvestedCashThresholdRub;
        existing.UninvestedCashLookbackDays = request.UninvestedCashLookbackDays ?? existing.UninvestedCashLookbackDays;
        existing.YieldBelowAlternativeBpsThreshold = request.YieldBelowAlternativeBpsThreshold ?? existing.YieldBelowAlternativeBpsThreshold;
        existing.MaturityWindowDaysForAlternativeComparison = request.MaturityWindowDaysForAlternativeComparison ?? existing.MaturityWindowDaysForAlternativeComparison;
        existing.DefaultMaxConcentrationPercent = request.DefaultMaxConcentrationPercent ?? existing.DefaultMaxConcentrationPercent;
        existing.DurationDriftToleranceYears = request.DurationDriftToleranceYears ?? existing.DurationDriftToleranceYears;

        // CommissionRateOverride — фронт всегда шлёт текущее значение поля формы целиком (как и
        // остальные пороги ниже в Settings.tsx), поэтому null здесь означает "убрать override",
        // не "не менять" (в отличие от полей выше, где PUT — частичное обновление per-поле).
        existing.CommissionRateOverride = request.CommissionRateOverride;

        await settingsRepo.UpsertAsync(existing);

        var context = await BuildCommissionContextAsync(
            principal, accountRepo, operationRepo, commissionRateProvider, tInvestClient, ct);

        return Results.Ok(BuildSettingsDto(user, existing, defaultSignalOptions.Value, context));
    }

    /// <summary>Контекст комиссии для GET/PUT ответа (plan/22 часть D) — авто-оценка из журнала, тариф T-Invest, эффективная ставка+источник.</summary>
    private sealed record CommissionContext(
        CommissionAutoEstimateDto? AutoEstimate,
        string? TInvestTariff,
        decimal CommissionEffectiveRate,
        string CommissionEffectiveSource);

    private static async Task<CommissionContext> BuildCommissionContextAsync(
        ClaimsPrincipal principal,
        IAccountRepository accountRepo,
        IOperationRepository operationRepo,
        ICommissionRateProvider commissionRateProvider,
        ITInvestPortfolioClient tInvestClient,
        CancellationToken ct)
    {
        var accountId = await PositionsEndpoints.ResolveAccountIdAsync(principal, accountRepo);

        // Тариф — best-effort, не должен ронять GET/PUT /api/settings (см. doc-comment GetUserTariffAsync).
        string? tariff = null;
        try
        {
            var tariffInfo = await tInvestClient.GetUserTariffAsync(ct);
            tariff = tariffInfo?.Tariff;
        }
        catch (Exception)
        {
            // Проглатываем — тариф это необязательный контекст для UI (plan/22 часть B).
        }

        if (accountId is null)
        {
            return new CommissionContext(null, tariff, SwitchAnalysisService.DefaultCommissionRate, nameof(CommissionRateSource.Default));
        }

        var operations = (await operationRepo.GetByAccountIdAsync(accountId.Value)).ToList();
        var asOf = Bonds.Core.Time.BusinessClock.MoscowToday().ToDateTime(TimeOnly.MinValue);
        var estimate = CommissionRateEstimator.Estimate(operations, asOf);

        var resolved = await commissionRateProvider.GetAsync(accountId.Value, ct);

        var autoEstimateDto = estimate is null
            ? null
            : new CommissionAutoEstimateDto
            {
                Rate = estimate.Rate,
                TurnoverRub = estimate.TurnoverRub,
                FeeTotalRub = estimate.FeeTotalRub,
                TradeCount = estimate.TradeCount,
                WindowMonths = estimate.WindowMonths,
            };

        return new CommissionContext(autoEstimateDto, tariff, resolved.Rate, resolved.Source.ToString());
    }

    private static SettingsResponseDto BuildSettingsDto(
        User user, UserSettings? settings, SignalEngineOptions defaults, CommissionContext commission) => new()
    {
        BaseCurrency = user.BaseCurrency,
        TInvestTokenConfigured = !string.IsNullOrEmpty(settings?.TInvestTokenEncrypted),
        TInvestTokenMasked = MaskToken(settings?.TInvestTokenLast4),
        UpcomingEventDaysThreshold = settings?.UpcomingEventDaysThreshold ?? defaults.UpcomingEventDaysThreshold,
        UninvestedCashThresholdRub = settings?.UninvestedCashThresholdRub ?? defaults.UninvestedCashThresholdRub,
        UninvestedCashLookbackDays = settings?.UninvestedCashLookbackDays ?? defaults.UninvestedCashLookbackDays,
        YieldBelowAlternativeBpsThreshold = settings?.YieldBelowAlternativeBpsThreshold ?? defaults.YieldBelowAlternativeBpsThreshold,
        MaturityWindowDaysForAlternativeComparison = settings?.MaturityWindowDaysForAlternativeComparison ?? defaults.MaturityWindowDaysForAlternativeComparison,
        DefaultMaxConcentrationPercent = settings?.DefaultMaxConcentrationPercent ?? defaults.DefaultMaxConcentrationPercent,
        DurationDriftToleranceYears = settings?.DurationDriftToleranceYears ?? defaults.DurationDriftToleranceYears,
        CommissionRateOverride = settings?.CommissionRateOverride,
        CommissionAutoEstimate = commission.AutoEstimate,
        TInvestTariff = commission.TInvestTariff,
        CommissionEffectiveRate = commission.CommissionEffectiveRate,
        CommissionEffectiveSource = commission.CommissionEffectiveSource,
    };

    private static async Task<IResult> PutTInvestToken(
        TInvestTokenRequestDto request,
        ClaimsPrincipal principal,
        IUserSettingsRepository settingsRepo,
        IDataProtectionProvider dataProtection,
        ITInvestTokenValidator tokenValidator,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Token))
        {
            throw new ValidationException("Токен не должен быть пустым");
        }

        var userId = ResolveUserId(principal);
        var token = request.Token.Trim();

        // Пробный вызов T-Invest ДО шифрования/записи (plan/13 часть C) — невалидный токен не
        // должен попадать в БД молча.
        var validation = await tokenValidator.ValidateAsync(token, ct);
        if (!validation.IsValid)
        {
            return Results.Json(
                new { error = validation.ErrorMessage ?? "Токен не прошёл проверку T-Invest", type = "TInvestTokenValidationException" },
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var protector = dataProtection.CreateProtector(Bonds.Infrastructure.Services.TInvestTokenProvider.ProtectorPurpose);
        var encrypted = protector.Protect(token);
        var last4 = token.Length >= 4 ? token[^4..] : token;

        var existing = await settingsRepo.GetByUserIdAsync(userId) ?? new UserSettings { UserId = userId };
        existing.TInvestTokenEncrypted = encrypted;
        existing.TInvestTokenLast4 = last4;

        await settingsRepo.UpsertAsync(existing);

        // Намеренно НЕ возвращаем сам токен — только статус, маску и подтверждение счёта (spec §11,
        // plan/13 часть C: "маскированный идентификатор счёта (последние 4 символа) как подтверждение").
        var accountId = validation.AccountId ?? string.Empty;
        return Results.Ok(new TInvestTokenResponseDto
        {
            TInvestTokenConfigured = true,
            TInvestTokenMasked = MaskToken(last4),
            ValidatedAccountIdMasked = MaskToken(accountId.Length >= 4 ? accountId[^4..] : accountId),
        });
    }

    private static string? MaskToken(string? last4) =>
        string.IsNullOrEmpty(last4) ? null : $"••••{last4}";

    private static ulong ResolveUserId(ClaimsPrincipal principal)
    {
        var sub = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!ulong.TryParse(sub, out var userId))
            throw new ValidationException("Не удалось определить пользователя из токена авторизации");
        return userId;
    }
}

public sealed record SettingsResponseDto
{
    public required string BaseCurrency { get; init; }
    public required bool TInvestTokenConfigured { get; init; }
    public string? TInvestTokenMasked { get; init; }
    public required int UpcomingEventDaysThreshold { get; init; }
    public required decimal UninvestedCashThresholdRub { get; init; }
    public required int UninvestedCashLookbackDays { get; init; }
    public required int YieldBelowAlternativeBpsThreshold { get; init; }
    public required int MaturityWindowDaysForAlternativeComparison { get; init; }
    public required decimal DefaultMaxConcentrationPercent { get; init; }
    public required decimal DurationDriftToleranceYears { get; init; }

    /// <summary>Plan/22 часть D: ручной override ставки комиссии — ДОЛЯ (0.0005 = 0.05%). Null — не задан.</summary>
    public decimal? CommissionRateOverride { get; init; }

    /// <summary>Read-only: авто-оценка ставки из журнала операций (часть A). Null — журнал не позволяет оценить (нет сделок/оборот 0).</summary>
    public CommissionAutoEstimateDto? CommissionAutoEstimate { get; init; }

    /// <summary>Read-only: имя тарифа T-Invest (часть B, GetInfo) — только контекст, в расчёт не входит. Null — не удалось получить (нет токена/ошибка gRPC).</summary>
    public string? TInvestTariff { get; init; }

    /// <summary>Read-only: ставка, которая реально будет применена расчётами (резолвер части C) — ДОЛЯ.</summary>
    public required decimal CommissionEffectiveRate { get; init; }

    /// <summary>Read-only: источник CommissionEffectiveRate — строковое имя <see cref="Bonds.Core.Interfaces.CommissionRateSource"/> (UserOverride/EstimatedFromTrades/Default).</summary>
    public required string CommissionEffectiveSource { get; init; }
}

/// <summary>Read-only контекст авто-оценки ставки комиссии из журнала (plan/22 часть A/D) — см. <see cref="Bonds.Core.Analytics.CommissionEstimate"/>.</summary>
public sealed record CommissionAutoEstimateDto
{
    /// <summary>Оценённая ставка — ДОЛЯ (0.00046 = 0.046%), не процент.</summary>
    public required decimal Rate { get; init; }
    public required decimal TurnoverRub { get; init; }
    public required decimal FeeTotalRub { get; init; }
    public required int TradeCount { get; init; }
    public required int WindowMonths { get; init; }
}

public sealed record SettingsUpdateRequestDto
{
    public string? BaseCurrency { get; init; }
    public int? UpcomingEventDaysThreshold { get; init; }
    public decimal? UninvestedCashThresholdRub { get; init; }
    public int? UninvestedCashLookbackDays { get; init; }
    public int? YieldBelowAlternativeBpsThreshold { get; init; }
    public int? MaturityWindowDaysForAlternativeComparison { get; init; }
    public decimal? DefaultMaxConcentrationPercent { get; init; }
    public decimal? DurationDriftToleranceYears { get; init; }

    /// <summary>Plan/22 часть D: override ставки комиссии — ДОЛЯ (не процент; конвертация из % на фронте). Null — убрать override. Валидация: 0 &lt; x &lt; 0.05, иначе 422.</summary>
    public decimal? CommissionRateOverride { get; init; }
}

public sealed record TInvestTokenRequestDto
{
    public required string Token { get; init; }
}

public sealed record TInvestTokenResponseDto
{
    public required bool TInvestTokenConfigured { get; init; }
    public string? TInvestTokenMasked { get; init; }

    /// <summary>
    /// Plan/13 часть C: маскированный (последние 4 символа) Id брокерского счёта, к которому
    /// привязан только что провалидированный токен — подтверждение для пользователя, что
    /// проверка реально прошла, без раскрытия полного Id счёта.
    /// </summary>
    public string? ValidatedAccountIdMasked { get; init; }
}
