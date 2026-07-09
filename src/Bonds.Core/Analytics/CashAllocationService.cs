namespace Bonds.Core.Analytics;

/// <summary>
/// Задача 34 часть B.3 — какую ось концентрации применяет <see cref="CashAllocationService.Allocate"/>.
/// Issuer в банке (<c>bond_universe</c>) отсутствует (задача 34 doc-comment
/// <see cref="CashAllocationCandidate.Sector"/>) — для рыночных источников (market/recommended)
/// нужна альтернативная ось, а "market" по решению владельца (см. plan/34 часть B.3 NEEDS DECISION,
/// принято: "market" — честный greedy по доходности) вовсе без лимита концентрации.
/// </summary>
public enum CashAllocationConcentrationAxis
{
    /// <summary>Дефолт — лимит доли ЭМИТЕНТА (<see cref="CashAllocationCandidate.Issuer"/>/
    /// <see cref="CashAllocationCandidate.CurrentIssuerMarketValueRub"/>), source=portfolio, прежнее
    /// поведение до задачи 34 без изменений.</summary>
    Issuer,

    /// <summary>Лимит доли СЕКТОРА (<see cref="CashAllocationCandidate.Sector"/>) — source=recommended
    /// (задача 34). Требует <c>maxSectorSharePercent</c> в <see cref="CashAllocationService.Allocate"/>.
    /// В отличие от issuer-оси, "текущая доля сектора в портфеле" НЕ учитывается (на банк-слое нет
    /// данных о секторной композиции уже удерживаемых позиций) — лимит ограничивает распределение
    /// ТОЛЬКО вносимых денег между секторами кандидатов, не всего портфеля.</summary>
    Sector,

    /// <summary>Без лимита концентрации вовсе — source=market (задача 34, принятый дефолт NEEDS
    /// DECISION plan/34 часть B.3): "весь рынок" должен честно показывать самые доходные бумаги без
    /// диверсификационных искажений; диверсификация в этом режиме — только через
    /// <c>maxLotsPerCandidate</c> (не более 1 лота на кандидата).</summary>
    None,
}

/// <summary>
/// «Куда вложить сумму» (plan/17 §B, задача 34 §B — источники кандидатов). Жадное распределение
/// свободных денег между кандидатами: для source=portfolio — текущими позициями портфеля (НЕ
/// скринер по всей вселенной бумаг — та же граница MVP, что у <see cref="SwitchAnalysisService"/>,
/// spec §3 «Вне скоупа»); для source=market/recommended (задача 34) — кандидатами всей
/// фикс-купонной вселенной банка <c>bond_universe</c> (вызывающий эндпоинт строит
/// <see cref="CashAllocationCandidate"/> из банк-записей, сервис не знает о происхождении
/// кандидата — тот же принцип, что <see cref="CashAllocationCandidate.IsComparable"/>). Кандидаты
/// сортируются по убыванию эффективной доходности (YTM либо текущая купонная доходность для
/// флоатера/индексируемой — та же логика выбора, что в <see cref="PositionComparisonService"/>), и
/// деньги идут в бумагу с максимальной доходностью, пока не упрутся в активную ось концентрации
/// (<see cref="CashAllocationConcentrationAxis"/>), в лимит лотов на кандидата
/// (<c>maxLotsPerCandidate</c>) или в остаток денег меньше цены одной бумаги. Чистый сервис, без I/O.
/// </summary>
public static class CashAllocationService
{
    /// <summary>Дефолтный лимит доли одного эмитента в портфеле после докупки (25% — тот же ориентир, что
    /// <c>SignalEngineOptions.DefaultMaxConcentrationPercent</c>; здесь отдельная константа, т.к. сервис
    /// не имеет доступа к Signals-слою, но значение намеренно совпадает).</summary>
    public const decimal DefaultMaxIssuerSharePercent = 25m;

