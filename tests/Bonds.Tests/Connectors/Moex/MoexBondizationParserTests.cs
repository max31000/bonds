using Bonds.Core.Models;
using Bonds.Infrastructure.Connectors.Moex;
using FluentAssertions;
using Xunit;

namespace Bonds.Tests.Connectors.Moex;

/// <summary>
/// Тесты парсера bondization на сохранённых фикстурах реальных ответов MOEX ISS
/// (tests/Bonds.Tests/Fixtures/Moex) — без сетевых вызовов (plan/04 Часть A "Тесты").
/// Покрывает обязательные краевые случаи: фикс-купонная бумага, флоатер, амортизация+оферта,
/// неполные купоны (синтетическая фикстура, см. Fixtures/Moex/README.md).
/// </summary>
public class MoexBondizationParserTests
{
    private static string ReadFixture(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Moex", fileName));

    [Fact]
    public void FixedCouponOfz_ParsesAllCoupons_AllKnown_NotIncomplete()
    {
        var json = ReadFixture("bondization_fixed_ofz_26238.json");

        var result = MoexBondizationParser.Parse("SU26238RMFS4", json);

        result.Coupons.Should().NotBeEmpty();
        result.Coupons.Should().OnlyContain(c => c.IsKnown, "купоны ОФЗ-ПД с фиксированным купоном всегда известны заранее");
        result.Coupons.Should().OnlyContain(c => c.ValueRub > 0);
        result.DataIncomplete.Should().BeFalse();
        result.Amortizations.Should().ContainSingle(a => true, "ОФЗ-ПД без амортизации имеет только финальное погашение тела");
        result.Offers.Should().BeEmpty("ОФЗ-ПД 26238 не имеет оферт");
    }

    [Fact]
    public void FixedCouponOfz_CouponsAreOrderedByDate()
    {
        var json = ReadFixture("bondization_fixed_ofz_26238.json");
        var result = MoexBondizationParser.Parse("SU26238RMFS4", json);

        result.Coupons.Should().BeInAscendingOrder(c => c.CouponDate);
    }

    [Fact]
    public void Floater_PastCouponsKnown_FutureRateNotFabricated()
    {
        var json = ReadFixture("bondization_floater_ofz_29025.json");

        var result = MoexBondizationParser.Parse("SU29025RMFS2", json);

        result.Coupons.Should().NotBeEmpty();
        // Флоатер: исторические купоны (ставка уже зафиксирована и выплачена/известна) — known.
        result.Coupons.Where(c => c.CouponDate < DateOnly.FromDateTime(DateTime.UtcNow))
            .Should().OnlyContain(c => c.IsKnown);
        // Ни один купон не должен быть "выдуман" — там, где значения нет, ValueRub остаётся null,
        // а не 0 (spec §4.4 "не подставлять нули молча").
        result.Coupons.Where(c => !c.IsKnown).Should().OnlyContain(c => c.ValueRub == null);
    }

    [Fact]
    public void AmortizingBondWithOffer_ParsesAmortizationScheduleAndOffer()
    {
        var json = ReadFixture("bondization_amortizing_offer_gtlk_1p16.json");

        var result = MoexBondizationParser.Parse("RU000A101GD3", json);

        result.Amortizations.Should().HaveCountGreaterThan(2, "ГТЛК 1P-16 гасит номинал частями, а не одним платежом в конце");
        result.Amortizations.Should().OnlyContain(a => a.AmountRub > 0);
        result.Amortizations.Should().BeInAscendingOrder(a => a.Date);

        result.Offers.Should().ContainSingle();
        result.Offers[0].OfferType.Should().Be(OfferType.Put, "ISS offertype 'Оферта/Погашение' трактуется как put по умолчанию");
    }

    [Fact]
    public void IncompleteCoupons_SyntheticFixture_DetectedAsDataIncomplete()
    {
        // Синтетическая фикстура (см. Fixtures/Moex/README.md): реальный ответ MOEX с искусственно
        // удалёнными серединным и последним купонами — имитирует документированный в spec §4.4 риск
        // "MOEX отдаёт не все купоны". Реальный SECID с подтверждённой неполнотой не был найден
        // в ходе живых запросов этой сессии, поэтому фикстура помечена SYNTHETIC явно.
        var json = ReadFixture("bondization_incomplete_coupons_SYNTHETIC.json");

        var result = MoexBondizationParser.Parse("SU26238RMFS4", json);

        result.DataIncomplete.Should().BeTrue("разрыв между двумя известными купонами должен детектироваться как неполнота");
        result.Coupons.Should().NotBeEmpty("даже при неполноте сохраняем то, что есть, а не отбрасываем всё");
    }

    [Fact]
    public void MissingBlock_ReturnsEmptyList_DoesNotThrow()
    {
        const string json = "{\"coupons\": {\"columns\": [\"coupondate\"], \"data\": []}}";

        var act = () => MoexBondizationParser.Parse("TEST", json);

        var result = act.Should().NotThrow().Subject;
        result.Coupons.Should().BeEmpty();
        result.Amortizations.Should().BeEmpty("блок amortizations отсутствует в ответе — не должно падать");
        result.Offers.Should().BeEmpty();
    }
}
