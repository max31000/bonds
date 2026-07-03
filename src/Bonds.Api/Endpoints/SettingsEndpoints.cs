using System.Security.Claims;
using Bonds.Api.Middleware;
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

    private static async Task<IResult> GetSettings(
        ClaimsPrincipal principal,
        IUserRepository userRepo,
        IUserSettingsRepository settingsRepo,
        IOptions<SignalEngineOptions> defaultSignalOptions)
    {
        var userId = ResolveUserId(principal);
        var user = await userRepo.GetByIdAsync(userId);
        if (user is null) throw new NotFoundException("Пользователь не найден");

        var settings = await settingsRepo.GetByUserIdAsync(userId);
        return Results.Ok(BuildSettingsDto(user, settings, defaultSignalOptions.Value));
    }

    private static async Task<IResult> PutSettings(
        SettingsUpdateRequestDto request,
        ClaimsPrincipal principal,
        IUserRepository userRepo,
        IUserSettingsRepository settingsRepo,
        IOptions<SignalEngineOptions> defaultSignalOptions)
    {
        var userId = ResolveUserId(principal);
        var user = await userRepo.GetByIdAsync(userId);
        if (user is null) throw new NotFoundException("Пользователь не найден");

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

        await settingsRepo.UpsertAsync(existing);

        return Results.Ok(BuildSettingsDto(user, existing, defaultSignalOptions.Value));
    }

    private static SettingsResponseDto BuildSettingsDto(User user, UserSettings? settings, SignalEngineOptions defaults) => new()
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
