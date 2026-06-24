using Bonds.Core.Models;
using Bonds.Infrastructure.Repositories;
using Bonds.IntegrationTests.Infrastructure;
using FluentAssertions;
using MySqlConnector;
using Xunit;

namespace Bonds.IntegrationTests;

/// <summary>
/// Round-trip тесты IInstrumentRepository: вставка -> чтение -> обновление, уникальность ISIN
/// (plan/03 §D, критерии приёмки этапа 03).
/// </summary>
[Collection("Integration")]
public class InstrumentRepositoryTests
{
    private readonly TestWebApplicationFactory _factory;

    public InstrumentRepositoryTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private InstrumentRepository CreateRepo() => new(_factory.Database.ConnectionString);

    private static Instrument NewInstrument(string isin) => new()
    {
        Isin = isin,
        Secid = "SU26238",
        Figi = "BBG00FXBVTV0",
        Issuer = "Минфин РФ",
        Sector = "Sovereign",
        FaceValue = 1000m,
        Currency = "RUB",
        CouponType = CouponType.Fixed,
        HasAmortization = false,
        HasOffers = false,
        MaturityDate = new DateOnly(2030, 5, 15),
        DataIncomplete = false,
        IsOutOfScopeCurrency = false,
    };

    [Fact]
    public async Task Upsert_Then_GetByIsin_RoundTrips()
    {
        var repo = CreateRepo();
        var isin = $"RU{Guid.NewGuid():N}".Substring(0, 12);
        var instrument = NewInstrument(isin);

        var id = await repo.UpsertAsync(instrument);
        id.Should().BeGreaterThan(0);

        var loaded = await repo.GetByIsinAsync(isin);
        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(id);
        loaded.Secid.Should().Be(instrument.Secid);
        loaded.Figi.Should().Be(instrument.Figi);
        loaded.Issuer.Should().Be(instrument.Issuer);
        loaded.FaceValue.Should().Be(instrument.FaceValue);
        loaded.Currency.Should().Be("RUB");
        loaded.CouponType.Should().Be(CouponType.Fixed);
        loaded.MaturityDate.Should().Be(instrument.MaturityDate);
    }

    [Fact]
    public async Task Upsert_SameIsinTwice_UpdatesInPlace_NoDuplicate()
    {
        var repo = CreateRepo();
        var isin = $"RU{Guid.NewGuid():N}".Substring(0, 12);
        var first = NewInstrument(isin);

        var firstId = await repo.UpsertAsync(first);

        var updated = NewInstrument(isin);
        updated.Issuer = "Обновлённый эмитент";
        updated.CouponType = CouponType.Floating;
        updated.DataIncomplete = true;

        var secondId = await repo.UpsertAsync(updated);

        secondId.Should().Be(firstId, "upsert по ISIN не должен создавать новую строку");

        var loaded = await repo.GetByIsinAsync(isin);
        loaded!.Issuer.Should().Be("Обновлённый эмитент");
        loaded.CouponType.Should().Be(CouponType.Floating);
        loaded.DataIncomplete.Should().BeTrue();
    }

    [Fact]
    public async Task GetById_Then_GetBySecid_Then_GetByFigi_AllResolve()
    {
        var repo = CreateRepo();
        var isin = $"RU{Guid.NewGuid():N}".Substring(0, 12);
        var secid = $"SEC{Guid.NewGuid():N}".Substring(0, 10);
        var figi = $"FIGI{Guid.NewGuid():N}".Substring(0, 10);

        var instrument = NewInstrument(isin);
        instrument.Secid = secid;
        instrument.Figi = figi;

        var id = await repo.UpsertAsync(instrument);

        (await repo.GetByIdAsync(id))!.Isin.Should().Be(isin);
        (await repo.GetBySecidAsync(secid))!.Id.Should().Be(id);
        (await repo.GetByFigiAsync(figi))!.Id.Should().Be(id);
    }

    [Fact]
    public async Task DuplicateIsin_ViaRawInsert_ViolatesUniqueConstraint()
    {
        // Прямая проверка UNIQUE KEY uq_instruments_isin на уровне схемы (а не через upsert-логику
        // репозитория), как того требует критерий приёмки "уникальность isin".
        var isin = $"RU{Guid.NewGuid():N}".Substring(0, 12);

        await using var conn = new MySqlConnection(_factory.Database.ConnectionString);
        await conn.OpenAsync();

        const string insertSql = @"
            INSERT INTO instruments (isin, face_value, currency, coupon_type, maturity_date)
            VALUES (@Isin, 1000, 'RUB', 'Fixed', '2030-01-01')";

        await using (var cmd = new MySqlCommand(insertSql, conn))
        {
            cmd.Parameters.AddWithValue("@Isin", isin);
            await cmd.ExecuteNonQueryAsync();
        }

        Func<Task> act = async () =>
        {
            await using var cmd = new MySqlCommand(insertSql, conn);
            cmd.Parameters.AddWithValue("@Isin", isin);
            await cmd.ExecuteNonQueryAsync();
        };

        await act.Should().ThrowAsync<MySqlException>();
    }

    [Fact]
    public async Task GetAll_ReturnsInsertedInstrument()
    {
        var repo = CreateRepo();
        var isin = $"RU{Guid.NewGuid():N}".Substring(0, 12);
        await repo.UpsertAsync(NewInstrument(isin));

        var all = await repo.GetAllAsync();

        all.Should().Contain(i => i.Isin == isin);
    }
}
