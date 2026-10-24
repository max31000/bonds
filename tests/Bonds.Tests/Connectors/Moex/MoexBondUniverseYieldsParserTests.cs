using Bonds.Infrastructure.Connectors.Moex;
using Bonds.Infrastructure.Universe;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Connectors.Moex;

/// <summary>
/// Задача 42 часть A: разбор блока <c>marketdata_yields</c> и новых полей <c>securities</c>
/// (ACCRUEDINT, ISSUESIZE) на РЕАЛЬНОМ снимке живого MOEX ISS 2026-10-15
/// (<c>Fixtures/Moex/bond_universe_with_yields_2026-10-15.json</c> — 19 бумаг: ОФЗ с несколькими
/// board'ами, корпоративные с датой доходности к оферте/MBS, бумаги БЕЗ строки marketdata_yields).
/// Единицы сырых полей — как у биржи (проценты/б.п./рубли), конвертация в доли — в маппере.
/// </summary>
public class MoexBondUniverseYieldsParserTests
{
    private static string LoadFixture() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Moex", "bond_universe_with_yields_2026-10-15.json"));

    private static MoexBondMarketRow Row(string secid, string boardId)
    {
        var rows = MoexBondUniverseParser.ParsePage(LoadFixture());
        return rows.Single(r => r.Secid == secid && r.BoardId == boardId);
    }

    [Fact]
    public void ParsePage_Ofz26238_PicksUpOfficialYieldsAndSpreadsRawUnits()
    {
        var row = Row("SU26238RMFS4", "TQOB");

        row.YieldDateType.Should().Be("MATDATE");
        row.EffectiveYieldPercent.Should().Be(16.228m, "EFFECTIVEYIELD приходит в ПРОЦЕНТАХ — сырое значение без конверсии");
        row.GspreadBp.Should().Be(-25m, "GSPREADBP в базисных пунктах, сырое значение");
        row.ZspreadBp.Should().Be(-8m, "ZSPREADBP в базисных пунктах, сырое значение");
        row.YieldToOfferPercent.Should().BeNull("у ОФЗ без оферты YIELDTOOFFER пуст");
    }

    [Fact]
    public void ParsePage_Ofz26238_ParsesAccruedAndIssueSize()
    {
        var row = Row("SU26238RMFS4", "TQOB");

        // Номинал 1000, купон ~35 ₽ — НКД в РУБЛЯХ на одну бумагу (26.26), а не в % и не на лот.
        row.FaceValue.Should().Be(1000m);
        row.AccruedRub.Should().Be(26.26m);
        row.IssueSize.Should().Be(1_000_000_000L);
    }

    [Fact]
    public void ParsePage_BoardWithoutYieldsRow_HasNullYieldFields()
    {
        // У SU26245RMFS9 три board'а: PACT/TQOB есть в marketdata_yields, SPOB — нет.
        var spob = Row("SU26245RMFS9", "SPOB");

        spob.EffectiveYieldPercent.Should().BeNull();
        spob.GspreadBp.Should().BeNull();
        spob.ZspreadBp.Should().BeNull();
        spob.YieldDateType.Should().BeNull();
        spob.AccruedRub.Should().NotBeNull("ACCRUEDINT берётся из securities и не зависит от marketdata_yields");
    }

    [Fact]
    public void ParsePage_BondWithoutAnyYieldsRow_StillParsesSecuritiesFields()
    {
        var row = Row("RU000A0JS4J1", "TQCB");

        row.GspreadBp.Should().BeNull();
        row.EffectiveYieldPercent.Should().BeNull();
        row.AccruedRub.Should().BeGreaterThan(0m);
    }

    [Fact]
    public void ParsePage_YieldToOfferBond_ParsesOfferTypeAndYield()
    {
        var offer = MoexBondUniverseParser.ParsePage(LoadFixture())
            .Where(r => r.YieldDateType == "OFFER" && r.YieldToOfferPercent.HasValue)
            .ToList();

        offer.Should().NotBeEmpty("в снимке есть бумаги с доходностью к оферте");
        offer.Should().OnlyContain(r => r.YieldToOfferPercent > 0m);
    }

    [Fact]
    public void ParsePage_MbsYieldDateType_IsPreservedAsIs()
    {
        MoexBondUniverseParser.ParsePage(LoadFixture())
            .Should().Contain(r => r.YieldDateType == "MBS");
    }

