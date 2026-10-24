using System.Data;
using Bonds.Core.Capital;
using Bonds.Core.Interfaces.Repositories;
using Dapper;
using MySqlConnector;

namespace Bonds.Infrastructure.Repositories;

/// <summary>
/// Корзины капитала (<c>baskets</c>/<c>basket_members</c>, задача 62). Веса — ДОЛИ DECIMAL(9,6), <c>min_trade</c> — валюта портфеля.
/// Каждый метод фильтрует по <c>user_id</c>: корзины и члены другого пользователя недоступны.
/// </summary>
public class CapitalBasketRepository : ICapitalBasketRepository
{
    private readonly string _connectionString;

    public CapitalBasketRepository(string connectionString) => _connectionString = connectionString;

    private MySqlConnection CreateConnection() => new(_connectionString);

    private const string BasketColumns = @"
        id AS Id, user_id AS UserId, portfolio AS Portfolio, code AS Code, title AS Title, target_weight AS TargetWeight,
        band AS Band, hard_band AS HardBand, liquidity_tier AS LiquidityTier, fill_rule AS FillRule,
        auto_buy AS AutoBuy, auto_sell AS AutoSell, min_trade AS MinTrade, sort_order AS SortOrder";

    private const string MemberColumns = @"
        m.id AS Id, m.basket_id AS BasketId, m.broker AS Broker, m.instrument_key AS InstrumentKey,
        m.sub_weight AS SubWeight, m.match_rule AS MatchRule";