    /// <summary>
    /// Задача 34 часть B.3 — дефолтный лимит доли СЕКТОРА для source=recommended
    /// (<see cref="CashAllocationConcentrationAxis.Sector"/>). 35% — выбрано в согласованном
    /// владельцем диапазоне 30-40% (plan/34 NEEDS DECISION), ближе к верхней границе: банк
    /// классифицирует сектор ГРУБО, всего 3 значения (<c>BondUniverseSectorMapper</c>:
    /// "Гособлигации"/"Муниципальные"/"Корпоративные", подавляющее большинство корпоративных бумаг
    /// попадает в один общий бакет) — прямое переиспользование issuer-лимита (25%, рассчитанного на
    /// десятки/сотни отдельных эмитентов) было бы слишком тесным для всего в 3 бакета и почти всегда
    /// резало бы "Корпоративные" даже при разумной докупке. 35% (чуть выше равного деления 1/3 ≈
    /// 33.3%) не даёт жадному проходу полностью уйти в один бакет, но оставляет разумный перекос к
    /// самому доходному сектору.
    /// </summary>
    public const decimal DefaultMaxSectorSharePercent = 35m;

    public const string Disclaimer =
        "Оценка распределения свободных средств по бумагам текущего портфеля (не скринер по всей " +
        "вселенной бумаг — сравниваются только позиции, которые уже есть в портфеле). Не учитывает " +
        "налоги и не является индивидуальной инвестиционной рекомендацией. Доходность — аналитическая " +
        "оценка (YTM либо текущая купонная доходность для флоатера/индексируемой бумаги), не гарантирована.";

    /// <summary>
    /// Распределяет <paramref name="amountRub"/> между <paramref name="candidates"/>.
    /// Алгоритм: сортировка кандидатов по убыванию <see cref="CashAllocationCandidate.EffectiveYield"/>
    /// (кандидаты без доходности — в конец, помечаются пропуском «нет доходности», не участвуют в
    /// распределении); для каждого по очереди докупаем максимум лотов, пока:
    /// <list type="bullet">
    /// <item>не упрёмся в <paramref name="maxLotsPerCandidate"/> (задача 34 — диверсификация "не
    /// более N лотов на кандидата"; null — без ограничения, прежнее поведение);</item>
    /// <item>не упрёмся в активную ось концентрации <paramref name="concentrationAxis"/>: доля
    /// эмитента (<paramref name="maxIssuerSharePercent"/>, дефолт — прежнее поведение source=portfolio)
    /// либо доля сектора (<paramref name="maxSectorSharePercent"/>, задача 34 — source=recommended)
    /// либо совсем без лимита (<see cref="CashAllocationConcentrationAxis.None"/> — source=market,
    /// задача 34). База лимита РАЗНАЯ для двух осей: Issuer делит на текущую рыночную стоимость
    /// ВСЕГО портфеля + вносимая сумма (растёт по мере того, как деньги физически входят в
    /// портфель, иначе лимит считался бы от "старой" базы и позволял бы перекос сильнее заявленного);
    /// Sector делит на ФИКСИРОВАННЫЙ <paramref name="amountRub"/> (весь бюджет распределения) — у
    /// сектора нет содержательной "текущей доли в портфеле" (банк не знает секторную композицию уже
    /// удерживаемых позиций), а деление на растущую с нуля базу сделало бы лимит бесполезным (первый
    /// же купленный лот любого сектора математически давал бы долю 100% и блокировался бы любым
    /// лимитом &lt;100%);</item>
    /// <item>не кончатся деньги (остаток меньше цены ещё одного лота).</item>
    /// </list>
    /// Если ни одного лота купить не удалось: для оси Issuer (обратная совместимость source=portfolio,
    /// поведение НЕ менялось задачей 34) причина всегда <see cref="CashAllocationSkipReason.ConcentrationLimit"/>
    /// — так было исторически, даже когда истинная причина "не хватило денег на первый лот"; для
    /// осей Sector/None (задача 34, новый код, ничьё поведение не ломает) причина различается честно:
    /// <see cref="CashAllocationSkipReason.InsufficientFunds"/>, если денег не хватило бы даже без
    /// учёта лимита концентрации, иначе <see cref="CashAllocationSkipReason.ConcentrationLimit"/>.
    /// </summary>
    public static CashAllocationResult Allocate(
        decimal amountRub,
        IReadOnlyList<CashAllocationCandidate> candidates,
        decimal currentPortfolioValueRub,
        decimal maxIssuerSharePercent = DefaultMaxIssuerSharePercent,
        CashAllocationConcentrationAxis concentrationAxis = CashAllocationConcentrationAxis.Issuer,
        decimal? maxSectorSharePercent = null,
        int? maxLotsPerCandidate = null)
    {
        if (amountRub <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(amountRub), "Сумма должна быть положительной.");
        }

