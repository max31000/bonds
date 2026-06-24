namespace Bonds.Core.Models;

/// <summary>
/// Временной ряд рыночных котировок инструмента. Текущая цена/НКД по открытым позициям
/// приоритетно берутся из T-Invest при синке; историческое/справочное — из MOEX
/// (см. plan/00 §4 "Разделение источников данных"). Снимок расчётных метрик (YTM, дюрация
/// и т.д.) сюда не входит на этом этапе — он появится вместе с Calculation Engine (этап 05)
/// и будет храниться отдельно, чтобы не смешивать сырые рыночные данные с производными.
/// </summary>
public class MarketQuote
{
    public ulong Id { get; set; }
    public ulong InstrumentId { get; set; }

    public DateOnly AsOf { get; set; }

    public decimal? CleanPrice { get; set; }
    public decimal? DirtyPrice { get; set; }

    /// <summary>НКД на дату (если известен от источника; иначе считается движком).</summary>
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