    [Fact]
    public void DeduplicateByBoard_JoinsYieldsOfWinningBoard_ByTurnover()
    {
        // TQOB (оборот > 0) побеждает PACT/SPOB; официальные доходности берутся именно с ЕГО строки.
        var deduped = MoexBondUniverseParser.DeduplicateByBoard(MoexBondUniverseParser.ParsePage(LoadFixture()));

        deduped.Select(r => r.Secid).Should().OnlyHaveUniqueItems();
        var ofz = deduped.Single(r => r.Secid == "SU26245RMFS9");
        ofz.BoardId.Should().Be("TQOB");
        ofz.GspreadBp.Should().NotBeNull();
    }

    [Fact]
    public void Mapper_ConvertsUnits_PercentAndBpToFractions_AccruedStaysRubles()
    {
        var row = Row("SU26238RMFS4", "TQOB");

        var entry = BondUniverseEntryMapper.Map(row, curve: null);

        entry.EffectiveYieldFraction.Should().Be(0.16228m, "16.228 % ÷ 100 = доля");
        entry.GspreadOfficialFraction.Should().Be(-0.0025m, "-25 б.п. ÷ 10000 = доля");
        entry.ZspreadOfficialFraction.Should().Be(-0.0008m, "-8 б.п. ÷ 10000 = доля");
        entry.YieldDateType.Should().Be("MATDATE");
        entry.AccruedRub.Should().Be(26.26m, "НКД в рублях на одну бумагу — не конвертируется");
        entry.IssueSize.Should().Be(1_000_000_000L);
    }

    [Fact]
    public void Mapper_OfferBond_YieldToOfferConvertedFromPercent()
    {
        var raw = MoexBondUniverseParser.ParsePage(LoadFixture())
            .First(r => r.YieldDateType == "OFFER" && r.YieldToOfferPercent.HasValue);

        var entry = BondUniverseEntryMapper.Map(raw, curve: null);

        entry.YieldToOfferFraction.Should().Be(raw.YieldToOfferPercent!.Value / 100m);
        entry.YieldDateType.Should().Be("OFFER");
    }

    [Fact]
    public void Mapper_NoYieldsRow_AllOfficialFieldsNull()
    {
        var entry = BondUniverseEntryMapper.Map(Row("RU000A0JS4J1", "TQCB"), curve: null);

        entry.GspreadOfficialFraction.Should().BeNull();
        entry.ZspreadOfficialFraction.Should().BeNull();
        entry.EffectiveYieldFraction.Should().BeNull();
        entry.YieldToOfferFraction.Should().BeNull();
        entry.YieldDateType.Should().BeNull();
    }

    [Fact]
    public void Mapper_AbsurdMoexYields_BecomeNull_InsteadOfOverflowingDecimalColumns()
    {
        // Прод 2026-10-24: дефолтный выпуск с доходностью ~10^6 % ронял весь почасовой upsert банка
        // (DECIMAL(12,8) / DECIMAL(10,6) — максимум 9999.99…). Неправдоподобная доля → null, остальные поля живы.
        var row = new MoexBondMarketRow
        {
            Secid = "RU000TEST001", BoardId = "TQCB",
            YieldPercent = 2_500_000m, EffectiveYieldPercent = 1_234_567m, YieldToOfferPercent = -999_999m,
            GspreadBp = 99_999_999m, ZspreadBp = 120m, PricePercent = 5m,
        };

        var entry = BondUniverseEntryMapper.Map(row, curve: null);

        entry.EffectiveYieldFraction.Should().BeNull();
        entry.YieldToOfferFraction.Should().BeNull();
        entry.GspreadOfficialFraction.Should().BeNull();
        entry.ZspreadOfficialFraction.Should().Be(0.012m, "правдоподобные значения не трогаем");
    }

    [Theory]
    [InlineData(100_000, 1000)]   // ровно предел (100 000 % = доля 1000) — сохраняется
    [InlineData(45.5, 0.455)]
    public void Mapper_PlausibleYield_KeptAsFraction(double percent, double expectedFraction)
    {
        var row = new MoexBondMarketRow { Secid = "RU000TEST002", BoardId = "TQCB", EffectiveYieldPercent = (decimal)percent };
        BondUniverseEntryMapper.Map(row, curve: null).EffectiveYieldFraction.Should().Be((decimal)expectedFraction);
    }
}