        if (concentrationAxis == CashAllocationConcentrationAxis.Sector && maxSectorSharePercent is null)
        {
            throw new ArgumentException(
                "maxSectorSharePercent обязателен при concentrationAxis=Sector.", nameof(maxSectorSharePercent));
        }

        var allocations = new List<CashAllocationLine>();
        var skipped = new List<CashAllocationSkip>();
        var leftover = amountRub;

        // Итоговая база для лимита концентрации растёт по мере того, как деньги физически входят
        // в портфель (иначе лимит считался бы от "старой" базы и позволял бы перекос сильнее заявленного).
        var portfolioValueAfterAllocation = currentPortfolioValueRub;

        var bucketValueAfterAllocation = concentrationAxis switch
        {
            // Sector: своей "текущей" величины на банк-слое нет (см. doc-comment метода) — бакеты
            // стартуют с нуля, растут только за счёт новой докупки.
            CashAllocationConcentrationAxis.Sector => candidates
                .Where(c => c.Sector is not null)
                .GroupBy(c => c.Sector!)
                .ToDictionary(g => g.Key, _ => 0m),
            CashAllocationConcentrationAxis.None => new Dictionary<string, decimal>(),
            _ => candidates
                .Where(c => c.Issuer is not null)
                .GroupBy(c => c.Issuer!)
                .ToDictionary(g => g.Key, g => g.Sum(c => c.CurrentIssuerMarketValueRub)),
        };

        var ordered = candidates
            .OrderByDescending(c => c.EffectiveYield.HasValue)
            .ThenByDescending(c => c.EffectiveYield)
            .ToList();

