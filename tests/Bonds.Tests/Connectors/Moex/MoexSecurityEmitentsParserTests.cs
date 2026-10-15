using Bonds.Infrastructure.Connectors.Moex;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Connectors.Moex;

/// <summary>
/// Задача 43 часть A: разбор страницы bulk-справочника ISS
/// <c>/iss/securities.json?engine=stock&amp;market=bonds&amp;iss.only=securities&amp;securities.columns=secid,emitent_id,emitent_title,emitent_inn</c>
/// на РЕАЛЬНОМ срезе живого MOEX ISS 2026-10-15 (100 строк: Gaz Capital с ИНН=null, российские
/// эмитенты с полным набором, старые выпуски RU000... вообще без эмитента).
/// </summary>
public class MoexSecurityEmitentsParserTests
{
    private static string LoadFixture() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Moex", "securities_emitents_page_2026-10-15.json"));

    [Fact]
    public void ParsePage_RealFixture_ReadsAllRowsByColumnName()
    {
        var rows = MoexSecurityEmitentsParser.ParsePage(LoadFixture());

        rows.Should().HaveCount(100);
        var selectel = rows.Single(r => r.Secid == "RU000A10GCU9");
        selectel.EmitentId.Should().Be(11355);
        selectel.EmitentTitle.Should().Be("Акционерное общество \"Селектел\"");
        selectel.EmitentInn.Should().Be("7810962785");
    }

    [Fact]
    public void ParsePage_RowsWithoutEmitent_HaveNullFieldsNotException()
    {
        var rows = MoexSecurityEmitentsParser.ParsePage(LoadFixture());

        var old = rows.Single(r => r.Secid == "RU0001245474");
        old.EmitentId.Should().BeNull();
        old.EmitentTitle.Should().BeNull();
        old.EmitentInn.Should().BeNull();
    }

    [Fact]
    public void ParsePage_EmitentWithoutInn_KeepsIdAndTitle()
    {
        var rows = MoexSecurityEmitentsParser.ParsePage(LoadFixture());

        var gaz = rows.Single(r => r.Secid == "XS0191754729");
        gaz.EmitentId.Should().Be(7202);
        gaz.EmitentTitle.Should().Be("Gaz Capital S.A.");
        gaz.EmitentInn.Should().BeNull("иностранный эмитент — ИНН отсутствует");
    }

    [Fact]
    public void ParsePage_NoSecuritiesBlockOrEmpty_ReturnsEmptyList()
    {
        MoexSecurityEmitentsParser.ParsePage("{}").Should().BeEmpty();
        MoexSecurityEmitentsParser.ParsePage("{\"securities\":{\"columns\":[\"secid\"],\"data\":[]}}").Should().BeEmpty();
    }

    [Fact]
    public void ParsePage_ColumnOrderDiffers_StillMapsByName()
    {
        const string json = "{\"securities\":{\"columns\":[\"emitent_inn\",\"secid\",\"emitent_title\",\"emitent_id\"],"
                          + "\"data\":[[\"7707083893\",\"RU000A0JX0J2\",\"ПАО Сбербанк\",1222]]}}";

        var row = MoexSecurityEmitentsParser.ParsePage(json).Single();

        row.Secid.Should().Be("RU000A0JX0J2");
        row.EmitentId.Should().Be(1222);
        row.EmitentInn.Should().Be("7707083893");
        row.EmitentTitle.Should().Be("ПАО Сбербанк");
    }
}
