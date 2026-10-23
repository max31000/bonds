using Bonds.Api.Auth;
using Bonds.Api.Endpoints;
using Bonds.Api.Middleware;
using Bonds.Infrastructure;
using Prometheus;

// Prometheus: процесс/рантайм .NET — да; EventCounters и адаптер .NET Meters — выключены, чтобы не
// плодить серии (контракт mvv42-infra-mon docs/specs/2026-10-16-app-metrics.md). Должно идти до
// создания первой метрики.
Metrics.SuppressDefaultMetrics(new SuppressDefaultMetricOptions
{
    SuppressEventCounters = true,
    SuppressMeters = true,
});

var builder = WebApplication.CreateBuilder(args);

// /metrics — только на отдельном KestrelMetricServer (в контейнере 9091, на хосте 127.0.0.1:9501);
// основной порт приложения (и nginx) метрики не отдаёт. Metrics:Port = 0 отключает сервер (тесты).
var metricsPort = builder.Configuration.GetValue("Metrics:Port", 9091);
if (metricsPort > 0)
{
    builder.Services.AddMetricServer(o => o.Port = (ushort)metricsPort);
}

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

// Явный UseRouting, чтобы UseHttpMetrics видел совпавший эндпоинт: метка endpoint = шаблон маршрута
// (/api/positions/{id}), никогда сырой путь; без совпадения — endpoint="".
app.UseRouting();
app.UseHttpMetrics(o => o.ReduceStatusCodeCardinality());

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
app.MapIncomeEndpoints();
app.MapFundsEndpoints();
app.MapCurrenciesEndpoints();
app.MapBrokerHoldingsEndpoints();
app.MapCashEndpoints();
app.MapBasketEndpoints();
app.MapPortfolioAnalyticsEndpoints();
app.MapReplacementEndpoints();
app.MapPositionVerdictEndpoints();
app.MapAllocationEndpoints();
app.MapSignalsEndpoints();
app.MapSyncEndpoints();
app.MapSettingsEndpoints();
app.MapCredentialsEndpoints();
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
