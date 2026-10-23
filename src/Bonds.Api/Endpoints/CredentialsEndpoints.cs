using System.Text.Json;
using Bonds.Api.Auth;
using Bonds.Api.Middleware;
using Bonds.Core.Credentials;
using Bonds.Core.Interfaces.Repositories;

namespace Bonds.Api.Endpoints;

/// <summary>
/// «Подключения» (задача 58): секреты пользователя (токены брокеров и ИИ-провайдеров) в <c>user_credentials</c>.
/// <list type="bullet">
/// <item><c>GET /api/credentials</c> — строка на КАЖДОГО провайдера реестра (незаданные — <c>status: "missing"</c>).</item>
/// <item><c>PUT /api/credentials/{provider}</c> — валидация (<see cref="ICredentialValidator"/>) ДО сохранения.</item>
/// <item><c>DELETE /api/credentials/{provider}</c> — 204.</item>
/// <item><c>GET /api/capabilities</c> — какие части функционала включены (флаг true только при status ok).</item>
/// </list>
/// <para>
/// <b>Секреты в ответах отсутствуют никогда</b> — ни в GET, ни в PUT/ошибках (только открытая маска last4).
/// Всё по <c>userId</c> из <see cref="ICurrentUser"/>. Новый провайдер: запись в <see cref="CredentialProviders"/> + валидатор.
/// </para>
/// </summary>
public static class CredentialsEndpoints
{
    public static void MapCredentialsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/credentials", GetCredentials);
        app.MapPut("/api/credentials/{provider}", PutCredential);
        app.MapDelete("/api/credentials/{provider}", DeleteCredential);
        app.MapGet("/api/capabilities", GetCapabilities);
    }

    private static IResult ValidationFailure(string error) =>
        Results.Json(new { error, type = "ValidationException" }, statusCode: StatusCodes.Status422UnprocessableEntity);

    private static async Task<ulong> ResolveUserIdAsync(ICurrentUser currentUser) =>
        await currentUser.GetUserIdAsync()
        ?? throw new ValidationException("Не удалось определить пользователя из токена авторизации");

    private static async Task<IResult> GetCredentials(ICurrentUser currentUser, IUserCredentialStore store)
    {
        var userId = await ResolveUserIdAsync(currentUser);
        var statuses = (await store.ListStatusesAsync(userId)).ToDictionary(s => s.Provider);
        return Results.Ok(CredentialProviders.All
            .Select(p => ToDto(p, statuses.GetValueOrDefault(p.Id)))
            .ToList());
    }

    private static async Task<IResult> PutCredential(
        string provider,
        CredentialPutRequestDto request,
        ICurrentUser currentUser,
        IUserCredentialStore store,
        IAccountRepository accountRepo,
        IEnumerable<ICredentialValidator> validators,
        CancellationToken ct)
    {
        var descriptor = CredentialProviders.Find(provider)
            ?? throw new NotFoundException($"Неизвестный провайдер: {provider}");
        var userId = await ResolveUserIdAsync(currentUser);

        // Берём только поля из описания провайдера, значения тримим; все поля обязательны.
        var fields = new Dictionary<string, string>();
        foreach (var field in descriptor.Fields)
        {
            if (request.Fields is null
                || !request.Fields.TryGetValue(field.Name, out var value)
                || string.IsNullOrWhiteSpace(value))
            {
                return ValidationFailure($"Поле «{field.Title}» обязательно");
            }

            fields[field.Name] = value.Trim();
        }

        var validator = validators.FirstOrDefault(v => v.ProviderId == descriptor.Id)
            ?? throw new InvalidOperationException($"Для провайдера {descriptor.Id} не зарегистрирован валидатор");

        var validation = await validator.ValidateAsync(fields, ct);
        if (!validation.Ok)
        {
            // Ничего не сохраняем; сообщение валидатора не содержит секрет (контракт ICredentialValidator).
            return ValidationFailure(validation.Message ?? "Секрет не прошёл проверку");
        }

        var previous = descriptor.Id == CredentialProviders.TInvestRead
            ? await store.GetSecretAsync(userId, descriptor.Id)
            : null;

        var last4 = validation.Last4 ?? descriptor.ComputeLast4(fields);
        await store.SaveAsync(userId, descriptor.Id, fields, last4, validation.MetaJson);

        if (descriptor.Id == CredentialProviders.TInvestRead)
        {
            var tokenChanged = previous is null || previous.GetValueOrDefault("token") != fields["token"];
            await TInvestAccountBinding.RebindIfTokenChangedAsync(currentUser, accountRepo, validation.BrokerAccountId, tokenChanged);
        }

        var saved = (await store.ListStatusesAsync(userId)).FirstOrDefault(s => s.Provider == descriptor.Id);
        return Results.Ok(ToDto(descriptor, saved));
    }

    private static async Task<IResult> DeleteCredential(string provider, ICurrentUser currentUser, IUserCredentialStore store)
    {
        var descriptor = CredentialProviders.Find(provider)
            ?? throw new NotFoundException($"Неизвестный провайдер: {provider}");
        var userId = await ResolveUserIdAsync(currentUser);
        await store.DeleteAsync(userId, descriptor.Id);
        return Results.NoContent();
    }

    private static async Task<IResult> GetCapabilities(ICurrentUser currentUser, IUserCredentialStore store)
    {
        var userId = await ResolveUserIdAsync(currentUser);
        var okProviders = (await store.ListStatusesAsync(userId))
            .Where(s => s.Status == CredentialStatus.Ok)
            .Select(s => s.Provider)
            .ToHashSet();

        // Ключ = провайдер реестра (snake_case -> camelCase), плюс aiAgents = любой провайдер категории Ai со статусом ok.
        // Известные ключи присутствуют всегда (freedom — false, пока задача 59 не добавила провайдера).
        var caps = new Dictionary<string, bool>
        {
            ["tinvestRead"] = false,
            ["tinvestTrade"] = false,
            ["freedom"] = false,
            ["aiAgents"] = false,
        };
        foreach (var p in CredentialProviders.All)
        {
            caps[ToCamelCase(p.Id)] = okProviders.Contains(p.Id);
        }

        caps["aiAgents"] = CredentialProviders.All.Any(p => p.Category == CredentialCategory.Ai && okProviders.Contains(p.Id));
        return Results.Ok(caps);
    }

    /// <summary>"tinvest_read" → "tinvestRead".</summary>
    internal static string ToCamelCase(string snake)
    {
        var parts = snake.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return parts[0] + string.Concat(parts.Skip(1).Select(x => char.ToUpperInvariant(x[0]) + x[1..]));
    }

    private static CredentialDto ToDto(CredentialProviderDescriptor p, CredentialStatusInfo? s) => new()
    {
        Provider = p.Id,
        Title = p.Title,
        Category = p.Category,
        Fields = p.Fields.Select(f => new CredentialFieldDto { Name = f.Name, Title = f.Title, IsSecret = f.IsSecret }).ToList(),
        Unlocks = p.Unlocks,
        Experimental = p.Experimental,
        Status = s?.Status ?? CredentialStatus.Missing,
        StatusMessage = s?.StatusMessage,
        Last4 = s?.Last4,
        VerifiedAt = s?.VerifiedAtUtc,
        Meta = string.IsNullOrEmpty(s?.MetaJson) ? null : JsonSerializer.Deserialize<JsonElement>(s.MetaJson),
    };
}

