using System.Data;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Dapper;
using MySqlConnector;

namespace Bonds.Infrastructure.Repositories;

public class InstrumentRepository : IInstrumentRepository
{
    private readonly string _connectionString;

    public InstrumentRepository(string connectionString) => _connectionString = connectionString;

    private IDbConnection CreateConnection() => new MySqlConnection(_connectionString);

    private const string SelectColumns = @"
        id AS Id, isin AS Isin, secid AS Secid, figi AS Figi, name AS Name, issuer AS Issuer, sector AS Sector,
        face_value AS FaceValue, currency AS Currency, coupon_type AS CouponType,
        has_amortization AS HasAmortization, has_offers AS HasOffers, maturity_date AS MaturityDate,
        data_incomplete AS DataIncomplete, is_out_of_scope_currency AS IsOutOfScopeCurrency,
        created_at AS CreatedAt, updated_at AS UpdatedAt";

    public async Task<Instrument?> GetByIdAsync(ulong id)
    {
        using var conn = CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<Instrument>(
            $"SELECT {SelectColumns} FROM instruments WHERE id = @Id", new { Id = id });
    }

    public async Task<Instrument?> GetByIsinAsync(string isin)
    {
        using var conn = CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<Instrument>(
            $"SELECT {SelectColumns} FROM instruments WHERE isin = @Isin", new { Isin = isin });
    }

    public async Task<Instrument?> GetBySecidAsync(string secid)
    {
        using var conn = CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<Instrument>(
            $"SELECT {SelectColumns} FROM instruments WHERE secid = @Secid", new { Secid = secid });
    }

    public async Task<Instrument?> GetByFigiAsync(string figi)
    {
        using var conn = CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<Instrument>(
            $"SELECT {SelectColumns} FROM instruments WHERE figi = @Figi", new { Figi = figi });
    }

    public async Task<IEnumerable<Instrument>> GetAllAsync()
    {
        using var conn = CreateConnection();
        return await conn.QueryAsync<Instrument>($"SELECT {SelectColumns} FROM instruments ORDER BY isin");
    }

    public async Task<ulong> UpsertAsync(Instrument instrument)
    {
        using var conn = CreateConnection();
        const string sql = @"
            INSERT INTO instruments (isin, secid, figi, name, issuer, sector, face_value, currency, coupon_type,
                                      has_amortization, has_offers, maturity_date, data_incomplete,
                                      is_out_of_scope_currency)
            VALUES (@Isin, @Secid, @Figi, @Name, @Issuer, @Sector, @FaceValue, @Currency, @CouponType,
                    @HasAmortization, @HasOffers, @MaturityDate, @DataIncomplete, @IsOutOfScopeCurrency)
            ON DUPLICATE KEY UPDATE
                secid = @Secid, figi = @Figi, name = @Name, issuer = @Issuer, sector = @Sector,
                face_value = @FaceValue, currency = @Currency, coupon_type = @CouponType,
                has_amortization = @HasAmortization, has_offers = @HasOffers,
                maturity_date = @MaturityDate, data_incomplete = @DataIncomplete,
                is_out_of_scope_currency = @IsOutOfScopeCurrency,
                id = LAST_INSERT_ID(id);
            SELECT LAST_INSERT_ID();";

        // CouponType передаётся как строка явно: Dapper для enum-свойств POCO-параметров игнорирует
        // зарегистрированный ITypeHandler и шлёт числовое значение (см. DapperTypeHandlers.cs).
        return await conn.ExecuteScalarAsync<ulong>(sql, new
        {
            instrument.Isin,
            instrument.Secid,
            instrument.Figi,
            instrument.Name,
            instrument.Issuer,
            instrument.Sector,
            instrument.FaceValue,
            instrument.Currency,
            CouponType = instrument.CouponType.ToString(),
            instrument.HasAmortization,
            instrument.HasOffers,
            instrument.MaturityDate,
            instrument.DataIncomplete,
            instrument.IsOutOfScopeCurrency,
        });
    }
}