        foreach (var candidate in ordered)
        {
            // Задача 31 часть B.3: гейт сравнимости — ПЕРЕД гейтом доходности. Сервис по-прежнему не
            // знает про IsFloater как таковой (см. doc-comment CashAllocationCandidate.IsComparable/
            // тест Allocate_FloaterWithoutYtm...) — просто получает уже посчитанный вызывающим слоем
            // булев флаг (тот же паттерн, что EffectiveYield уже готовый CurrentYield для флоатера).
            // Без этого гейта флоатер с высоким CurrentYield обходил бы фикс-купонные бумаги по
            // NoYield-проверке (у него ЕСТЬ доходность — просто несравнимая).
            if (!candidate.IsComparable)
            {
                skipped.Add(new CashAllocationSkip
                {
                    InstrumentId = candidate.InstrumentId,
                    Secid = candidate.Secid,
                    Name = candidate.Name,
                    Issuer = candidate.Issuer,
                    Reason = CashAllocationSkipReason.NotComparable,
                });
                continue;
            }

            if (candidate.EffectiveYield is null)
            {
                skipped.Add(new CashAllocationSkip
                {
                    InstrumentId = candidate.InstrumentId,
                    Secid = candidate.Secid,
                    Name = candidate.Name,
                    Issuer = candidate.Issuer,
                    Reason = CashAllocationSkipReason.NoYield,
                });
                continue;
            }

            if (candidate.PricePerLotRub <= 0m)
            {
                skipped.Add(new CashAllocationSkip
                {
                    InstrumentId = candidate.InstrumentId,
                    Secid = candidate.Secid,
                    Name = candidate.Name,
                    Issuer = candidate.Issuer,
                    Reason = CashAllocationSkipReason.NoPrice,
                });
                continue;
            }

            // Задача 34: ключ бакета зависит от активной оси — Sector/None читают Sector/ничего,
            // Issuer (прежнее поведение) — Issuer с fallback на InstrumentId, если у кандидата нет
            // эмитента (тот же fallback, что и до задачи 34; InstrumentId для этой оси всегда не
            // null — источник portfolio всегда его проставляет, см. doc-comment CashAllocationCandidate.InstrumentId).
            string? bucketKey = concentrationAxis switch
            {
                CashAllocationConcentrationAxis.Sector => candidate.Sector,
                CashAllocationConcentrationAxis.None => null,
                _ => candidate.Issuer ?? $"__instrument_{candidate.InstrumentId}",
            };

            var effectiveMaxSharePercent = concentrationAxis == CashAllocationConcentrationAxis.Sector
                ? maxSectorSharePercent!.Value
                : maxIssuerSharePercent;

            var bucketValue = bucketKey is not null && bucketValueAfterAllocation.TryGetValue(bucketKey, out var v) ? v : 0m;

            var quantity = 0m;
            var costRub = 0m;
            var lotsBoughtCount = 0;

            while (true)
            {
                if (maxLotsPerCandidate is { } maxLots && lotsBoughtCount >= maxLots) break; // задача 34: не более N лотов на кандидата

                var nextCostRub = costRub + candidate.PricePerLotRub;
                if (nextCostRub > leftover) break; // не хватает денег на ещё один лот

                var newPortfolioValue = portfolioValueAfterAllocation + candidate.PricePerLotRub;

                if (bucketKey is not null)
                {
                    var newBucketValue = bucketValue + costRub + candidate.PricePerLotRub;

                    // Задача 34: ось Sector делит на ФИКСИРОВАННЫЙ amountRub (весь бюджет
                    // распределения), а не на растущую базу newPortfolioValue, как ось Issuer.
                    // Причина: у Issuer есть содержательная "текущая стоимость портфеля" — база
                    // изначально большая и растёт только за вносимые деньги (реалистичное
                    // "после докупки"). У Sector такой базы нет (currentPortfolioValueRub=0 для
                    // market/recommended, см. вызывающий код) — если делить на растущую базу с
                    // нуля, ПЕРВЫЙ ЖЕ купленный лот любого сектора математически даёт долю 100%
                    // (это единственное, что куплено на тот момент) и блокируется любым лимитом
                    // &lt;100%, что делает ось бесполезной. Деление на amountRub — "какая доля
                    // ВСЕГО вносимого бюджета ушла в этот сектор" — не зависит от порядка покупок.
                    var denominator = concentrationAxis == CashAllocationConcentrationAxis.Sector
                        ? amountRub
                        : newPortfolioValue;
                    var newSharePercent = denominator > 0m
                        ? newBucketValue / denominator * 100m
                        : 0m;

                    if (newSharePercent > effectiveMaxSharePercent) break; // лимит концентрации
                }

                costRub = nextCostRub;
                quantity += candidate.LotSize;
                lotsBoughtCount++;
                portfolioValueAfterAllocation = newPortfolioValue;
            }

            if (quantity > 0m)
            {
                leftover -= costRub;
                if (bucketKey is not null)
                {
                    bucketValueAfterAllocation[bucketKey] = bucketValue + costRub;
                }

                // Задача 24: разложение costRub (вся докупка) на компоненты — тот же множитель
                // "число купленных лотов", что и у costRub относительно PricePerLotRub.
                var lotsBought = candidate.LotSize > 0m ? quantity / candidate.LotSize : 0m;

                allocations.Add(new CashAllocationLine
                {
                    InstrumentId = candidate.InstrumentId,
                    Secid = candidate.Secid,
                    Name = candidate.Name,
                    Issuer = candidate.Issuer,
                    Sector = candidate.Sector,
                    Quantity = quantity,
                    EstimatedCostRub = costRub,
                    EffectiveYield = candidate.EffectiveYield.Value,
                    LotSizeAssumed = candidate.LotSizeIsAssumed,
                    CleanCostRub = candidate.CleanPriceRub * lotsBought,
                    AccruedCostRub = candidate.AccruedRub * lotsBought,
                    CommissionCostRub = candidate.CommissionRub * lotsBought,
                });
            }
            else
            {
                // Задача 34: ось Issuer сохраняет прежнее поведение (всегда ConcentrationLimit, см.
                // doc-comment метода) для обратной совместимости source=portfolio; оси Sector/None —
                // новый код, честно различает "не хватило бы денег даже без лимита" от "лимит
                // реально заблокировал покупку".
                var reason = concentrationAxis == CashAllocationConcentrationAxis.Issuer
                    ? CashAllocationSkipReason.ConcentrationLimit
                    : candidate.PricePerLotRub > leftover
                        ? CashAllocationSkipReason.InsufficientFunds
                        : CashAllocationSkipReason.ConcentrationLimit;

                skipped.Add(new CashAllocationSkip
                {
                    InstrumentId = candidate.InstrumentId,
                    Secid = candidate.Secid,
                    Name = candidate.Name,
                    Issuer = candidate.Issuer,
                    Reason = reason,
                });
            }
        }

