using System.Data;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Dapper;
using MySqlConnector;

namespace Bonds.Infrastructure.Repositories;

/// <summary>Справочник валют (задача 55), образец — <see cref="FundRepository"/>.</summary>
public class CurrencyRepository : ICurrencyRepository
{
    private readonly string _connectionString;

    public CurrencyRepository(string connectionString) => _connectionString = connectionString;

    private IDbConnection CreateConnection() => new MySqlConnection(_connectionString);

    private const string SelectColumns = @"
        id AS Id, figi AS Figi, iso_code AS IsoCode, ticker AS Ticker, name AS Name, moex_secid AS MoexSecid,
        lot AS Lot, face_value AS FaceValue, created_at AS CreatedAt, updated_at AS UpdatedAt";

    public async Task<Currency?> GetByIdAsync(ulong id)
    {
        using var conn = CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<Currency>(
            $"SELECT {SelectColumns} FROM currencies WHERE id = @Id", new { Id = id });
    }

    public async Task<Currency?> GetByFigiAsync(string figi)
    {
        using var conn = CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<Currency>(
            $"SELECT {SelectColumns} FROM currencies WHERE figi = @Figi", new { Figi = figi });
    }

    public async Task<Currency?> GetByIsoAsync(string isoCode)
    {
        using var conn = CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<Currency>(
            $"SELECT {SelectColumns} FROM currencies WHERE iso_code = @Iso", new { Iso = isoCode.Trim().ToUpperInvariant() });
    }

    public async Task<IReadOnlyList<Currency>> GetAllAsync()
    {
        using var conn = CreateConnection();
        return (await conn.QueryAsync<Currency>($"SELECT {SelectColumns} FROM currencies ORDER BY id")).ToList();
    }

    public async Task<ulong> UpsertAsync(Currency currency)
    {
        using var conn = CreateConnection();
        // figi обновляется ТОЛЬКО поверх заглушки FX:* (всегда отслеживаемая валюта, задача 60): её заменяет настоящий FIGI при первом
        // синке T-Invest. Настоящий FIGI не перетирается — иначе два инструмента одной валюты (TOM/TOD) перекидывали бы его каждый синк.
        // updated_at трогаем явно: ворота «справочник раз в сутки» должны сдвигаться, даже если ни одно поле не изменилось.
        const string sql = @"
            INSERT INTO currencies (figi, iso_code, ticker, name, moex_secid, lot, face_value)
            VALUES (@Figi, @IsoCode, @Ticker, @Name, @MoexSecid, @Lot, @FaceValue)
            ON DUPLICATE KEY UPDATE
                figi = IF(figi LIKE 'FX:%', @Figi, figi), iso_code = @IsoCode, ticker = @Ticker, name = @Name, moex_secid = @MoexSecid,
                lot = @Lot, face_value = @FaceValue,
                updated_at = CURRENT_TIMESTAMP,
                id = LAST_INSERT_ID(id);
            SELECT LAST_INSERT_ID();";

        return await conn.ExecuteScalarAsync<ulong>(sql, new
        {
            currency.Figi,
            IsoCode = currency.IsoCode.Trim().ToUpperInvariant(),
            currency.Ticker,
            currency.Name,
            currency.MoexSecid,
            currency.Lot,
            currency.FaceValue,
        });
    }
}
