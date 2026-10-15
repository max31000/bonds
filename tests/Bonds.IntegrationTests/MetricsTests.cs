using System.Net;
using Bonds.IntegrationTests.Infrastructure;
using FluentAssertions;
using Prometheus;
using Xunit;

namespace Bonds.IntegrationTests;

/// <summary>
/// Prometheus app metrics (контракт mvv42-infra-mon docs/specs/2026-10-16-app-metrics.md): /metrics отдаёт
/// только отдельный KestrelMetricServer (9091), основной порт — нет; HTTP-метрики размечены шаблоном
/// маршрута, а не сырым путём с id.
/// </summary>
[Collection("NoDatabase")]
public class MetricsTests
{
    private readonly NoDatabaseWebApplicationFactory _factory;

    public MetricsTests(NoDatabaseWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task MainApp_DoesNotServeMetrics()
    {
        // Владелец с валидной cookie проходит FallbackPolicy — значит 404 означает «маршрута нет», а не «нет доступа».
        var client = Mvv42TestAuth.Authorize(_factory.CreateClient(), NoDatabaseWebApplicationFactory.OwnerTelegramId);

        var response = await client.GetAsync("/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("http_requests_received_total");
    }

    [Fact]
    public async Task HttpMetrics_UseRouteTemplate_NotConcretePath()
    {
        const string concreteId = "987654321";
        var client = _factory.CreateClient();

        // Без cookie — 401, но маршрут уже сопоставлен (UseRouting до UseHttpMetrics), БД не нужна.
        await client.GetAsync($"/api/positions/{concreteId}");

        using var stream = new MemoryStream();
        await Metrics.DefaultRegistry.CollectAndExportAsTextAsync(stream);
        var text = System.Text.Encoding.UTF8.GetString(stream.ToArray());

        var lines = text.Split('\n').Where(l => l.StartsWith("http_requests_received_total{")).ToList();
        lines.Should().Contain(l => l.Contains("endpoint=\"/api/positions/{id}\"") && l.Contains("code=\"4xx\""));
        text.Should().NotContain(concreteId);
    }
}
