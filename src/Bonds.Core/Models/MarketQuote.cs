namespace Bonds.Core.Models;

/// <summary>
/// Временной ряд рыночных котировок инструмента. Текущая цена/НКД по открытым позициям
/// приоритетно берутся из T-Invest при синке; историческое/справочное — из MOEX
/// (см. plan/00 §4 "Разделение источников данных"). Снимок расчётных метрик (YTM, дюрация
/// и т.д.) сюда не входит на этом этапе — он появится вместе с Calculation Engine (этап 05)
/// и будет храниться отдельно, чтобы не смешивать сырые рыночные данные с производными.
/// <para>
/// <b>Стакан (bid/ask) сюда намеренно не входит.</b> T-Invest отдаёт лучшие цены спроса/
/// предложения (<c>ITInvestPortfolioClient.GetQuotesAsync</c> → <c>TInvestQuote.BestBid/BestAsk</c>),
/// но они не персистируются — спека §8 относит предупреждение о низкой ликвидности к категории
/// "на будущее" (вне MVP). См. doc-comment в <c>BondSyncService</c> на месте, где стакан
/// запрашивается, но не пишется. Если этапу 07 (сигналы) потребуется именно историческая
/// персистентность стакана (не только "сейчас" из живого вызова) — добавить миграцию с
/// полями BidPrice/AskPrice тогда, не сейчас.
/// </para>
/// </summary>
public class MarketQuote
{
    public ulong Id { get; set; }
    public ulong InstrumentId { get; set; }

    public DateOnly AsOf { get; set; }

    /// <summary>
    /// Чистая цена, В РУБЛЯХ за одну облигацию (номинал, не лот) — НИКОГДА не пункты/% от
    /// номинала. Оба источника отдают цену в пунктах (T-Invest marketdata <c>GetLastPrices</c> →
    /// <c>TInvestQuote.LastPrice</c>; MOEX securities.json <c>PREVPRICE</c>/<c>PREVWAPRICE</c> →
    /// <see cref="Connectors.Moex.MoexSecurityInfo.PrevPrice"/>) — конвертация в рубли
    /// (пункты / 100 × номинал инструмента) обязана произойти на стороне писателя ДО записи сюда
    /// (см. <c>Bonds.Infrastructure.Quotes.LiveQuoteConverter</c>, <c>WatchlistSyncService</c>).
    /// Единственное исключение, не требующее конверсии, — T-Invest <c>GetPortfolio.CurrentPrice</c>
    /// (<see cref="Connectors.TInvest.TInvestPortfolioPosition.CurrentPrice"/>), который брокер
    /// уже отдаёт в рублях за бумагу. Путаница этих единиц была причиной продакшн-бага занижения
    /// стоимости портфеля в разы — при добавлении новых писателей этого поля свериться с этим
    /// контрактом обязательно.
    /// </summary>
    public decimal? CleanPrice { get; set; }

    /// <summary>Грязная цена (чистая цена + НКД), В РУБЛЯХ за одну облигацию — тот же контракт единиц, что у <see cref="CleanPrice"/>.</summary>
    public decimal? DirtyPrice { get; set; }

    /// <summary>НКД на дату, В РУБЛЯХ за одну облигацию (если известен от источника; иначе считается движком).</summary>
    public decimal? Accrued { get; set; }

    /// <summary>Объём торгов за день (ликвидность).</summary>
    public decimal? Volume { get; set; }

    public MarketQuoteSource Source { get; set; }

    public DateTime CreatedAt { get; set; }
}

public enum MarketQuoteSource
{
    TInvest,
    Moex,
}
