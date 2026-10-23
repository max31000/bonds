namespace Bonds.Api.Endpoints.Dtos;

// Контракты /api/baskets (задача 62). ЕДИНИЦЫ: targetWeight/band/hardBand/weight/target/drift/shareOfTotal/subWeight/actualSubShare/targetsSum —
// ДОЛИ (0.15 = 15%), проценты рисует только фронт; value/deficitToTarget/freeCash/minTrade/totalValue/baseValue — в ВАЛЮТЕ ПОРТФЕЛЯ
// (rub — рубли, usd — доллары по курсу bonds). Перечисления — camelCase-строки.

/// <summary>Член корзины. <see cref="Value"/> — стоимость бумаги в портфеле (null — не удерживается либо это правило класса).</summary>
public sealed record CapitalBasketMemberDto
{
    public required ulong Id { get; init; }
    public required string Broker { get; init; }

    /// <summary>ISIN или тикер; для <c>asset_class</c> — класс актива.</summary>
    public required string InstrumentKey { get; init; }

    /// <summary>ДОЛЯ внутри корзины (fixed) — цель.</summary>
    public decimal? SubWeight { get; init; }

    /// <summary><c>instrument|asset_class</c>.</summary>
    public required string MatchRule { get; init; }

    public decimal? Value { get; init; }

    /// <summary>Фактическая ДОЛЯ бумаги внутри корзины (Value ÷ стоимость корзины); null — не удерживается/правило класса/корзина пуста.</summary>
    public decimal? ActualSubShare { get; init; }
}

/// <summary>Позиция, попавшая в корзину.</summary>
public sealed record CapitalBasketPositionDto
{
    public required string Broker { get; init; }
    public required string Key { get; init; }
    public required string AssetClass { get; init; }
    public required string Name { get; init; }

    /// <summary>В валюте портфеля.</summary>
    public required decimal Value { get; init; }
}

/// <summary>
/// Корзина с отклонением. Системные строки (<see cref="IsSystem"/>): <c>cash</c> (свободные деньги, в базе доли), <c>unassigned</c> и <c>fx</c> —
/// без цели, настроек и id.
/// </summary>
public sealed record CapitalBasketDto
{
    public ulong? Id { get; init; }
    public required string Code { get; init; }
    public required string Title { get; init; }
    public required bool IsSystem { get; init; }

    public decimal? TargetWeight { get; init; }
    public decimal? Band { get; init; }
    public decimal? HardBand { get; init; }
    public int? LiquidityTier { get; init; }

    /// <summary><c>fixed|engine|hold</c>.</summary>
    public string? FillRule { get; init; }

    public bool? AutoBuy { get; init; }
    public bool? AutoSell { get; init; }

    /// <summary>Минимальная сделка, в валюте портфеля.</summary>
    public decimal? MinTrade { get; init; }

    public required decimal Value { get; init; }

    /// <summary>Value ÷ база доли (портфель минус корзины без цели; деньги в базе), ДОЛЯ.</summary>
    public required decimal Weight { get; init; }

    /// <summary>Value ÷ весь портфель, ДОЛЯ.</summary>
    public required decimal ShareOfTotal { get; init; }

    /// <summary>Цель, ДОЛЯ; null — вне целей.</summary>
    public decimal? Target { get; init; }

    /// <summary>Weight − Target, ДОЛЯ (отрицательное — недобор); null без цели.</summary>
    public decimal? Drift { get; init; }

    /// <summary><c>ok|below|above|hardBelow|hardAbove|noTarget</c>.</summary>
    public required string State { get; init; }

    /// <summary>Target × база − Value, в валюте портфеля: положительное — не хватает до цели, отрицательное — избыток; null без цели.</summary>
    public decimal? DeficitToTarget { get; init; }

    public required IReadOnlyList<CapitalBasketMemberDto> Members { get; init; }
    public required IReadOnlyList<CapitalBasketPositionDto> Positions { get; init; }
}

public sealed record CapitalPortfolioDto
{
    /// <summary><c>rub|usd</c>.</summary>
    public required string Portfolio { get; init; }

    /// <summary>Валюта портфеля: RUB или USD.</summary>
    public required string Currency { get; init; }

    /// <summary>false — счёта брокера этого портфеля нет (корзины есть, позиций нет).</summary>
    public required bool HasAccount { get; init; }

    public required decimal TotalValue { get; init; }

    /// <summary>База доли: портфель минус корзины без цели.</summary>
    public required decimal BaseValue { get; init; }

    public required decimal FreeCash { get; init; }

    /// <summary>Σ целей, ДОЛЯ. Не 1 — цели портфеля не сходятся (UI предупреждает).</summary>
    public required decimal TargetsSum { get; init; }

    public bool DataIncomplete { get; init; }
    public DateTime? SyncedAt { get; init; }
    public required IReadOnlyList<CapitalBasketDto> Baskets { get; init; }
}

public sealed record BasketsResponseDto
{
    public required IReadOnlyList<CapitalPortfolioDto> Portfolios { get; init; }
    public required string Disclaimer { get; init; }
}

/// <summary>Редактируемые поля корзины (замена целиком). Все веса — ДОЛИ.</summary>
public record BasketUpdateRequest
{
    public string? Title { get; init; }

    /// <summary>ДОЛЯ 0..1; null — корзина вне целей.</summary>
    public decimal? TargetWeight { get; init; }

    public decimal? Band { get; init; }
    public decimal? HardBand { get; init; }
    public int? LiquidityTier { get; init; }
    public string? FillRule { get; init; }
    public bool AutoBuy { get; init; }
    public bool AutoSell { get; init; }
    public decimal? MinTrade { get; init; }
}

/// <summary>Элемент пакетного обновления: id + поля <see cref="BasketUpdateRequest"/>.</summary>
public sealed record BasketBatchItem : BasketUpdateRequest
{
    public ulong Id { get; init; }
}

/// <summary>Пакет: цели нескольких корзин меняются атомарно, сумма целей проверяется на ИТОГОВОМ состоянии.</summary>
public sealed record BasketBatchUpdateRequest
{
    public IReadOnlyList<BasketBatchItem>? Items { get; init; }
}

public sealed record BasketMemberRequest
{
    public string? Broker { get; init; }
    public string? InstrumentKey { get; init; }

    /// <summary>ДОЛЯ внутри корзины, 0..1.</summary>
    public decimal? SubWeight { get; init; }

    /// <summary><c>instrument</c> (по умолчанию) или <c>asset_class</c>.</summary>
    public string? MatchRule { get; init; }
}
