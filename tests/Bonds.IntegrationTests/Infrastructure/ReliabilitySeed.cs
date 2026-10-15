using Bonds.Core.Models;
using Bonds.Infrastructure.Repositories;

namespace Bonds.IntegrationTests.Infrastructure;

/// <summary>
/// Задача 46: общий сидинг данных скоркарты для интеграционных тестов (эмитент банка, карточка бумаги,
/// отчётность и статус ГИР БО, risk_level). Контейнер общий — ИНН/EMITTER_ID случайные и уникальные на вызов.
/// </summary>
internal static class ReliabilitySeed
{
    private static int _emitentCounter;

    public static string NewInn() => Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString();

    public static int NewEmitentId() => Random.Shared.Next(100_000_000, 1_900_000_000) + Interlocked.Increment(ref _emitentCounter);

    /// <summary>Год последней (свежей) отчётности: прошлый календарный — всегда моложе 20 месяцев.</summary>
    public static int LastReportYear => DateTime.UtcNow.Year - 1;

    /// <summary>Привязывает выпуск банка к эмитенту (bond_universe.emitent_id/inn/title).</summary>
    public static Task AttachIssuerAsync(string conn, string secid, int emitentId, string? inn) =>
        new BondUniverseRepository(conn).UpdateEmitentsAsync([new UniverseEmitent(secid, emitentId, inn, $"Issuer {secid}")]);

    /// <summary>Карточка бумаги MOEX с заданными флагами дефолтов (null = неизвестно).</summary>
    public static Task SeedCardAsync(string conn, string secid, string isin, int emitentId, bool? hasDefault, bool? hasTechnicalDefault, bool? qualified = false) =>
        new SecurityDetailsRepository(conn).UpsertBatchAsync([new SecurityDetails
        {
            Secid = secid, Isin = isin, EmitentId = emitentId, HasDefault = hasDefault, HasTechnicalDefault = hasTechnicalDefault,
            IsQualifiedInvestors = qualified, FetchedAt = DateTime.UtcNow,
        }]);

    public static Task SeedRiskLevelAsync(string conn, string isin, BrokerRiskLevel level) =>
        new InstrumentRiskLevelRepository(conn).UpsertBatchAsync([new InstrumentRiskLevel(isin, level, DateTime.UtcNow)]);

    /// <summary>Здоровая отчётность (РУБЛИ): те же цифры, что эталонный профиль юнит-тестов (слой B = 40/40).</summary>
    public static IssuerFinancials[] HealthyFinancials(string inn, string source = IssuerFinancialsSources.GirBo) =>
    [
        new IssuerFinancials
        {
            Inn = inn, Period = LastReportYear, Source = source, FetchedAt = DateTime.UtcNow,
            Revenue = 6e9m, OperatingProfit = 1.2e9m, InterestPaid = 0.3e9m, NetProfit = 0.6e9m, Equity = 3e9m, TotalAssets = 9e9m,
            CurrentAssets = 4e9m, CurrentLiabilities = 2e9m, Cash = 0.5e9m, LongDebt = 2e9m, ShortDebt = 0.5e9m, RetainedEarnings = 2e9m,
        },
        new IssuerFinancials { Inn = inn, Period = LastReportYear - 1, Source = source, FetchedAt = DateTime.UtcNow, Revenue = 5e9m, NetProfit = 0.4e9m },
        new IssuerFinancials { Inn = inn, Period = LastReportYear - 2, Source = source, FetchedAt = DateTime.UtcNow, Revenue = 4e9m, NetProfit = 0.3e9m },
    ];

    public static async Task SeedFinancialsAsync(string conn, string inn, string status, IssuerFinancials[]? rows = null)
    {
        var repo = new IssuerFinancialsRepository(conn);
        if (rows is { Length: > 0 }) await repo.UpsertAsync(rows);
        await repo.UpsertStatusAsync(new IssuerFinancialsStatus(inn, status, DateTime.UtcNow));
    }

    /// <summary>Полностью «здоровый» выпуск: эмитент + карточка без дефолтов + risk_level Low + отчётность ГИР БО ok
    /// (при совместной с листингом 1 и живой ликвидностью даёт Green). Возвращает ИНН.</summary>
    public static async Task<string> SeedHealthyIssuerAsync(string conn, string secid, string isin)
    {
        var inn = NewInn();
        var emitentId = NewEmitentId();
        await AttachIssuerAsync(conn, secid, emitentId, inn);
        await SeedCardAsync(conn, secid, isin, emitentId, hasDefault: false, hasTechnicalDefault: false);
        await SeedRiskLevelAsync(conn, isin, BrokerRiskLevel.Low);
        await SeedFinancialsAsync(conn, inn, IssuerFinancialsStatuses.Ok, HealthyFinancials(inn));
        return inn;
    }
}
