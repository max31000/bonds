using System.Data;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Dapper;
using MySqlConnector;

namespace Bonds.Infrastructure.Repositories;

/// <summary>Торговые метаданные инструментов (<c>instrument_trading_meta</c>, задача 61). Единицы — в doc-comment <see cref="InstrumentTradingMeta"/>.</summary>
public class InstrumentTradingMetaRepository : IInstrumentTradingMetaRepository
{
    private readonly string _connectionString;

    public InstrumentTradingMetaRepository(string connectionString) => _connectionString = connectionString;

    private IDbConnection CreateConnection() => new MySqlConnection(_connectionString);

    private const string SelectColumns = @"
        id AS Id, broker AS Broker, instrument_key AS InstrumentKey, uid AS Uid, figi AS Figi, ticker AS Ticker, class_code AS ClassCode,
        lot AS Lot, min_price_increment AS MinPriceIncrement, min_price_increment_points AS MinPriceIncrementPoints, currency AS Currency,
        api_trade_available AS ApiTradeAvailable, buy_available AS BuyAvailable, sell_available AS SellAvailable,
        pays_distributions AS PaysDistributions, its_twin AS ItsTwin, updated_at AS UpdatedAt";

    public async Task UpsertAsync(InstrumentTradingMeta meta)
    {
        using var conn = CreateConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO instrument_trading_meta
                (broker, instrument_key, uid, figi, ticker, class_code, lot, min_price_increment, min_price_increment_points, currency,
                 api_trade_available, buy_available, sell_available, pays_distributions, its_twin, updated_at)
              VALUES
                (@Broker, @InstrumentKey, @Uid, @Figi, @Ticker, @ClassCode, @Lot, @MinPriceIncrement, @MinPriceIncrementPoints, @Currency,
                 @ApiTradeAvailable, @BuyAvailable, @SellAvailable, @PaysDistributions, @ItsTwin, @UpdatedAt)
              ON DUPLICATE KEY UPDATE uid = VALUES(uid), figi = VALUES(figi), ticker = VALUES(ticker), class_code = VALUES(class_code),
                lot = VALUES(lot), min_price_increment = VALUES(min_price_increment),
                min_price_increment_points = VALUES(min_price_increment_points), currency = VALUES(currency),
                api_trade_available = VALUES(api_trade_available), buy_available = VALUES(buy_available),
                sell_available = VALUES(sell_available), pays_distributions = VALUES(pays_distributions),
                its_twin = VALUES(its_twin), updated_at = VALUES(updated_at)",
            meta);
    }

    public async Task<IReadOnlyList<InstrumentTradingMeta>> GetByBrokerAsync(string broker)
    {
        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<InstrumentTradingMeta>(
            $"SELECT {SelectColumns} FROM instrument_trading_meta WHERE broker = @Broker ORDER BY instrument_key",
            new { Broker = broker });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<InstrumentTradingMeta>> GetByKeysAsync(string broker, IReadOnlyCollection<string> keys)
    {
        if (keys.Count == 0) return [];

        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<InstrumentTradingMeta>(
            $"SELECT {SelectColumns} FROM instrument_trading_meta WHERE broker = @Broker AND instrument_key IN @Keys ORDER BY instrument_key",
            new { Broker = broker, Keys = keys.ToArray() });
        return rows.ToList();
    }
}
