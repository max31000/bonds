using System.Net;
using System.Text.Json;

namespace Bonds.Api.Middleware;

/// <summary>
/// Единый формат ошибок (plan/08 "Общие требования к контрактам": "Ошибки — единый формат,
/// порт ErrorHandlingMiddleware из cashpulse"). Намеренно НЕ перехватывает "неполные данные"
/// (отсутствующая котировка, несошедшийся YTM и т.п.) — those деградируют с флагами в самом
/// DTO и возвращаются как 200 (spec §4.4, plan/08: "Неполные данные — не 500, а 200 с пометкой").
/// Этот middleware ловит только программные ошибки запроса (не найдено/валидация) и
/// непредвиденные исключения.
/// </summary>
public class ErrorHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ErrorHandlingMiddleware> _logger;

    public ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (NotFoundException ex)
        {
            await WriteErrorResponse(context, HttpStatusCode.NotFound, ex.Message, nameof(NotFoundException));
        }
        catch (ValidationException ex)
        {
            await WriteErrorResponse(context, HttpStatusCode.BadRequest, ex.Message, nameof(ValidationException));
        }
        catch (Exception ex)
        {
            // Токен T-Invest и прочие секреты никогда не логируются (spec §11) — это обычный
            // ex.Message/тип исключения, не значения запроса.
            _logger.LogError(ex, "Unhandled exception");
            await WriteErrorResponse(context, HttpStatusCode.InternalServerError,
                "An internal server error occurred", ex.GetType().Name);
        }
    }

    private static async Task WriteErrorResponse(
        HttpContext context,
        HttpStatusCode statusCode,
        string message,
        string type)
    {
        context.Response.StatusCode = (int)statusCode;
        context.Response.ContentType = "application/json";

        var response = new { error = message, type };
        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

        await context.Response.WriteAsync(json);
    }
}

public class NotFoundException : Exception
{
    public NotFoundException(string message) : base(message) { }
}

public class ValidationException : Exception
{
    public ValidationException(string message) : base(message) { }
}
