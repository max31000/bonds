using System.Data;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Dapper;
using MySqlConnector;

namespace Bonds.Infrastructure.Repositories;

/// <summary>Справочник фондов (задача 51). Образец — <see cref="InstrumentRepository"/>.</summary>
public class FundRepository : IFundRepository
{
    private readonly string _connectionString;

    public FundRepository(string connectionString) => _connectionString = connectionString;

    private IDbConnection CreateConnection() => new MySqlConnection(_connectionString);

    private const string SelectColumns = @"
        id AS Id, figi AS Figi, isin AS Isin, ticker AS Ticker, secid AS Secid, name AS Name,
        management_company AS ManagementCompany, focus_type AS FocusType,
        total_expense_fraction AS TotalExpenseFraction, forecast_method AS ForecastMethod,
        benchmark_index AS BenchmarkIndex, created_at AS CreatedAt, updated_at AS UpdatedAt";

    public async Task<Fund?> GetByIdAsync(ulong id)
    {
        using var conn = CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<Fund>(
            $"SELECT {SelectColumns} FROM funds WHERE id = @Id", new { Id = id });
    }

    public async Task<Fund?> GetByFigiAsync(string figi)
    {
        using var conn = CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<Fund>(
            $"SELECT {SelectColumns} FROM funds WHERE figi = @Figi", new { Figi = figi });
    }

    public async Task<Fund?> GetByIsinAsync(string isin)
    {
        using var conn = CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<Fund>(
            $"SELECT {SelectColumns} FROM funds WHERE isin = @Isin", new { Isin = isin });
    }

    public async Task<IReadOnlyList<Fund>> GetAllAsync()
    {
        using var conn = CreateConnection();
        return (await conn.QueryAsync<Fund>($"SELECT {SelectColumns} FROM funds ORDER BY id")).ToList();
    }

    public async Task<ulong> UpsertAsync(Fund fund)
    {
        using var conn = CreateConnection();
        // forecast_method / benchmark_index в ON DUPLICATE KEY UPDATE намеренно отсутствуют: это настройки
        // пользователя, суточный рефреш справочника не должен их сбрасывать. updated_at обновится сам
        // (ON UPDATE CURRENT_TIMESTAMP), если изменилось хоть одно поле — поэтому явно трогаем его.
        const string sql = @"
            INSERT INTO funds (figi, isin, ticker, secid, name, management_company, focus_type,
                               total_expense_fraction, forecast_method, benchmark_index)
            VALUES (@Figi, @Isin, @Ticker, @Secid, @Name, @ManagementCompany, @FocusType,
                    @TotalExpenseFraction, @ForecastMethod, @BenchmarkIndex)
            ON DUPLICATE KEY UPDATE
                isin = @Isin, ticker = @Ticker, secid = @Secid, name = @Name,
                management_company = @ManagementCompany, focus_type = @FocusType,
                total_expense_fraction = @TotalExpenseFraction,
                updated_at = CURRENT_TIMESTAMP,
                id = LAST_INSERT_ID(id);
            SELECT LAST_INSERT_ID();";

        return await conn.ExecuteScalarAsync<ulong>(sql, new
        {
            fund.Figi,
            fund.Isin,
            fund.Ticker,
            fund.Secid,
            fund.Name,
            fund.ManagementCompany,
            fund.FocusType,
            fund.TotalExpenseFraction,
            ForecastMethod = fund.ForecastMethod.ToString(),
            fund.BenchmarkIndex,
        });
    }

    public async Task UpdateSettingsAsync(ulong fundId, FundForecastMethod forecastMethod, string? benchmarkIndex)
    {
        using var conn = CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE funds SET forecast_method = @Method, benchmark_index = @Benchmark WHERE id = @Id",
            new { Id = fundId, Method = forecastMethod.ToString(), Benchmark = benchmarkIndex });
    }
}
