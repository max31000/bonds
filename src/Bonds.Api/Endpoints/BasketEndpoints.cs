using Bonds.Api.Auth;
using Bonds.Api.Endpoints.Dtos;
using Bonds.Core.Capital;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Time;
using Bonds.Infrastructure.Analytics;
using MySqlConnector;

namespace Bonds.Api.Endpoints;

/// <summary>
/// Корзины целевой структуры капитала (задача 62): <c>GET /api/baskets</c> (цели + отклонения по двум портфелям), <c>PUT /api/baskets/{id}</c>
/// и пакетный <c>PUT /api/baskets</c> (цели нескольких корзин атомарно — сумма целей портфеля проверяется на ИТОГОВОМ состоянии, поэтому
/// перераспределить 15/85 → 20/80 по одной корзине нельзя), <c>POST/DELETE /api/baskets/{id}/members</c>, <c>POST /api/baskets/reset-defaults</c>.
/// Только модель целей и расчёт отклонений — ни правил, ни заявок. Данные только своего пользователя. ВСЕ веса — ДОЛИ (проценты рисует фронт).
/// </summary>
public static class BasketEndpoints
{
    internal const string Disclaimer =
        "Оценка, не рекомендация: цели и отклонения — ваша собственная модель структуры капитала, расчёт по данным брокеров на момент " +
        "последней синхронизации и курсам bonds. Не инвестиционная рекомендация.";

    public static void MapBasketEndpoints(this WebApplication app)
    {
        app.MapGet("/api/baskets", GetBaskets);
        app.MapPut("/api/baskets", PutBatch);
        app.MapPut("/api/baskets/{id:long}", PutOne);
        app.MapPost("/api/baskets/{id:long}/members", AddMember);
        app.MapDelete("/api/baskets/{id:long}/members/{memberId:long}", RemoveMember);
        app.MapPost("/api/baskets/reset-defaults", ResetDefaults);
    }

    private static async Task<IResult> GetBaskets(ICurrentUser currentUser, CapitalOverviewBuilder builder, CancellationToken ct)
    {
        if (await currentUser.GetUserIdAsync() is not { } userId)
            return Results.Ok(new BasketsResponseDto { Portfolios = [], Disclaimer = Disclaimer });
        return Results.Ok(await BuildResponseAsync(builder, userId, ct));
    }

    private static async Task<BasketsResponseDto> BuildResponseAsync(CapitalOverviewBuilder builder, ulong userId, CancellationToken ct)
    {
        var overview = await builder.BuildAsync(userId, BusinessClock.MoscowToday(), ct);
        return new BasketsResponseDto { Portfolios = overview.Select(ToDto).ToList(), Disclaimer = Disclaimer };
    }

    private static async Task<IResult> PutOne(
        long id, BasketUpdateRequest? request, ICurrentUser currentUser, ICapitalBasketRepository repo, CapitalOverviewBuilder builder, CancellationToken ct)
    {
        if (request is null) return AnalyticsSupport.ValidationError("Тело запроса обязательно.");
        return await ApplyUpdatesAsync([(id, request)], currentUser, repo, builder, ct);
    }

    private static async Task<IResult> PutBatch(
        BasketBatchUpdateRequest? request, ICurrentUser currentUser, ICapitalBasketRepository repo, CapitalOverviewBuilder builder, CancellationToken ct)
    {
        if (request?.Items is not { Count: > 0 } items) return AnalyticsSupport.ValidationError("items: нужна хотя бы одна корзина.");
        if (items.Select(i => i.Id).Distinct().Count() != items.Count) return AnalyticsSupport.ValidationError("items: id корзин не должны повторяться.");
        return await ApplyUpdatesAsync(items.Select(i => ((long)i.Id, (BasketUpdateRequest)i)).ToList(), currentUser, repo, builder, ct);
    }

