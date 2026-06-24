using System.Text.Json;
using Bonds.Core.Models;

namespace Bonds.Infrastructure.Connectors.Moex;

/// <summary>
/// Парсер ответа MOEX ISS bondization (купоны/амортизации/оферты), формат columns+data.
/// Чистая функция от JSON-строки к доменным моделям — без сетевых вызовов, поэтому
/// тестируется на сохранённых фикстурах (tests/Bonds.Tests/Fixtures/Moex).
/// </summary>
public static class MoexBondizationParser
{
    public static MoexBondizationResult Parse(string secid, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var coupons = ParseCoupons(root);
        var amortizations = ParseAmortizations(root);
        var offers = ParseOffers(root);

        var incomplete = DetectIncompleteCoupons(coupons);

        return new MoexBondizationResult
        {
            Secid = secid,
            Coupons = coupons,
            Amortizations = amortizations,
            Offers = offers,
            DataIncomplete = incomplete,
        };
    }

    private static List<CouponSchedule> ParseCoupons(JsonElement root)
    {
        var result = new List<CouponSchedule>();
        var table = IssTable.Parse(root, "coupons");
        if (table is null) return result;

        foreach (var row in table.Rows())
        {
            var couponDate = row.GetDateOnly("coupondate");
            if (couponDate is null) continue; // строка без даты купона бесполезна — пропускаем, не падаем

            // value_rub — абсолютный размер купона в рублях на номинал (предпочтительно над valueprc,
            // который выражен в % годовых и требует пересчёта). Если value_rub отсутствует (бывает для
            // будущих купонов флоатера, см. ниже), купон неизвестен -> IsKnown=false, ValueRub=null
            // (spec §4.4 "не подставлять нули молча").
            var valueRub = row.GetDecimal("value_rub") ?? row.GetDecimal("value");
            var isKnown = valueRub is not null;

            result.Add(new CouponSchedule
            {
                CouponDate = couponDate.Value,
                ValueRub = valueRub,
                PeriodDays = ComputePeriodDays(row),
                IsKnown = isKnown,
            });
        }

        return result.OrderBy(c => c.CouponDate).ToList();
    }

    private static int? ComputePeriodDays(IssRow row)
    {
        var start = row.GetDateOnly("startdate");
        var end = row.GetDateOnly("coupondate");
        if (start is null || end is null) return null;
        var days = end.Value.DayNumber - start.Value.DayNumber;
        return days > 0 ? days : null;
    }

    private static List<AmortizationSchedule> ParseAmortizations(JsonElement root)
    {
        var result = new List<AmortizationSchedule>();
        var table = IssTable.Parse(root, "amortizations");
        if (table is null) return result;

        foreach (var row in table.Rows())
        {
            var date = row.GetDateOnly("amortdate");
            var amount = row.GetDecimal("value_rub") ?? row.GetDecimal("value");
            if (date is null || amount is null) continue;

            // ISS включает в "amortizations" финальное погашение тела (data_source="maturity")
            // как отдельную строку с amount=100% номинала — это ожидаемо и не является амортизацией
            // в узком смысле, но мы сохраняем как есть: движок (этап 05) умеет отличать частичные
            // выплаты от финального погашения по сумме/доле, схема таблицы amortization_schedules
            // не делает этого разделения на этом этапе (см. plan/03).
            result.Add(new AmortizationSchedule
            {
                Date = date.Value,
                AmountRub = amount.Value,
            });
        }

        return result.OrderBy(a => a.Date).ToList();
    }

    private static List<OfferSchedule> ParseOffers(JsonElement root)
    {
        var result = new List<OfferSchedule>();
        var table = IssTable.Parse(root, "offers");
        if (table is null) return result;

        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

        foreach (var row in table.Rows())
        {
            var date = row.GetDateOnly("offerdate");
            if (date is null) continue;

            // ISS "offertype" приходит как русская строка ("Оферта", "Оферта/Погашение", "Call", ...).
            // Контракт не документирует строгий enum значений, поэтому маппим консервативно:
            // распознаём явный call по подстроке, всё остальное (в т.ч. "Оферта", "Put") — put,
            // что соответствует частому случаю на рынке РФ (put-оферты сильно превалируют над call).
            var offerTypeRaw = row.GetString("offertype") ?? string.Empty;
            var offerType = offerTypeRaw.Contains("call", StringComparison.OrdinalIgnoreCase)
                             || offerTypeRaw.Contains("колл", StringComparison.OrdinalIgnoreCase)
                ? OfferType.Call
                : OfferType.Put;

            result.Add(new OfferSchedule
            {
                Date = date.Value,
                OfferType = offerType,
                IsExecuted = date.Value < today,
            });
        }

        return result.OrderBy(o => o.Date).ToList();
    }

    /// <summary>
    /// Детектор неполноты купонного графика (spec §4.4, plan/04 Часть A п.5).
    /// Эвристика: ищем купон с известным значением (IsKnown), после которого следующий купон
    /// по дате отстоит более чем в ~1.6 раза от типичного (медианного) межкупонного интервала —
    /// это признак "дыры" в данных ISS (пропущенный купон), а не естественного редкого графика.
    /// Не флагуем единственный финальный разрыв, вызванный окончанием известного диапазона
    /// (флоатер/далёкий горизонт) — это нормальная отдельная история (IsKnown=false), а не неполнота.
    /// </summary>
    private static bool DetectIncompleteCoupons(List<CouponSchedule> coupons)
    {
        if (coupons.Count < 3) return false;

        var gaps = new List<int>();
        for (var i = 1; i < coupons.Count; i++)
        {
            gaps.Add(coupons[i].CouponDate.DayNumber - coupons[i - 1].CouponDate.DayNumber);
        }

        var sorted = gaps.OrderBy(g => g).ToList();
        var median = sorted[sorted.Count / 2];
        if (median <= 0) return false;

        // Пропуск считаем "дыркой", только если он между двумя ИЗВЕСТНЫМИ купонами (IsKnown) —
        // разрыв в хвосте после последнего известного купона флоатера — ожидаемое поведение,
        // не неполнота (см. summary).
        for (var i = 1; i < coupons.Count; i++)
        {
            if (!coupons[i - 1].IsKnown || !coupons[i].IsKnown) continue;
            var gap = coupons[i].CouponDate.DayNumber - coupons[i - 1].CouponDate.DayNumber;
            if (gap > median * 1.6)
            {
                return true;
            }
        }

        return false;
    }
}