        return new CashAllocationResult
        {
            AmountRub = amountRub,
            Allocations = allocations,
            Skipped = skipped,
            LeftoverRub = leftover,
            Disclaimer = Disclaimer,
        };
    }
}

/// <summary>
/// Один кандидат на докупку — для source=portfolio (plan/17 §B) текущая позиция портфеля, для
/// source=market/recommended (задача 34) — запись <c>bond_universe</c> банка. Сервис не знает,
/// откуда взялся кандидат (тот же принцип, что <see cref="IsComparable"/>).
/// </summary>
public sealed record CashAllocationCandidate
{
    /// <summary>
    /// Реальный <c>Instrument.Id</c> — для source=portfolio (в т.ч. watchlist-кандидаты) ВСЕГДА не
    /// null (holdings строятся из реальных позиций/материализованных инструментов). Для
    /// source=market/recommended (задача 34) — null: банк-кандидат из <c>bond_universe</c> не связан
    /// с таблицей Instrument (материализация каждого кандидата через
    /// <c>POST /api/universe/{secid}/materialize</c> намеренно НЕ вызывается — план явно запрещает
    /// "материализовывать вселенную через движок"; тот же принцип, что уже применён в
    /// <c>ReplacementCandidateDto</c>, который по той же причине несёт Secid/Isin, а не InstrumentId).
    /// Идентификатор для рыночных кандидатов — <see cref="Secid"/>.
    /// </summary>
    public required ulong? InstrumentId { get; init; }

    /// <summary>Задача 34: биржевой идентификатор MOEX (SECID) — только для source=market/recommended
    /// (идентификатор рыночного кандидата, см. <see cref="InstrumentId"/>). Null для source=portfolio.</summary>
    public string? Secid { get; init; }

    public string? Name { get; init; }

    /// <summary>Эмитент — только для source=portfolio (<c>bond_universe</c> банка эмитента не хранит,
    /// см. <see cref="Sector"/>). Null для source=market/recommended (не выдумывать эмитента из
    /// имени — см. doc-comment plan/34).</summary>
    public string? Issuer { get; init; }

    /// <summary>Задача 34: грубая секторная классификация банка (<c>BondUniverseEntry.Sector</c> —
    /// "Гособлигации"/"Муниципальные"/"Корпоративные", НЕ то же самое, что <c>Instrument.Sector</c>)
    /// — альтернативная ось концентрации для source=market/recommended, когда issuer недоступен (см.
    /// <see cref="CashAllocationConcentrationAxis.Sector"/>). Null для source=portfolio (issuer-ветка
    /// не использует Sector).</summary>
    public string? Sector { get; init; }

    /// <summary>YTM либо CurrentYield (для флоатера/индексируемой) — см. <see cref="PositionComparisonService"/>. Null — доходность не определена, кандидат пропускается.</summary>
    public decimal? EffectiveYield { get; init; }

    /// <summary>Грязная цена (цена + НКД) одного лота с учётом комиссии покупки (<see cref="SwitchAnalysisService.DefaultCommissionRate"/> либо переопределённая ставка) — сколько рублей стоит купить <see cref="LotSize"/> штук.</summary>
    public required decimal PricePerLotRub { get; init; }