/// <summary>Привязка локального счёта к брокерскому счёту, подтверждённому валидацией токена T-Invest (общая для legacy и нового PUT).</summary>
internal static class TInvestAccountBinding
{
    /// <summary>
    /// Новый токен может смотреть на другой счёт — перепривязываем локальный счёт к подтверждённому валидацией (синк берёт
    /// брокерский счёт отсюда, см. BondSyncService). Тот же токен — привязку не трогаем: валидация берёт «первый открытый счёт»,
    /// а владелец мог быть привязан ко второму, и сверка позиций стёрла бы его портфель.
    /// </summary>
    public static async Task RebindIfTokenChangedAsync(
        ICurrentUser currentUser, IAccountRepository accountRepo, string? validatedBrokerAccountId, bool tokenChanged)
    {
        var localAccountId = await PositionsEndpoints.ResolveAccountIdAsync(currentUser, accountRepo);
        if (tokenChanged && localAccountId is > 0 && await accountRepo.GetByIdAsync(localAccountId.Value) is { } account)
        {
            account.BrokerAccountId = string.IsNullOrEmpty(validatedBrokerAccountId) ? null : validatedBrokerAccountId;
            await accountRepo.UpdateAsync(account);
        }
    }
}

/// <summary>Тело <c>PUT /api/credentials/{provider}</c>: значения полей секрета по именам из описания провайдера.</summary>
public sealed record CredentialPutRequestDto
{
    public Dictionary<string, string>? Fields { get; init; }
}

public sealed record CredentialFieldDto
{
    public required string Name { get; init; }
    public required string Title { get; init; }
    public required bool IsSecret { get; init; }
}

/// <summary>Строка «Подключений». Секрета здесь нет НИКОГДА — только открытая маска <see cref="Last4"/>.</summary>
public sealed record CredentialDto
{
    public required string Provider { get; init; }
    public required string Title { get; init; }

    /// <summary>"broker" | "ai" (camelCase-имя <see cref="CredentialCategory"/>).</summary>
    public required CredentialCategory Category { get; init; }
    public required IReadOnlyList<CredentialFieldDto> Fields { get; init; }
    public required string Unlocks { get; init; }
    public required bool Experimental { get; init; }

    /// <summary>"ok" | "invalid" | "unverified" | "missing" (missing — записи нет).</summary>
    public required string Status { get; init; }
    public string? StatusMessage { get; init; }

    /// <summary>Последние 4 символа главного поля — только для маски в UI.</summary>
    public string? Last4 { get; init; }

    /// <summary>Момент последней успешной проверки, UTC.</summary>
    public DateTime? VerifiedAt { get; init; }

    /// <summary>Несекретные данные валидатора (например, маска счёта).</summary>
    public JsonElement? Meta { get; init; }
}
