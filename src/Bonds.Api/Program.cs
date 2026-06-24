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

// JSON: camelCase + string enums (контракт для фронта)
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
});

var app = builder.Build();

app.UseCors();

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

// Здесь будут app.MapXxxEndpoints() по мере появления доменных модулей (этапы 03+)

// Health check — без авторизации, не зависит от БД
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

// Нужен для WebApplicationFactory<Program> в интеграционных тестах
public partial class Program { }
