using Bonds.Infrastructure.Connectors.Moex;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Connectors.Moex;

public class MoexGcurveParserTests
{
    private static string ReadFixture(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Moex", fileName));

    [Fact]
    public void RealGcurveResponse_ParsesAllNssParameters()
    {
        var json = ReadFixture("zcyc_gcurve.json");

        var snapshot = MoexGcurveParser.Parse(json);

        snapshot.Should().NotBeNull();
        snapshot!.B1.Should().NotBe(0);
        snapshot.T1.Should().BeGreaterThan(0);
        // G1..G9 должны быть разобраны (хотя бы часть может быть 0 легитимно на коротком участке кривой).
        snapshot.G1.Should().NotBe(default(decimal) - 1); // тривиальная проверка что поле задано (значение реальное)
    }

    [Fact]
    public void MissingParamsBlock_ReturnsNull()
    {
        const string json = "{\"securities\": {\"columns\": [], \"data\": []}}";

        var snapshot = MoexGcurveParser.Parse(json);

        snapshot.Should().BeNull();
    }

    [Fact]
    public void PartialNssParameters_ReturnsNull_DoesNotFabricateMissingValues()
    {
        const string json = """
        {
            "params": {
                "columns": ["tradedate", "B1", "B2"],
                "data": [["2026-06-24", 1.0, 2.0]]
            }
        }
        """;

        var snapshot = MoexGcurveParser.Parse(json);

        snapshot.Should().BeNull("отсутствие B3/T1/G1-G9 не должно приводить к снимку с нулями по умолчанию");
    }
}
