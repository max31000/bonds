using System.Text;
using Bonds.Api.Endpoints;
using Bonds.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// CORS — origins читаются из конфига, чтобы не хардкодить домен в коде
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        var origins = builder.Configuration
            .GetSection("Cors:AllowedOrigins")
            .Get<string[]>()
            ?? ["http://localhost:5173"];
        policy.WithOrigins(origins)
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Bonds API", Version = "v1" });
});

// Infrastructure DI (repos, services, migration runner) — наполняется по мере роста сервиса
builder.Services.AddInfrastructure(builder.Configuration);

// JWT-авторизация (этап 02). Bearer-токен валидируется стандартным AddJwtBearer
// (а не самописным middleware, как в cashpulse) — план 02 предпочитает этот способ.
// IConfigureOptions резолвится из DI лениво при первом запросе (а не сразу из builder.Configuration
// на старте), чтобы WebApplicationFactory.ConfigureWebHost в тестах успела подменить Jwt:Secret —
// тот же паттерн, что и GetConnStr в DependencyInjection.cs.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();

builder.Services.AddSingleton<IConfigureOptions<JwtBearerOptions>>(sp =>
    new ConfigureNamedOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
    {
        var config = sp.GetRequiredService<IConfiguration>();
        var secret = config["Jwt:Secret"] ?? throw new InvalidOperationException("Jwt:Secret не настроен");

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
            ValidateIssuer = true,
            ValidIssuer = config["Jwt:Issuer"] ?? "bonds",
            ValidateAudience = true,
            ValidAudience = config["Jwt:Audience"] ?? "bonds",
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    }));

// Fallback-политика: все эндпоинты требуют авторизации, если явно не помечены [AllowAnonymous]
// (см. /health и POST /api/auth/telegram). Это гарантирует, что доменные эндпоинты,
// добавленные в следующих этапах, не останутся незащищёнными по умолчанию.
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

// JSON: camelCase + string enums (контракт для фронта)
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
});

var app = builder.Build();

app.UseCors();

app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Bonds API v1");
    });
}

// Миграции на старте (пропускаются в Testing — там работает DatabaseFixture)
if (!app.Environment.IsEnvironment("Testing"))
{
    try
    {
        var migrationRunner = app.Services.GetRequiredService<MigrationRunner>();
        await migrationRunner.RunAsync();
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Failed to run database migrations");
        throw;
    }
}

// Auth (этап 02): POST /api/auth/telegram — публичный (AllowAnonymous внутри), GET /api/auth/me — защищён
app.MapAuthEndpoints();

// Здесь будут app.MapXxxEndpoints() по мере появления доменных модулей (этапы 03+)
// Внимание: FallbackPolicy требует авторизацию по умолчанию — новые публичные маршруты
// нужно явно помечать .AllowAnonymous().

// Health check — без авторизации, не зависит от БД
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

app.Run();

// Нужен для WebApplicationFactory<Program> в интеграционных тестах
public partial class Program { }
