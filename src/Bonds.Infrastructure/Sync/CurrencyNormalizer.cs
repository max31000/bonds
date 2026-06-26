namespace Bonds.Infrastructure.Sync;

/// <summary>
/// Нормализация валюты номинала из MOEX <c>FACEUNIT</c> (T-2/N-2). Чистая функция, чтобы её
/// можно было покрыть тестами без мока всего синка. SUR/RUR — устаревшие коды рубля на MOEX,
/// нормализуются в RUB (в рублёвом скоупе MVP). Любая иная валюта (USD/EUR/…) возвращается как
/// есть и помечается вне рублёвого контура — такие бумаги не считаются как рублёвые (spec §11/§3).
/// </summary>
public static class CurrencyNormalizer
{
    public static (string Currency, bool IsOutOfScope) Normalize(string? faceUnit)
    {
        if (string.IsNullOrWhiteSpace(faceUnit)) return ("RUB", false);

        var cur = faceUnit.Trim().ToUpperInvariant();
        return cur is "SUR" or "RUB" or "RUR"
            ? ("RUB", false)
            : (cur, true);
    }
}
