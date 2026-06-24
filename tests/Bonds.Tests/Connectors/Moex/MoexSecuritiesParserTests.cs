using Bonds.Infrastructure.Connectors.Moex;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Connectors.Moex;

public class MoexSecuritiesParserTests
{
    private static string ReadFixture(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Moex", fileName));

    [Fact]
    public void FixedCouponOfz_ParsesFaceValueMaturityAndCouponPercent()
    {
        var json = ReadFixture("securities_fixed_ofz_26238.json");

        var info = MoexSecuritiesParser.Parse(json);

        info.Should().NotBeNull();
        info!.Secid.Should().Be("SU26238RMFS4");
        info.Isin.Should().Be("RU000A1038V6");
        info.FaceValue.Should().Be(1000);
        info.MatDate.Should().Be(new DateOnly(2041, 5, 15));
        info.CouponPercent.Should().NotBeNull();
        info.LooksLikeFloater.Should().BeFalse();
        info.HasAmortizationHint.Should().BeFalse();
    }

    [Fact]
    public void Floater_MultipleBoards_PicksPreferredBoard_AndDetectsFloaterHint()
    {
        var json = ReadFixture("securities_floater_ofz_29025.json");

        var info = MoexSecuritiesParser.Parse(json);

        info.Should().NotBeNull();
        // Фикстура содержит две строки (SPOB и TQOB) для одного SECID — должен выбраться TQOB
        // (основной режим торгов гособлигациями), а не первая строка по порядку в ответе.
        info!.BoardId.Should().Be("TQOB");
        info.LooksLikeFloater.Should().BeTrue("BONDTYPE='Флоатер' должен распознаваться как подсказка плавающего купона");
    }

    [Fact]
    public void AmortizingBond_DetectsAmortizationHint()
    {
        var json = ReadFixture("securities_amortizing_offer_gtlk_1p16.json");

        var info = MoexSecuritiesParser.Parse(json);

        info.Should().NotBeNull();
        info!.HasAmortizationHint.Should().BeTrue();
        info.FaceValue.Should().Be(250, "после частичной амортизации текущий номинал в securities.json уменьшается относительно первоначального");
    }

    [Fact]
    public void EmptyData_ReturnsNull()
    {
        const string json = "{\"securities\": {\"columns\": [\"SECID\"], \"data\": []}}";

        var info = MoexSecuritiesParser.Parse(json);

        info.Should().BeNull();
    }

    [Fact]
    public void ResolveSecidByIsin_FindsBondInSearchResults()
    {
        var json = ReadFixture("securities_search_by_isin.json");

        var secid = MoexSecuritiesParser.ParseSecidFromSearch(json, "RU000A1038V6");

        secid.Should().Be("SU26238RMFS4");
    }

    [Fact]
    public void ResolveSecidByIsin_NoMatch_ReturnsNull()
    {
        var json = ReadFixture("securities_search_by_isin.json");

        var secid = MoexSecuritiesParser.ParseSecidFromSearch(json, "RU000UNKNOWN0");

        secid.Should().BeNull();
    }
}
