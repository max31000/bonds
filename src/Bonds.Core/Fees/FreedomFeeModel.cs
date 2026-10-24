namespace Bonds.Core.Fees;

/// <summary>Рынок исполнения для расчёта комиссии Freedom (ключи таблицы тарифов).</summary>
public enum FreedomMarket
{
    Usa,
    Europe,
    /// <summary>Казахстанский ITS (витрина иностранных бумаг), акции.</summary>
    KazakhstanIts,
    /// <summary>Казахстанский ITS, фонды (ETF) — отдельный класс бумаг у брокера.</summary>
    KazakhstanItsFunds,
    Kazakhstan,
    KazakhstanAix,
    Otc,
}

/// <summary>
/// Модель комиссий Freedom (Tradernet), чистая функция. Формула сверена по выписке до цента (все эталоны — точно, задача 67 #9):
/// <c>fee = round2(max(round2(pct × amountUsd) + perOrder + perUnit × qty, minTrade))</c> — процентная часть округляется до центов ДО сложения с фиксом.
/// <para>
/// ЕДИНИЦЫ: <c>pct</c> — ДОЛЯ суммы сделки (0.005 = 0.5%); <c>amountUsd</c>, <c>perOrder</c>, <c>perUnit</c>,
/// <c>minTrade</c> — доллары США; <c>qty</c> — штуки. Эталон на Python хранит проценты (0.5): граница
/// конверсии здесь — все ставки уже поделены на 100.
/// </para>
/// <para><c>perOrder</c> берётся ОДИН раз на заявку: если заявка исполнена двумя сделками, $1.2 списан
/// только в первой — для второй передавайте <c>includePerOrder: false</c>.</para>
/// </summary>
public static class FreedomFeeModel
{
    public const int InvestorTariffId = 1430;
    public const int StandardTariffId = 1431;

    private readonly record struct Schedule(decimal Pct, decimal PerOrderUsd, decimal PerUnitUsd, decimal MinTradeUsd);

    // Доли (0.005 = 0.5%). Investor: ITS-фонды = как ITS-акции (отдельной ставки в выписке не наблюдалось).
    private static readonly Dictionary<FreedomMarket, Schedule> Investor = new()
    {
        [FreedomMarket.Usa] = new(0.005m, 1.2m, 0.012m, 0m),
        [FreedomMarket.Europe] = new(0.005m, 1.2m, 0.012m, 0m),
        [FreedomMarket.KazakhstanIts] = new(0.005m, 0m, 0.012m, 0.01m),
        [FreedomMarket.KazakhstanItsFunds] = new(0.005m, 0m, 0.012m, 0.01m),
        [FreedomMarket.Kazakhstan] = new(0.00085m, 0m, 0m, 0m),
        [FreedomMarket.KazakhstanAix] = new(0.00085m, 0m, 0m, 0m),
        [FreedomMarket.Otc] = new(0.02m, 0m, 0m, 25m),
    };

    private static readonly Dictionary<FreedomMarket, Schedule> Standard = new()
    {
        [FreedomMarket.Usa] = new(0.0012m, 1.2m, 0m, 0m),
        [FreedomMarket.Europe] = new(0.0012m, 1.2m, 0m, 0m),
        [FreedomMarket.KazakhstanIts] = new(0.0012m, 0m, 0m, 0.01m),
        [FreedomMarket.KazakhstanItsFunds] = new(0.005m, 0m, 0m, 0m),
        [FreedomMarket.Kazakhstan] = new(0.00085m, 0m, 0m, 0m),
        [FreedomMarket.KazakhstanAix] = new(0.00085m, 0m, 0m, 0m),
        [FreedomMarket.Otc] = new(0.02m, 0m, 0m, 25m),
    };

    /// <summary>
    /// Маршрут по тикеру: <c>.EU</c> → Europe; <c>.US</c> → KazakhstanIts (фонды — ItsFunds), если у бумаги
    /// есть двойник на ITS (<paramref name="itsTwin"/>), иначе Usa; <c>.KZ</c> → Kazakhstan (<c>.AIX.</c> → Aix);
    /// <c>.ITS</c> → ITS; прочее → Usa.
    /// </summary>
    public static FreedomMarket RouteFor(string ticker, bool itsTwin, bool isFund)
    {
        var t = ticker.Trim().ToUpperInvariant();
        var its = isFund ? FreedomMarket.KazakhstanItsFunds : FreedomMarket.KazakhstanIts;
        if (t.EndsWith(".ITS", StringComparison.Ordinal)) return its;
        if (t.EndsWith(".EU", StringComparison.Ordinal)) return FreedomMarket.Europe;
        if (t.EndsWith(".KZ", StringComparison.Ordinal))
        {
            return t.Contains(".AIX.", StringComparison.Ordinal) ? FreedomMarket.KazakhstanAix : FreedomMarket.Kazakhstan;
        }

        if (t.EndsWith(".US", StringComparison.Ordinal)) return itsTwin ? its : FreedomMarket.Usa;
        return FreedomMarket.Usa;
    }

    /// <summary>Комиссия в USD, округлённая до центов (как у брокера; процентная часть — до центов отдельно, см. класс). Сверка с выпиской — точная.</summary>
    /// <exception cref="NotSupportedException">Тариф не из <see cref="InvestorTariffId"/>/<see cref="StandardTariffId"/>.</exception>
    public static decimal Fee(int tariffId, FreedomMarket market, decimal amountUsd, decimal qty, bool includePerOrder = true)
    {
        var table = tariffId switch
        {
            InvestorTariffId => Investor,
            StandardTariffId => Standard,
            _ => throw new NotSupportedException($"Тариф {tariffId} не поддержан моделью комиссий."),
        };
        var s = table[market];
        // Задача 67 (#9): процентная часть округляется до центов ДО сложения с фиксом за заявку и за штуку (так считает брокер — все эталоны выписки сходятся
        // до цента, без допуска: COPX 0.7012 → 0.70 + 1.2 + 0.024 = 1.924 → 1.92, а не 1.9252 → 1.93).
        var pct = Math.Round(s.Pct * Math.Abs(amountUsd), 2, MidpointRounding.AwayFromZero);
        var raw = pct + (includePerOrder ? s.PerOrderUsd : 0m) + s.PerUnitUsd * Math.Abs(qty);
        return Math.Round(Math.Max(raw, s.MinTradeUsd), 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>Точка безубыточности для ETF на Standard: ITS-фонды 0.5% = Europe 0.12% + $1.2 → ≈ $315.79.</summary>
    public static decimal BreakEvenAmountUsd =>
        Standard[FreedomMarket.Europe].PerOrderUsd / (Standard[FreedomMarket.KazakhstanItsFunds].Pct - Standard[FreedomMarket.Europe].Pct);

    /// <summary>
    /// Правило доливки ETF (тариф Standard): что дешевле для суммы сделки в USD — ITS-фонды (0.5%) или Europe
    /// (0.12% + $1.2). До <see cref="BreakEvenAmountUsd"/> выгоднее ITS, дальше — Europe; на равенстве — ITS.
    /// </summary>
    public static FreedomMarket CheaperRoute(decimal amountUsd) =>
        amountUsd <= BreakEvenAmountUsd ? FreedomMarket.KazakhstanItsFunds : FreedomMarket.Europe;
}