    /// <summary>
    /// Применяет изменения к ИТОГОВОМУ состоянию портфелей: поля каждой корзины проверяются, сумма целей — для каждого портфеля, где цель хотя бы
    /// одной корзины изменилась (правка флагов/полос не заставляет «чинить» чужие цели). Нарушение — 422, ничего не пишется.
    /// </summary>
    private static async Task<IResult> ApplyUpdatesAsync(
        IReadOnlyList<(long Id, BasketUpdateRequest Request)> updates,
        ICurrentUser currentUser, ICapitalBasketRepository repo, CapitalOverviewBuilder builder, CancellationToken ct)
    {
        if (await currentUser.GetUserIdAsync() is not { } userId) return Results.NotFound();

        var all = (await repo.GetByUserAsync(userId)).ToList();
        var targetsChanged = new HashSet<string>();
        var changed = new List<CapitalBasket>();
        foreach (var (id, request) in updates)
        {
            var basket = all.FirstOrDefault(b => (long)b.Id == id);
            if (basket is null) return Results.NotFound();

            var before = basket.TargetWeight;
            if (ApplyRequest(basket, request) is { } error) return AnalyticsSupport.ValidationError($"{basket.Code}: {error}");
            if (before != basket.TargetWeight) targetsChanged.Add(basket.Portfolio);
            changed.Add(basket);
        }

        foreach (var portfolio in targetsChanged)
        {
            if (BasketValidation.ValidateTargetsSum(portfolio, all.Where(b => b.Portfolio == portfolio)) is { } sumError)
                return AnalyticsSupport.ValidationError(sumError);
        }

        await repo.UpdateManyAsync(userId, changed);
        return Results.Ok(await BuildResponseAsync(builder, userId, ct));
    }

    /// <summary>Переносит поля запроса на (уже загруженную) корзину и проверяет их. Возвращает текст ошибки или null.</summary>
    private static string? ApplyRequest(CapitalBasket basket, BasketUpdateRequest request)
    {
        if (request.Band is null || request.HardBand is null || request.LiquidityTier is null || request.FillRule is null || request.MinTrade is null)
            return "band, hardBand, liquidityTier, fillRule и minTrade обязательны.";

        basket.Title = request.Title?.Trim() ?? string.Empty;
        basket.TargetWeight = request.TargetWeight;
        basket.Band = request.Band.Value;
        basket.HardBand = request.HardBand.Value;
        basket.LiquidityTier = request.LiquidityTier.Value;
        basket.FillRule = request.FillRule;
        basket.AutoBuy = request.AutoBuy;
        basket.AutoSell = request.AutoSell;
        basket.MinTrade = request.MinTrade.Value;
        return BasketValidation.ValidateFields(basket);
    }

    private static async Task<IResult> AddMember(
        long id, BasketMemberRequest? request, ICurrentUser currentUser, ICapitalBasketRepository repo)
    {
        if (await currentUser.GetUserIdAsync() is not { } userId) return Results.NotFound();
        if (request is null) return AnalyticsSupport.ValidationError("Тело запроса обязательно.");

        var all = await repo.GetByUserAsync(userId);
        var basket = all.FirstOrDefault(b => (long)b.Id == id);
        if (basket is null) return Results.NotFound();

        var member = new CapitalBasketMember
        {
            BasketId = basket.Id,
            Broker = request.Broker?.Trim().ToLowerInvariant() ?? string.Empty,
            InstrumentKey = request.InstrumentKey?.Trim() ?? string.Empty,
            SubWeight = request.SubWeight,
            MatchRule = request.MatchRule?.Trim().ToLowerInvariant() ?? BasketMatchRules.Instrument,
        };
        if (BasketValidation.ValidateMember(basket.Portfolio, member) is { } error) return AnalyticsSupport.ValidationError(error);

        // Инвариант «позиция ровно в одной корзине»: одна бумага (и одно правило класса) — член не более чем одной корзины портфеля.
        var owner = all.Where(b => b.Portfolio == basket.Portfolio)
            .FirstOrDefault(b => b.Members.Any(m => BasketValidation.MembersConflict(m, member)));
        if (owner is not null)
            return AnalyticsSupport.ValidationError($"«{member.InstrumentKey}» уже входит в корзину «{owner.Title}» — одна бумага состоит ровно в одной корзине.");

        try
        {
            member.Id = await repo.AddMemberAsync(userId, basket.Id, member);
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            return AnalyticsSupport.ValidationError($"«{member.InstrumentKey}» уже входит в эту корзину.");
        }

        return Results.Created($"/api/baskets/{id}/members/{member.Id}", new CapitalBasketMemberDto
        {
            Id = member.Id, Broker = member.Broker, InstrumentKey = member.InstrumentKey, SubWeight = member.SubWeight, MatchRule = member.MatchRule,
        });
    }

    private static async Task<IResult> RemoveMember(long id, long memberId, ICurrentUser currentUser, ICapitalBasketRepository repo)
    {
        if (await currentUser.GetUserIdAsync() is not { } userId) return Results.NotFound();
        return await repo.RemoveMemberAsync(userId, (ulong)id, (ulong)memberId) ? Results.NoContent() : Results.NotFound();
    }

