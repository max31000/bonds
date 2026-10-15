using Bonds.Api.Auth;
using Bonds.Api.Endpoints;
using Bonds.Api.Middleware;
using Bonds.Infrastructure;

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

// Auth: только общий mvv42-auth (cookie JWT, ключи из JWKS). Fallback/Default policy = app policy
// `mvv42:app` + проверка владельца (tg == Telegram:OwnerId) — см. Auth/BondsAuth.cs. Новый эндпоинт
// защищён по умолчанию; публичные — только явным .AllowAnonymous() (сейчас лишь /health).
builder.Services.AddBondsAuth(builder.Configuration);

// JSON: camelCase + string enums (контракт для фронта)
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
});

var app = builder.Build();

// Единый формат ошибок — должен оборачивать весь остальной конвейер, иначе
// необработанные исключения дальше по цепочке не попадут в этот middleware.
app.UseMiddleware<ErrorHandlingMiddleware>();

// Swagger ДО UseAuthentication/UseAuthorization: FallbackPolicy
// применяется к ЛЮБОМУ эндпоинту маршрутизации, включая те, что UseSwagger/UseSwaggerUI
// регистрируют внутри себя через IEndpointRouteBuilder — если поставить блок после
// UseAuthorization(), swagger.json и Swagger UI сами требуют авторизацию и становятся
// недоступны (обнаружено эмпирически при smoke-проверке этого этапа: curl возвращал 401
// вместо JSON-схемы). Размещение здесь выводит их из-под действия middleware авторизации.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Bonds API v1");
    });
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

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

// Контракты для фронта — все защищены FallbackPolicy по умолчанию.
app.MapPositionsEndpoints();
app.MapCashFlowEndpoints();
app.MapXirrEndpoints();
app.MapPortfolioAnalyticsEndpoints();
app.MapReplacementEndpoints();
app.MapAllocationEndpoints();
app.MapSignalsEndpoints();
app.MapSyncEndpoints();
app.MapSettingsEndpoints();
app.MapLiveEndpoints();
app.MapWatchlistEndpoints();
app.MapUniverseEndpoints();
app.MapIssuerEndpoints();

// Внимание: FallbackPolicy требует авторизацию по умолчанию — новые публичные маршруты
// нужно явно помечать .AllowAnonymous().

// Health check — без авторизации, не зависит от БД
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

app.Run();

// Нужен для WebApplicationFactory<Program> в интеграционных тестах
public partial class Program { }
