using Bonds.Core.Models;
using Bonds.Infrastructure.Repositories;
using Bonds.IntegrationTests.Infrastructure;
using FluentAssertions;
using MySqlConnector;
using Xunit;

namespace Bonds.IntegrationTests;

/// <summary>Задача 55: репозитории валюты на реальном MySQL - идемпотентность upsert, регистр ISO, изоляция счетов, единицы, enum строкой.</summary>
[Collection("Integration")]
public class CurrencyRepositoriesTests
{
    private readonly TestWebApplicationFactory _factory;

    public CurrencyRepositoriesTests(TestWebApplicationFactory factory) => _factory = factory;

    private string Conn => _factory.Database.ConnectionString;

    private async Task<ulong> CreateAccountAsync()
    {
        var userId = await new UserRepository(Conn).CreateAsync(new User { TelegramId = Random.Shared.NextInt64(1, long.MaxValue) });
        return await new AccountRepository(Conn).CreateAsync(new Account { UserId = userId, Name = "Main" });
    }

    // iso_code уникален (VARCHAR(8)) - контейнер общий, берём случайный 7-символьный код.
    private static string NewIso() => "T" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private async Task<(ulong Id, string Iso, string Figi)> CreateCurrencyAsync()
    {
        var iso = NewIso();
        var figi = $"FIGI{Guid.NewGuid():N}"[..20];
        var id = await new CurrencyRepository(Conn).UpsertAsync(new Currency
        {
            Figi = figi, IsoCode = iso.ToLowerInvariant(), Ticker = $"{iso}RUB_TOM", Name = "Тестовая валюта", MoexSecid = $"{iso}RUB_TOM", Lot = 1000m, FaceValue = 100m,
        });
        return (id, iso, figi);
    }

    [Fact]
    public async Task Currency_UpsertByFigi_IsIdempotent_NormalizesIsoToUpper_AndKeepsFaceValue()
    {
        var repo = new CurrencyRepository(Conn);
        var (id, iso, figi) = await CreateCurrencyAsync();

        var id2 = await repo.UpsertAsync(new Currency { Figi = figi, IsoCode = iso, Name = "Новое имя", FaceValue = 100m });

        id2.Should().Be(id);
        var loaded = await repo.GetByFigiAsync(figi);
        loaded!.IsoCode.Should().Be(iso, "ISO хранится верхним регистром, T-Invest отдаёт нижний");
        loaded.Name.Should().Be("Новое имя");
        loaded.FaceValue.Should().Be(100m);
        loaded.UpdatedAt.Should().BeAfter(DateTime.UtcNow.AddMinutes(-5));
        (await repo.GetByIsoAsync(iso.ToLowerInvariant()))!.Id.Should().Be(id, "поиск по ISO нечувствителен к регистру");
        (await repo.GetByIdAsync(id))!.Figi.Should().Be(figi);
    }

    [Fact]
    public async Task CurrencyPosition_Upsert_ThenDeleteAllExcept_DoesNotTouchOtherAccount_AndKeepsNullAvgPrice()
    {
        var accountA = await CreateAccountAsync();
        var accountB = await CreateAccountAsync();
        var (c1, _, _) = await CreateCurrencyAsync();
        var (c2, _, _) = await CreateCurrencyAsync();
        var repo = new CurrencyPositionRepository(Conn);

        foreach (var acc in new[] { accountA, accountB })
        {
            await repo.UpsertAsync(new CurrencyPosition { AccountId = acc, CurrencyId = c1, Quantity = 1000.5m, AvgPurchasePriceRub = 13.2m, CurrentPriceRub = 12.2255m, PriceAsOf = DateTime.UtcNow });
            await repo.UpsertAsync(new CurrencyPosition { AccountId = acc, CurrencyId = c2, Quantity = 5m, AvgPurchasePriceRub = null });
        }

        await repo.UpsertAsync(new CurrencyPosition { AccountId = accountA, CurrencyId = c1, Quantity = 1200m, AvgPurchasePriceRub = 13.1m, CurrentPriceRub = 12.3m });
        var a = await repo.GetByAccountAsync(accountA);
        a.Should().HaveCount(2, "повторный upsert не дубль");
        a.Single(p => p.CurrencyId == c1).Quantity.Should().Be(1200m);
        a.Single(p => p.CurrencyId == c1).AvgPurchasePriceRub.Should().Be(13.1m);
        a.Single(p => p.CurrencyId == c2).AvgPurchasePriceRub.Should().BeNull("NULL = брокер не знает цену входа, а не 0");

        (await repo.DeleteAllExceptAsync(accountA, new[] { c1 })).Should().Be(1);
        (await repo.GetByAccountAsync(accountA)).Select(p => p.CurrencyId).Should().Equal(c1);
        (await repo.GetByAccountAsync(accountB)).Should().HaveCount(2, "чужой счёт не затронут");
        (await repo.DeleteAllExceptAsync(accountA, Array.Empty<ulong>())).Should().Be(1);
    }