    public async Task<IReadOnlyList<CapitalBasket>> GetByUserAsync(ulong userId, string? portfolio = null)
    {
        using var conn = CreateConnection();
        var baskets = (await conn.QueryAsync<CapitalBasket>(
            $@"SELECT {BasketColumns} FROM baskets WHERE user_id = @UserId AND (@Portfolio IS NULL OR portfolio = @Portfolio)
               ORDER BY portfolio, sort_order, id",
            new { UserId = userId, Portfolio = portfolio })).ToList();
        if (baskets.Count == 0) return baskets;

        var members = (await conn.QueryAsync<CapitalBasketMember>(
            $@"SELECT {MemberColumns} FROM basket_members m JOIN baskets b ON b.id = m.basket_id
               WHERE b.user_id = @UserId AND (@Portfolio IS NULL OR b.portfolio = @Portfolio) ORDER BY m.id",
            new { UserId = userId, Portfolio = portfolio })).ToList();
        var byBasket = members.GroupBy(m => m.BasketId).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var basket in baskets)
            basket.Members = byBasket.TryGetValue(basket.Id, out var list) ? list : [];
        return baskets;
    }

    private const int SeedMaxAttempts = 3;

    /// <summary>
    /// Ошибка гонки первого посева: параллельный запрос уже вставил корзины (UNIQUE user_id+code) либо InnoDB выбрал нас жертвой взаимной
    /// блокировки двух конкурирующих INSERT. В обоих случаях транзакция откатывается и состояние перечитывается.
    /// </summary>
    public static bool IsSeedRaceError(MySqlErrorCode code) => code is MySqlErrorCode.DuplicateKeyEntry or MySqlErrorCode.LockDeadlock;

    public async Task<bool> SeedIfEmptyAsync(ulong userId, IReadOnlyList<CapitalBasket> defaults)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var conn = CreateConnection();
            await conn.OpenAsync();
            using var tx = await conn.BeginTransactionAsync();
            try
            {
                // Сериализация первых запросов одного пользователя: блокировка строки users держится до конца транзакции,
                // поэтому второй посев ждёт коммита первого. COUNT — блокирующее чтение (FOR UPDATE), оно видит последние
                // закоммиченные строки, а не снимок REPEATABLE READ: иначе ретрай не замечал чужой посев (флейк CI 2026-10-23).
                await conn.ExecuteScalarAsync<ulong?>("SELECT id FROM users WHERE id = @UserId FOR UPDATE", new { UserId = userId }, tx);
                var existing = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM baskets WHERE user_id = @UserId FOR UPDATE", new { UserId = userId }, tx);
                if (existing > 0)
                {
                    await tx.RollbackAsync();
                    return false;
                }

                foreach (var basket in defaults) await InsertBasketAsync(conn, tx, userId, basket);
                await tx.CommitAsync();
                return true;
            }
            catch (MySqlException ex) when (IsSeedRaceError(ex.ErrorCode))
            {
                // Параллельный первый запрос уже посеял (или сеет) корзины. Откат и повторное чтение: увидим чужой посев — вернём false.
                await tx.RollbackAsync();
                if (attempt >= SeedMaxAttempts) throw;
            }
        }
    }

    public Task ReplacePortfolioAsync(ulong userId, string portfolio, IReadOnlyList<CapitalBasket> defaults) =>
        ReplacePortfoliosAsync(userId, new Dictionary<string, IReadOnlyList<CapitalBasket>> { [portfolio] = defaults });

    public async Task ReplacePortfoliosAsync(ulong userId, IReadOnlyDictionary<string, IReadOnlyList<CapitalBasket>> defaultsByPortfolio)
    {
        using var conn = CreateConnection();
        await conn.OpenAsync();
        using var tx = await conn.BeginTransactionAsync();
        foreach (var (portfolio, defaults) in defaultsByPortfolio)
        {
            await conn.ExecuteAsync("DELETE FROM baskets WHERE user_id = @UserId AND portfolio = @Portfolio", new { UserId = userId, Portfolio = portfolio }, tx);
            foreach (var basket in defaults) await InsertBasketAsync(conn, tx, userId, basket);
        }

        await tx.CommitAsync();
    }

    private static async Task InsertBasketAsync(IDbConnection conn, IDbTransaction tx, ulong userId, CapitalBasket b)
    {
        var id = await conn.ExecuteScalarAsync<ulong>(
            @"INSERT INTO baskets (user_id, portfolio, code, title, target_weight, band, hard_band, liquidity_tier, fill_rule,
                                   auto_buy, auto_sell, min_trade, sort_order)
              VALUES (@UserId, @Portfolio, @Code, @Title, @TargetWeight, @Band, @HardBand, @LiquidityTier, @FillRule,
                      @AutoBuy, @AutoSell, @MinTrade, @SortOrder);
              SELECT LAST_INSERT_ID();",
            new
            {
                UserId = userId, b.Portfolio, b.Code, b.Title, b.TargetWeight, b.Band, b.HardBand, b.LiquidityTier, b.FillRule,
                b.AutoBuy, b.AutoSell, b.MinTrade, b.SortOrder,
            }, tx);
        foreach (var m in b.Members)
        {
            await conn.ExecuteAsync(
                @"INSERT INTO basket_members (basket_id, broker, instrument_key, sub_weight, match_rule)
                  VALUES (@BasketId, @Broker, @InstrumentKey, @SubWeight, @MatchRule)",
                new { BasketId = id, m.Broker, m.InstrumentKey, m.SubWeight, m.MatchRule }, tx);
        }
    }

    public async Task UpdateManyAsync(ulong userId, IReadOnlyList<CapitalBasket> baskets)
    {
        using var conn = CreateConnection();
        await conn.OpenAsync();
        using var tx = await conn.BeginTransactionAsync();
        await WriteFieldsAsync(conn, tx, userId, baskets);
        await tx.CommitAsync();
    }

    public async Task<TResult> UpdateLockedAsync<TResult>(
        ulong userId, Func<IReadOnlyList<CapitalBasket>, Task<(IReadOnlyList<CapitalBasket>? Changed, TResult Result)>> apply)
    {
        using var conn = CreateConnection();
        await conn.OpenAsync();
        using var tx = await conn.BeginTransactionAsync();
        // FOR UPDATE: строки корзин пользователя заблокированы до коммита — второй PUT ждёт и проверяет сумму целей уже на итоговом состоянии первого.
        var baskets = (await conn.QueryAsync<CapitalBasket>(
            $"SELECT {BasketColumns} FROM baskets WHERE user_id = @UserId ORDER BY portfolio, sort_order, id FOR UPDATE", new { UserId = userId }, tx)).ToList();
        var members = (await conn.QueryAsync<CapitalBasketMember>(
            $@"SELECT {MemberColumns} FROM basket_members m JOIN baskets b ON b.id = m.basket_id WHERE b.user_id = @UserId ORDER BY m.id",
            new { UserId = userId }, tx)).ToList();
        var byBasket = members.GroupBy(m => m.BasketId).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var basket in baskets) basket.Members = byBasket.TryGetValue(basket.Id, out var list) ? list : [];

        var (changed, result) = await apply(baskets);
        if (changed is { Count: > 0 }) await WriteFieldsAsync(conn, tx, userId, changed);
        await tx.CommitAsync();
        return result;
    }

    private static async Task WriteFieldsAsync(MySqlConnection conn, MySqlTransaction tx, ulong userId, IReadOnlyList<CapitalBasket> baskets)
    {
        foreach (var b in baskets)
        {
            await conn.ExecuteAsync(
                @"UPDATE baskets SET title = @Title, target_weight = @TargetWeight, band = @Band, hard_band = @HardBand,
                         liquidity_tier = @LiquidityTier, fill_rule = @FillRule, auto_buy = @AutoBuy, auto_sell = @AutoSell, min_trade = @MinTrade
                  WHERE id = @Id AND user_id = @UserId",
                new { b.Id, UserId = userId, b.Title, b.TargetWeight, b.Band, b.HardBand, b.LiquidityTier, b.FillRule, b.AutoBuy, b.AutoSell, b.MinTrade }, tx);
        }
    }

    public async Task<ulong> AddMemberAsync(ulong userId, ulong basketId, CapitalBasketMember member)
    {
        using var conn = CreateConnection();
        var owned = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM baskets WHERE id = @BasketId AND user_id = @UserId", new { BasketId = basketId, UserId = userId });
        if (owned == 0) return 0;

        return await conn.ExecuteScalarAsync<ulong>(
            @"INSERT INTO basket_members (basket_id, broker, instrument_key, sub_weight, match_rule)
              VALUES (@BasketId, @Broker, @InstrumentKey, @SubWeight, @MatchRule);
              SELECT LAST_INSERT_ID();",
            new { BasketId = basketId, member.Broker, member.InstrumentKey, member.SubWeight, member.MatchRule });
    }

    public async Task<bool> RemoveMemberAsync(ulong userId, ulong basketId, ulong memberId)
    {
        using var conn = CreateConnection();
        var affected = await conn.ExecuteAsync(
            @"DELETE m FROM basket_members m JOIN baskets b ON b.id = m.basket_id
              WHERE m.id = @MemberId AND m.basket_id = @BasketId AND b.user_id = @UserId",
            new { MemberId = memberId, BasketId = basketId, UserId = userId });
        return affected > 0;
    }
}
