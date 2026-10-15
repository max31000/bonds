using System.Data;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Dapper;
using MySqlConnector;

namespace Bonds.Infrastructure.Repositories;

/// <summary>Задача 46: ручные рейтинги эмитентов (<c>issuer_ratings</c>, ключ (inn, agency)).</summary>
public class IssuerRatingRepository : IIssuerRatingRepository
{
    private readonly string _connectionString;

    public IssuerRatingRepository(string connectionString) => _connectionString = connectionString;

    private IDbConnection CreateConnection() => new MySqlConnection(_connectionString);

    private const string SelectColumns = "inn AS Inn, agency AS Agency, rating AS Rating, rating_date AS RatingDate, note AS Note";

    public async Task<IReadOnlyList<IssuerRating>> GetByInnAsync(string inn, CancellationToken ct = default)
    {
        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<IssuerRating>(
            $"SELECT {SelectColumns} FROM issuer_ratings WHERE inn = @Inn ORDER BY agency", new { Inn = inn });
        return rows.ToList();
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<IssuerRating>>> GetByInnsAsync(
        IReadOnlyCollection<string> inns, CancellationToken ct = default)
    {
        var result = new Dictionary<string, IReadOnlyList<IssuerRating>>();
        if (inns.Count == 0) return result;

        using var conn = CreateConnection();
        var rows = await conn.QueryAsync<IssuerRating>(
            $"SELECT {SelectColumns} FROM issuer_ratings WHERE inn IN @Inns ORDER BY inn, agency", new { Inns = inns.ToArray() });
        foreach (var group in rows.GroupBy(r => r.Inn))
        {
            result[group.Key] = group.ToList();
        }

        return result;
    }

    public async Task UpsertAsync(IssuerRating rating, CancellationToken ct = default)
    {
        using var conn = CreateConnection();
        await conn.ExecuteAsync(@"
            INSERT INTO issuer_ratings (inn, agency, rating, rating_date, note)
            VALUES (@Inn, @Agency, @Rating, @RatingDate, @Note)
            ON DUPLICATE KEY UPDATE rating = VALUES(rating), rating_date = VALUES(rating_date), note = VALUES(note)",
            new { rating.Inn, rating.Agency, rating.Rating, rating.RatingDate, rating.Note });
    }

    public async Task<bool> DeleteAsync(string inn, string agency, CancellationToken ct = default)
    {
        using var conn = CreateConnection();
        var affected = await conn.ExecuteAsync(
            "DELETE FROM issuer_ratings WHERE inn = @Inn AND agency = @Agency", new { Inn = inn, Agency = agency });
        return affected > 0;
    }
}