    /// <summary>
    /// Задача 24: разложение <see cref="PricePerLotRub"/> на компоненты (чистая цена + НКД + комиссия)
    /// для одного лота — только для отображения в UI («купить N шт × цена (чистая + НКД + комиссия)»),
    /// не участвует в расчёте распределения (тот использует только <see cref="PricePerLotRub"/>).
    /// Сумма CleanPriceRub + AccruedRub + CommissionRub должна равняться PricePerLotRub.
    /// </summary>
    public decimal CleanPriceRub { get; init; }

    /// <summary>Задача 24: НКД в составе цены одного лота — см. <see cref="CleanPriceRub"/>.</summary>
    public decimal AccruedRub { get; init; }

    /// <summary>Задача 24: комиссия покупки в составе цены одного лота — см. <see cref="CleanPriceRub"/>.</summary>
    public decimal CommissionRub { get; init; }

    /// <summary>Размер лота в штуках. У большинства корп. облигаций 1 — если источник (Instrument/T-Invest) не отдаёт лот, берётся 1 и помечается <see cref="LotSizeIsAssumed"/>.</summary>
    public required decimal LotSize { get; init; }

    /// <summary>true — <see cref="LotSize"/> не взят из данных инструмента, а принят равным 1 по умолчанию (см. doc-comment сервиса/плана).</summary>
    public bool LotSizeIsAssumed { get; init; }

    /// <summary>Текущая рыночная стоимость позиций этого эмитента в портфеле (для базы лимита концентрации). 0, если эмитента ещё нет в портфеле.</summary>
    public required decimal CurrentIssuerMarketValueRub { get; init; }

    /// <summary>
    /// Задача 31 часть B.3: true — доходность кандидата сравнима с YTM обычной фикс-купонной
    /// бумаги (флоатер/индексируемая — не сравнимы, их "доходность" — CurrentYield, не YTM).
    /// Вызывающий слой (эндпоинт) обязан проставить флаг; дефолт <c>true</c> — существующие
    /// вызовы без явного значения (в т.ч. старые тесты) продолжают работать как раньше.
    /// <c>false</c> → кандидат пропускается с <see cref="CashAllocationSkipReason.NotComparable"/>
    /// ДО проверки <see cref="EffectiveYield"/> (у флоатера ЕСТЬ доходность — CurrentYield, — но
    /// она не сравнима с YTM фикс-купона, поэтому не должна ранжироваться вместе с ним).
    /// <para>
    /// Не обязательно совпадает с полным <see cref="ReplacementMatrixService.IsComparable"/>
    /// (тем же, что использует матрица замен/RV/PostReplacement) — GetAllocation намеренно НЕ
    /// включает сюда DataIncomplete: бумага с неполными данными и без цены и так естественно
    /// получает <see cref="EffectiveYield"/>=null и уходит в существующий
    /// <see cref="CashAllocationSkipReason.NoYield"/> (см. doc-comment вызывающего кода в
    /// AnalyticsEndpoints.GetAllocation).
    /// </para>
    /// </summary>
    public bool IsComparable { get; init; } = true;
}

/// <summary>Итог распределения суммы — что купить, остаток, и что пропущено (spec §9, обязательный дисклеймер).</summary>
public sealed record CashAllocationResult
{
    public required decimal AmountRub { get; init; }
    public required IReadOnlyList<CashAllocationLine> Allocations { get; init; }
    public required IReadOnlyList<CashAllocationSkip> Skipped { get; init; }
    public required decimal LeftoverRub { get; init; }
    public required string Disclaimer { get; init; }
}

/// <summary>Одна строка распределения — сколько купить конкретной бумаги.</summary>
public sealed record CashAllocationLine
{
    /// <summary>Null для source=market/recommended — см. doc-comment <see cref="CashAllocationCandidate.InstrumentId"/>. Идентификатор для этих строк — <see cref="Secid"/>.</summary>
    public required ulong? InstrumentId { get; init; }

