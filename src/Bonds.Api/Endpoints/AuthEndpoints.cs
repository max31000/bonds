using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

namespace Bonds.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/api/auth/telegram", TelegramLogin).AllowAnonymous();
        app.MapGet("/api/auth/me", Me).RequireAuthorization();
    }

    /// <summary>
    /// Принимает данные от Telegram Login Widget, проверяет подпись,
    /// допускает только владельца (Telegram:OwnerId), создаёт/обновляет его
    /// запись в users и возвращает JWT.
    /// </summary>
    private static async Task<IResult> TelegramLogin(
        TelegramAuthData data,
        ITelegramAuthService telegramAuth,
        IUserRepository userRepo,
        IConfiguration config)
    {
        // 1. Проверяем подпись Telegram
        if (!telegramAuth.ValidateAuthData(data))
        {
            return Results.Json(new { error = "Недействительные данные авторизации Telegram" }, statusCode: 401);
        }

        // 2. Allowlist: пускаем только владельца (продукт single-user, см. spec §2)
        var ownerIdRaw = config["Telegram:OwnerId"];
        if (!long.TryParse(ownerIdRaw, out var ownerId) || data.Id != ownerId)
        {
            return Results.Json(new { error = "Доступ запрещён" }, statusCode: 403);
        }

        // 3. Находим или создаём пользователя-владельца
        var user = await userRepo.GetByTelegramIdAsync(data.Id);
        if (user is null)
        {
            user = new User
            {
                TelegramId = data.Id,
                Username = data.Username,
                FirstName = data.FirstName,
                LastName = data.LastName,
            };
            var newId = await userRepo.CreateAsync(user);
            user.Id = newId;
        }
        else if (user.Username != data.Username || user.FirstName != data.FirstName || user.LastName != data.LastName)
        {
            user.Username = data.Username;
            user.FirstName = data.FirstName;
            user.LastName = data.LastName;
            await userRepo.UpdateAsync(user);
        }

        // 4. Выдаём JWT
        var token = GenerateJwt(user, config);

        return Results.Ok(new
        {
            token,
            user = ToDto(user),
        });
    }

    private static async Task<IResult> Me(ClaimsPrincipal principal, IUserRepository userRepo)
    {
        var sub = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!ulong.TryParse(sub, out var userId))
            return Results.Json(new { error = "Требуется авторизация" }, statusCode: 401);

        var user = await userRepo.GetByIdAsync(userId);
        if (user is null)
            return Results.Json(new { error = "Требуется авторизация" }, statusCode: 401);

        return Results.Ok(ToDto(user));
    }

    private static object ToDto(User user) => new
    {
        id = user.Id,
        telegramId = user.TelegramId,
        username = user.Username,
        firstName = user.FirstName,
        lastName = user.LastName,
    };

    private static string GenerateJwt(User user, IConfiguration config)
    {
        var secret = config["Jwt:Secret"] ?? throw new InvalidOperationException("Jwt:Secret не настроен");
        var issuer = config["Jwt:Issuer"] ?? "bonds";
        var audience = config["Jwt:Audience"] ?? "bonds";
        var expDays = int.TryParse(config["Jwt:ExpirationDays"], out var d) ? d : 30;

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim("telegram_id", user.TelegramId.ToString()),
        };

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: DateTime.UtcNow.AddDays(expDays),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
