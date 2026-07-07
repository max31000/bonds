using Bonds.Core.Models;

namespace Bonds.Infrastructure.Connectors.Moex;

/// <summary>
/// Клиент MOEX ISS — справочно-аналитический слой (plan/00 §4 "Разделение источников данных",
/// plan/04 Часть A). Бесплатный публичный API без авторизации. Все методы возвращают null/пустой
/// результат при отсутствии данных, а не бросают исключение — устойчивость к неполноте источника
/// (§4.4) реализуется на уровне вызывающего кода (<see cref="Bonds.Core.Sync.BondSyncService"/>),
/// здесь же — только сетевой вызов и разбор ответа.
/// </summary>
public interface IMoexIssClient
{
    /// <summary>Резолвер ISIN→SECID (поиск по справочнику ISS). Null, если ISIN не найден.</summary>
    Task<string?> ResolveSecidByIsinAsync(string isin, CancellationToken ct = default);

    /// <summary>Параметры выпуска + цены (securities.json). Null, если SECID не найден/нет данных.</summary>
    Task<MoexSecurityInfo?> GetSecurityInfoAsync(string secid, CancellationToken ct = default);

    /// <summary>Купоны + амортизации + оферты (bondization.json). Пустые списки, если блок не вернулся.</summary>
    Task<MoexBondizationResult> GetBondizationAsync(string secid, CancellationToken ct = default);

    /// <summary>Последний снимок безрисковой кривой (zcyc.json). Null, если ISS не вернул параметры NSS.</summary>
    Task<YieldCurveSnapshot?> GetYieldCurveAsync(CancellationToken ct = default);

    /// <summary>Поиск бумаги по ISIN — возвращает эмитента и тип для маппинга в сегмент. Null, если ISIN не найден.</summary>
    Task<MoexSecuritySearch?> GetSecuritySearchAsync(string isin, CancellationToken ct = default);

    /// <summary>
    /// Дневные исторические цены (history.json, plan/15 Часть A) за период [from; to] включительно.
    /// Пагинация обязательна — ISS отдаёт страницы по 100 строк с блоком <c>history.cursor</c>;
    /// дочитывается тем же паттерном, что <see cref="GetBondizationAsync"/>. Дни без сделок дают
    /// строку с <see cref="MoexHistoryPricePoint.ClosePricePercent"/> = null — forward fill остаётся
    /// на стороне потребителя (см. doc-comment <see cref="MoexHistoryPricePoint"/>). Пустой список,
    /// если источник недоступен/бумага не найдена (не бросает исключение).
    /// </summary>
    Task<IReadOnlyList<MoexHistoryPricePoint>> GetHistoryPricesAsync(string secid, DateOnly from, DateOnly to, CancellationToken ct = default);

    /// <summary>
    /// Снимок ВСЕЙ рыночной вселенной облигаций MOEX (задача 26 часть A) —
    /// <c>/iss/engines/stock/markets/bonds/securities.json?iss.only=securities,marketdata</c>.
    /// Пагинация через <c>start</c> — тот же паттерн, что <see cref="GetHistoryPricesAsync"/>;
    /// эмпирически этот эндпоинт ISS отдаёт весь рынок (~3000-3500 строк) одной страницей и
    /// игнорирует <c>start</c>/<c>limit</c>, но дочитывание оставлено на случай, если MOEX начнёт
    /// пагинировать (без него хвост тихо потерялся бы, тот же класс бага, что в bondization/history).
    /// Строки НЕ дедуплицированы по SECID — один SECID может встретиться на нескольких BOARDID,
    /// дедупликация (по максимальному обороту) — ответственность вызывающего кода
    /// (<see cref="MoexBondUniverseParser.DeduplicateByBoard"/>). Пустой список, если источник
    /// недоступен (не бросает исключение).
    /// </summary>
    Task<IReadOnlyList<MoexBondMarketRow>> GetBondMarketSnapshotAsync(CancellationToken ct = default);
}