    /// <summary>Задача 34: см. <see cref="CashAllocationCandidate.Secid"/>.</summary>
    public string? Secid { get; init; }

    public string? Name { get; init; }
    public string? Issuer { get; init; }

    /// <summary>Задача 34: см. <see cref="CashAllocationCandidate.Sector"/>.</summary>
    public string? Sector { get; init; }

    public required decimal Quantity { get; init; }
    public required decimal EstimatedCostRub { get; init; }
    public required decimal EffectiveYield { get; init; }

    /// <summary>true — размер лота принят равным 1 по умолчанию (источник не предоставил лот).</summary>
    public required bool LotSizeAssumed { get; init; }

    /// <summary>
    /// Задача 24: разложение EstimatedCostRub на всю докупку (не на 1 лот) — чистая цена всех купленных
    /// бумаг. CleanCostRub + AccruedCostRub + CommissionCostRub = EstimatedCostRub (с точностью до
    /// округления). 0, если у кандидата не было разложения (см. <see cref="CashAllocationCandidate.CleanPriceRub"/>).
    /// </summary>
    public decimal CleanCostRub { get; init; }

    /// <summary>Задача 24: НКД в составе EstimatedCostRub — см. <see cref="CleanCostRub"/>.</summary>
    public decimal AccruedCostRub { get; init; }

    /// <summary>Задача 24: комиссия покупки в составе EstimatedCostRub — см. <see cref="CleanCostRub"/>.</summary>
    public decimal CommissionCostRub { get; init; }
}

/// <summary>Почему кандидат пропущен и не получил докупку.</summary>
public enum CashAllocationSkipReason
{
    /// <summary>Нет применимой доходности (YTM не сошёлся и не флоатер/индексируемая с CurrentYield).</summary>
    NoYield,

    /// <summary>
    /// Активная ось концентрации (эмитент/сектор, <see cref="CashAllocationConcentrationAxis"/>) не
    /// позволяет купить ни одного лота. Для оси Issuer (source=portfolio, поведение до задачи 34 не
    /// менялось) — универсальная причина для ЛЮБОГО нулевого исхода, в т.ч. когда денег не хватило бы
    /// даже без лимита (историческое упрощение). Для осей Sector/None (задача 34) она отличается от
    /// <see cref="InsufficientFunds"/> честно (см. doc-comment <see cref="CashAllocationService.Allocate"/>).
    /// </summary>
    ConcentrationLimit,

    /// <summary>Цена лота не определена (нет котировки).</summary>
    NoPrice,

    /// <summary>
    /// Задача 31 часть B.3: флоатер/индексируемая/бумага с неполными данными — доходность не
    /// сравнима с YTM фикс-купона (см. <see cref="CashAllocationCandidate.IsComparable"/> /
    /// <see cref="ReplacementMatrixService.IsComparable"/>), поэтому исключена из аллокации, даже
    /// если у неё формально есть высокая CurrentYield.
    /// </summary>
    NotComparable,

    /// <summary>
    /// Задача 34 — только для осей Sector/None (source=market/recommended): остатка денег не хватило
    /// бы на 1 лот кандидата ДАЖЕ без учёта лимита концентрации (для оси None лимита концентрации
    /// вообще нет — это единственная возможная причина нулевого исхода). Ось Issuer
    /// (source=portfolio) этой причиной не пользуется — см. doc-comment <see cref="ConcentrationLimit"/>.
    /// </summary>
    InsufficientFunds,
}

/// <summary>Кандидат, не получивший докупку, и причина.</summary>
public sealed record CashAllocationSkip
{
    /// <summary>Null для source=market/recommended — см. doc-comment <see cref="CashAllocationCandidate.InstrumentId"/>.</summary>
    public required ulong? InstrumentId { get; init; }

    /// <summary>Задача 34: см. <see cref="CashAllocationCandidate.Secid"/>.</summary>
    public string? Secid { get; init; }

    public string? Name { get; init; }
    public string? Issuer { get; init; }
    public required CashAllocationSkipReason Reason { get; init; }
}