    /// <summary>Сброс к стартовым корзинам: оба портфеля или один (<c>?portfolio=rub|usd</c>). Прежние корзины и члены удаляются.</summary>
    private static async Task<IResult> ResetDefaults(
        string? portfolio, ICurrentUser currentUser, ICapitalBasketRepository repo, CapitalOverviewBuilder builder, CancellationToken ct)
    {
        if (await currentUser.GetUserIdAsync() is not { } userId) return Results.NotFound();
        if (portfolio is not null && !CapitalPortfolios.All.Contains(portfolio))
            return AnalyticsSupport.ValidationError("portfolio: rub или usd.");

        // Если корзин нет вовсе — это и есть первый посев; иначе заменяем выбранные портфели.
        await builder.EnsureSeededAsync(userId);
        // Оба портфеля — одной транзакцией: сбой посередине не оставит один портфель сброшенным, а другой нет.
        var targets = (portfolio is null ? CapitalPortfolios.All : [portfolio]).ToDictionary(p => p, p => DefaultBaskets.For(userId, p));
        await repo.ReplacePortfoliosAsync(userId, targets);

        return Results.Ok(await BuildResponseAsync(builder, userId, ct));
    }

    private static CapitalPortfolioDto ToDto(CapitalPortfolioOverview p)
    {
        var configByCode = p.Baskets.ToDictionary(b => b.Code, StringComparer.Ordinal);
        return new CapitalPortfolioDto
        {
            Portfolio = p.Portfolio,
            Currency = p.Currency,
            HasAccount = p.HasAccount,
            TotalValue = p.Drift.TotalValue,
            BaseValue = p.Drift.BaseValue,
            FreeCash = p.FreeCash,
            TargetsSum = p.TargetsSum,
            DataIncomplete = p.DataIncomplete,
            SyncedAt = p.SyncedAtUtc,
            Baskets = p.Drift.Rows.Select(row => ToDto(row, configByCode.GetValueOrDefault(row.Code))).ToList(),
        };
    }

    private static CapitalBasketDto ToDto(BasketDriftRow row, CapitalBasket? config)
    {
        var members = (config?.Members ?? []).Select(m =>
        {
            decimal? value = null;
            if (m.MatchRule == BasketMatchRules.Instrument)
            {
                // Тот же критерий, что при назначении позиций в BasketDriftService.Assign: голый «LUV» видит «LUV.US».
                var held = row.Positions.Where(p => string.Equals(p.Broker, m.Broker, StringComparison.OrdinalIgnoreCase)
                                                    && BasketDriftService.KeyMatches(m.InstrumentKey, BasketDriftService.NormalizeKey(p.Key))).ToList();
                if (held.Count > 0) value = held.Sum(p => p.Value);
            }

            return new CapitalBasketMemberDto
            {
                Id = m.Id, Broker = m.Broker, InstrumentKey = m.InstrumentKey, SubWeight = m.SubWeight, MatchRule = m.MatchRule,
                Value = value, ActualSubShare = value is { } v && row.Value > 0m ? v / row.Value : null,
            };
        }).ToList();

        return new CapitalBasketDto
        {
            Id = row.BasketId,
            Code = row.Code,
            Title = row.Title,
            IsSystem = row.IsSystem,
            TargetWeight = config?.TargetWeight,
            Band = config?.Band,
            HardBand = config?.HardBand,
            LiquidityTier = config?.LiquidityTier,
            FillRule = config?.FillRule,
            AutoBuy = config?.AutoBuy,
            AutoSell = config?.AutoSell,
            MinTrade = config?.MinTrade,
            Value = row.Value,
            Weight = row.Weight,
            ShareOfTotal = row.ShareOfTotal,
            Target = row.Target,
            Drift = row.Drift,
            State = CamelCase(row.State),
            DeficitToTarget = row.DeficitToTarget,
            Members = members,
            Positions = row.Positions
                .Select(p => new CapitalBasketPositionDto { Broker = p.Broker, Key = p.Key, AssetClass = p.AssetClass, Name = p.Name, Value = p.Value })
                .ToList(),
        };
    }

    private static string CamelCase(BasketState state)
    {
        var s = state.ToString();
        return char.ToLowerInvariant(s[0]) + s[1..];
    }
}