    [Fact]
    public async Task CurrencyOperation_UpsertByExternalId_IsIdempotent_PreservesSignAndNullCurrency_AllEnumValues()
    {
        var accountId = await CreateAccountAsync();
        var (currencyId, _, _) = await CreateCurrencyAsync();
        var repo = new CurrencyOperationRepository(Conn);
        var ext = new[] { "a", "b", "c", "d", "e" }.Select(x => $"cop-{x}-{Guid.NewGuid():N}").ToArray();

        CurrencyOperation Op(string e, CurrencyOperationType t, decimal amount, ulong? currency) => new()
        {
            AccountId = accountId, CurrencyId = currency, Type = t, Date = new DateTime(2026, 10, 1, 12, 0, 0), AmountRub = amount, Quantity = 1000.5m, ExternalId = e,
        };

        var batch = new[]
        {
            Op(ext[0], CurrencyOperationType.Buy, -13000.5m, currencyId), Op(ext[1], CurrencyOperationType.Sell, 5000m, null),
            Op(ext[2], CurrencyOperationType.Tax, -1m, currencyId), Op(ext[3], CurrencyOperationType.Fee, -2m, currencyId),
            Op(ext[4], CurrencyOperationType.Income, 3m, currencyId),
        };
        await repo.UpsertManyByExternalIdAsync(batch);
        await repo.UpsertManyByExternalIdAsync(batch.Select(o => o.ExternalId == ext[1] ? Op(ext[1], CurrencyOperationType.Sell, 5100m, currencyId) : o));

        var all = await repo.GetByAccountAsync(accountId);
        all.Should().HaveCount(5, "повторный синк не создаёт дублей");
        all.Single(o => o.ExternalId == ext[0]).AmountRub.Should().Be(-13000.5m);
        all.Select(o => o.Type).Should().BeEquivalentTo(
            new[] { CurrencyOperationType.Buy, CurrencyOperationType.Sell, CurrencyOperationType.Tax, CurrencyOperationType.Fee, CurrencyOperationType.Income });
        all.Single(o => o.ExternalId == ext[1]).AmountRub.Should().Be(5100m);
        all.Single(o => o.ExternalId == ext[1]).CurrencyId.Should().Be(currencyId);
    }

    [Fact]
    public async Task CurrencyOperation_CurrencyDeleted_KeepsOperationWithNullCurrency()
    {
        var accountId = await CreateAccountAsync();
        var (currencyId, _, _) = await CreateCurrencyAsync();
        var ops = new CurrencyOperationRepository(Conn);
        await ops.UpsertManyByExternalIdAsync([new CurrencyOperation { AccountId = accountId, CurrencyId = currencyId, Type = CurrencyOperationType.Sell, Date = DateTime.UtcNow.Date, AmountRub = 100m, ExternalId = $"cop-{Guid.NewGuid():N}" }]);

        await using (var conn = new MySqlConnection(Conn))
        {
            await conn.OpenAsync();
            await using var cmd = new MySqlCommand("DELETE FROM currencies WHERE id = @Id", conn);
            cmd.Parameters.AddWithValue("@Id", currencyId);
            await cmd.ExecuteNonQueryAsync();
        }

        var remaining = await ops.GetByAccountAsync(accountId);
        remaining.Should().ContainSingle().Which.CurrencyId.Should().BeNull("ON DELETE SET NULL: деньги из журнала не теряются");
    }

    [Fact]
    public async Task CurrencyRates_BatchUpsert_Idempotent_LatestAndLastDate_RubPerUnitPrecision()
    {
        var (currencyId, _, _) = await CreateCurrencyAsync();
        var repo = new CurrencyMarketDataRepository(Conn);
        var d = new DateOnly(2026, 10, 1);

        (await repo.GetLatestRateAsync(currencyId)).Should().BeNull();
        (await repo.GetLastRateDateAsync(currencyId)).Should().BeNull();

        await repo.UpsertRatesAsync(currencyId, [new CurrencyRatePoint(d, 12.414m), new CurrencyRatePoint(d.AddDays(1), 0.082m), new CurrencyRatePoint(d.AddDays(1), 0.083m)]);
        await repo.UpsertRatesAsync(currencyId, [new CurrencyRatePoint(d, 12.5m)]);

        var all = await repo.GetRatesAsync(currencyId, d, d.AddDays(10));
        all.Should().HaveCount(2, "дубль даты схлопнут");
        all[0].CloseRub.Should().Be(12.5m, "повторная запись той же даты поправляет курс");
        all[1].CloseRub.Should().Be(0.083m, "шесть знаков хватает для рублей за иену");
        (await repo.GetLatestRateAsync(currencyId))!.TradeDate.Should().Be(d.AddDays(1));
        (await repo.GetLastRateDateAsync(currencyId)).Should().Be(d.AddDays(1));
        (await repo.GetRatesAsync(currencyId, d.AddDays(5), d.AddDays(9))).Should().BeEmpty();
    }

    [Fact]
    public async Task Currency_Upsert_ReplacesFxPlaceholderFigi_ButNeverOverwritesRealFigi()
    {
        var repo = new CurrencyRepository(Conn);
        var iso = NewIso();
        var placeholderId = await repo.UpsertAsync(new Currency { Figi = "FX:" + iso, IsoCode = iso, Name = iso });

        // Первый синк T-Invest: заглушка FX:* заменяется настоящим FIGI, id сохраняется.
        var realFigi = $"BBG{Guid.NewGuid():N}"[..20];
        var id1 = await repo.UpsertAsync(new Currency { Figi = realFigi, IsoCode = iso, Name = "Real" });
        id1.Should().Be(placeholderId);
        (await repo.GetByIsoAsync(iso))!.Figi.Should().Be(realFigi);

        // Второй инструмент той же валюты (TOD) не перетирает настоящий FIGI.
        var otherFigi = $"BBG{Guid.NewGuid():N}"[..20];
        var id2 = await repo.UpsertAsync(new Currency { Figi = otherFigi, IsoCode = iso, Name = "Real TOD" });
        id2.Should().Be(placeholderId);
        (await repo.GetByIsoAsync(iso))!.Figi.Should().Be(realFigi);

        // Повторная заглушка тоже не откатывает настоящий FIGI.
        await repo.UpsertAsync(new Currency { Figi = "FX:" + iso, IsoCode = iso, Name = iso });
        (await repo.GetByIsoAsync(iso))!.Figi.Should().Be(realFigi);
    }
}
