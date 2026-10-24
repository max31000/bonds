using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Bonds.Core.Agents;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Core.Policy;
using Bonds.Core.Time;
using Bonds.Infrastructure.Analytics;
using Bonds.Infrastructure.Policy;
using Bonds.Infrastructure.Universe;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bonds.Infrastructure.Agents.Mcp;

/// <summary>
/// Инструменты MCP-сервера bonds (задача 68). ВСЕ, кроме <c>propose_plan</c>, — только чтение и переиспользуют существующие точки истины
/// (<see cref="PortfolioHoldingsBuilder"/>, <see cref="CapitalOverviewBuilder"/>, <see cref="CashOverviewBuilder"/>, <see cref="BrokerHoldingsBuilder"/>,
/// <see cref="ReliabilityScoreProvider"/>, репозитории — на чтение). <c>propose_plan</c> — ТОЛЬКО <see cref="PolicyService.CreateManualAsync"/> (та же валидация,
/// что у ручного плана в вебе/боте), план всегда <c>PendingApproval</c>, <c>source = agent</c>; утверждать, отклонять, исполнять, менять лимиты/настройки/ключи
/// инструментам нечем. Ответ — JSON, обрезается по <see cref="AgentOptions.McpMaxResponseChars"/>. Единицы — как в API: доли — ДОЛИ, суммы — в валюте поля.
/// </summary>
public sealed partial class BondsMcpTools
{
    public const string Disclaimer = "Оценки и данные сервиса, не инвестиционная рекомендация.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly IServiceProvider _sp;
    private readonly IOptionsMonitor<AgentOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<BondsMcpTools> _logger;

    public BondsMcpTools(IServiceProvider sp, IOptionsMonitor<AgentOptions> options, TimeProvider time, ILogger<BondsMcpTools> logger)
    {
        _sp = sp;
        _options = options;
        _time = time;
        _logger = logger;
    }

    private sealed record ToolDef(string Name, string Description, JsonObject Schema);

    private static JsonObject NoArgs() => new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false };

    private static JsonObject IsinArg() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["isin"] = new JsonObject { ["type"] = "string", ["description"] = "ISIN бумаги, например RU000A0JX0J2" } },
        ["required"] = new JsonArray("isin"),
        ["additionalProperties"] = false,
    };

    private static readonly ToolDef[] Tools =
    [
        new("portfolio", "Облигации основного счёта T-Invest: количество, стоимость (₽, грязная цена × кол-во), YTM и дюрация (ДОЛИ/годы), погашение.", NoArgs()),
        new("baskets", "Корзины капитала по портфелям rub (T-Invest) и usd (Freedom): стоимость, доля, цель, отклонение (ДОЛИ), состояние полос, свободные деньги.", NoArgs()),
        new("plans", "Последние планы сделок пользователя (до 20): правило, статус, dry-run, строки (сторона, лоты, лимит, сумма).", NoArgs()),
        new("signals", "Активные события портфеля: тип, важность (Critical/Warning/Info), бумага, дата, подсказка.", NoArgs()),
        new("cashflow_next_90d", "Ожидаемые поступления по облигациям на 90 дней: дата, бумага, тип (купон/амортизация/погашение/оферта), брутто/налог/нетто (₽).", NoArgs()),
        new("bond_card", "Карточка бумаги из банка MOEX по ISIN: доходность (ДОЛЯ), спред, дюрация, оферта/погашение, ликвидность, эмитент.", IsinArg()),
        new("scorecard", "Скоркарта надёжности бумаги по ISIN: светофор, балл, покрытие данных (ДОЛЯ), флаги, ось Value.", IsinArg()),
        new("freedom_holdings", "Позиции и деньги счёта Freedom: суммы в валюте бумаги и в рублях/долларах по курсам сервиса.", NoArgs()),
        new("cash", "Деньги на счетах по брокерам: остатки по валютам, свободные рубли, последние пополнения и выводы.", NoArgs()),
        new("propose_plan",
            "Предложить план сделок. План НЕ исполняется и не подтверждается: он сохраняется со статусом «ждёт решения» и уходит пользователю. " +
            "kind: inflow — распределить сумму amount по корзинам; withdraw — план вывода amount или fraction (ДОЛЯ портфеля, 0.1 = 10%); " +
            "exit_basket — выйти из корзины без цели basketCode. portfolio: rub (по умолчанию) или usd. rationale — обоснование (обязательно).",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("inflow", "withdraw", "exit_basket") },
                    ["amount"] = new JsonObject { ["type"] = "number", ["description"] = "Сумма в валюте портфеля" },
                    ["fraction"] = new JsonObject { ["type"] = "number", ["description"] = "Доля портфеля (0, 1] — только withdraw" },
                    ["portfolio"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("rub", "usd") },
                    ["basketCode"] = new JsonObject { ["type"] = "string", ["description"] = "Код корзины — только exit_basket" },
                    ["rationale"] = new JsonObject { ["type"] = "string", ["description"] = "Почему предлагается план (до 1000 символов)" },
                },
                ["required"] = new JsonArray("kind", "rationale"),
                ["additionalProperties"] = false,
            }),
    ];

    public static IReadOnlySet<string> Names { get; } = Tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

    public static JsonArray Definitions() => new(Tools.Select(t => (JsonNode)new JsonObject
    {
        ["name"] = t.Name,
        ["description"] = t.Description,
        ["inputSchema"] = t.Schema.DeepClone(),
    }).ToArray());

    [GeneratedRegex(@"^[A-Z]{2}[A-Z0-9]{9}[0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex IsinPattern();

    [GeneratedRegex(@"^[a-z0-9_-]{1,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex BasketCodePattern();

    /// <summary>Вызов инструмента. Ошибка ввода — <c>isError</c> с понятным текстом; прочие сбои — общий текст (подробности только в лог, без данных).</summary>
    public async Task<(string Text, bool IsError)> CallAsync(string name, JsonElement args, ulong userId, CancellationToken ct = default, ulong runId = 0)
    {
        try
        {
            object payload = name switch
            {
                "portfolio" => await PortfolioAsync(userId, ct),
                "baskets" => await BasketsAsync(userId, ct),
                "plans" => await PlansAsync(userId),
                "signals" => await SignalsAsync(userId),
                "cashflow_next_90d" => await CashflowAsync(userId),
                "bond_card" => await BondCardAsync(RequireIsin(args), ct),
                "scorecard" => await ScorecardAsync(RequireIsin(args), ct),
                "freedom_holdings" => await FreedomAsync(userId, ct),
                "cash" => await Get<CashOverviewBuilder>().BuildAsync(userId, Today(), ct),
                "propose_plan" => await ProposePlanAsync(userId, runId, args, ct),
                _ => throw new McpToolInputException("Неизвестный инструмент"),
            };
            return (Serialize(payload), false);
        }
        catch (McpToolInputException ex)
        {
            return (ex.Message, true);
        }
        catch (PolicyValidationException ex)
        {
            return ("План не создан: " + ex.Message, true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("MCP tool {Tool} failed for user {UserId}: {ErrorType}", name, userId, ex.GetType().Name);
            return ("Инструмент временно недоступен", true);
        }
    }

    private T Get<T>() where T : notnull => (T)(_sp.GetService(typeof(T)) ?? throw new InvalidOperationException(typeof(T).Name + " не зарегистрирован"));

    private DateOnly Today() => BusinessClock.MoscowDate(_time.GetUtcNow().UtcDateTime);

    private string Serialize(object payload)
    {
        var text = JsonSerializer.Serialize(payload, Json);
        var max = Math.Max(1000, _options.CurrentValue.McpMaxResponseChars);
        return text.Length <= max ? text : text[..max] + "\n…(ответ обрезан: данных больше, чем помещается)";
    }

    private static string RequireIsin(JsonElement args)
    {
        var isin = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("isin", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim().ToUpperInvariant() : null;
        if (isin is null || !IsinPattern().IsMatch(isin)) throw new McpToolInputException("Нужен корректный ISIN (12 символов, например RU000A0JX0J2)");
        return isin;
    }

    private async Task<ulong?> PrimaryAccountAsync(ulong userId)
    {
        var accounts = await Get<IAccountRepository>().GetByUserIdAsync(userId);
        return accounts.Where(a => a.Broker == Brokers.TInvest).Select(a => (ulong?)a.Id).OrderBy(id => id).FirstOrDefault();
    }

    private async Task<object> PortfolioAsync(ulong userId, CancellationToken ct)
    {
        if (await PrimaryAccountAsync(userId) is not { } accountId) return new { positions = Array.Empty<object>(), note = "Счёт T-Invest не подключён" };
        var holdings = await Get<PortfolioHoldingsBuilder>().BuildForAccountAsync(accountId, Today(), ct);
        return new
        {
            units = "marketValueRub — рубли; ytmEffective, currentYield, gSpread — ДОЛИ; durations — годы",
            positions = holdings.Where(h => h.Quantity > 0m).OrderByDescending(h => h.MarketValueRub).Select(h => new
            {
                h.Isin,
                h.Name,
                h.Issuer,
                h.Sector,
                h.Quantity,
                h.MarketValueRub,
                h.YtmEffective,
                h.CurrentYield,
                h.GSpread,
                h.ModifiedDuration,
                h.MacaulayDuration,
                h.MaturityDate,
                h.HorizonDate,
                h.IsCalculatedToOffer,
                h.CouponType,
                h.IsFloater,
                h.DataIncomplete,
            }),
            disclaimer = Disclaimer,
        };
    }

    private async Task<object> BasketsAsync(ulong userId, CancellationToken ct)
    {
        var overview = await Get<CapitalOverviewBuilder>().BuildAsync(userId, Today(), ct);
        return new
        {
            units = "weight, target, drift, shareOfTotal — ДОЛИ; value, freeCash, deficitToTarget — валюта портфеля (rub — рубли, usd — доллары)",
            portfolios = overview.Where(p => p.HasAccount).Select(p => new
            {
                p.Portfolio,
                p.Currency,
                p.FreeCash,
                totalValue = p.Drift.TotalValue,
                p.DataIncomplete,
                p.SyncedAtUtc,
                baskets = p.Drift.Rows.Select(r => new
                {
                    r.Code,
                    r.Title,
                    r.Value,
                    r.Weight,
                    r.Target,
                    r.Drift,
                    r.State,
                    r.DeficitToTarget,
                    positions = r.Positions.Count,
                }),
            }),
            disclaimer = Disclaimer,
        };
    }

    private async Task<object> PlansAsync(ulong userId)
    {
        var plans = await Get<ITradePlanRepository>().ListAsync(userId, null, 20);
        return new
        {
            units = "limitPrice — валюта цены за штуку (облигации — чистая цена ₽); estAmount/estFee — валюта строки",
            plans = plans.Select(p => new
            {
                p.Id,
                p.Rule,
                p.Trigger,
                p.Status,
                p.DryRun,
                p.Source,
                p.CreatedAt,
                p.ExpiresAt,
                lines = p.Lines.Select(l => new { l.Broker, l.Ticker, l.InstrumentKey, l.Side, l.QuantityLots, l.LotSize, l.LimitPrice, l.EstAmount, l.EstFee, l.Currency }),
            }),
        };
    }

    private async Task<object> SignalsAsync(ulong userId)
    {
        if (await PrimaryAccountAsync(userId) is not { } accountId) return new { signals = Array.Empty<object>() };
        var signals = await Get<ISignalRepository>().GetByAccountIdAsync(accountId);
        var instruments = (await Get<IInstrumentRepository>().GetAllAsync()).ToDictionary(i => i.Id);
        return new
        {
            signals = signals.OrderByDescending(s => s.Severity).ThenBy(s => s.EventDate).Select(s => new
            {
                s.Type,
                s.Severity,
                s.Subject,
                s.SuggestedAction,
                s.EventDate,
                s.FirstSeenAt,
                isin = s.InstrumentId is { } iid && instruments.TryGetValue(iid, out var i) ? i.Isin : null,
            }),
        };
    }

    private async Task<object> CashflowAsync(ulong userId)
    {
        if (await PrimaryAccountAsync(userId) is not { } accountId) return new { flows = Array.Empty<object>() };
        var today = Today();
        var flows = await Get<IProjectedCashFlowRepository>().GetByAccountIdAsync(accountId, today, today.AddDays(90));
        var instruments = (await Get<IInstrumentRepository>().GetAllAsync()).ToDictionary(i => i.Id);
        return new
        {
            units = "grossRub/taxRub/netRub — рубли на всю позицию",
            flows = flows.OrderBy(f => f.Date).Select(f => new
            {
                f.Date,
                f.FlowType,
                isin = instruments.TryGetValue(f.InstrumentId, out var i) ? i.Isin : null,
                name = instruments.TryGetValue(f.InstrumentId, out var n) ? n.Name : null,
                f.GrossRub,
                f.TaxRub,
                f.NetRub,
                f.IsEstimated,
            }),
        };
    }

    private async Task<object> BondCardAsync(string isin, CancellationToken ct)
    {
        var entries = await Get<IBondUniverseRepository>().GetByIsinsAsync([isin], ct);
        if (entries.Count == 0) throw new McpToolInputException($"Бумаги {isin} нет в банке MOEX");
        return new { units = "доходности и спреды — ДОЛИ, дюрация — годы, цены — как в банке сервиса", bond = entries[0], disclaimer = Disclaimer };
    }

    private async Task<object> ScorecardAsync(string isin, CancellationToken ct)
    {
        var book = await Get<ReliabilityScoreProvider>().GetBookAsync(ct);
        if (!book.TryGetByIsin(isin, out var score)) throw new McpToolInputException($"Скоркарты для {isin} нет (бумаги нет в банке)");
        return new { units = "coverage — ДОЛЯ, score — 0..100", scorecard = score, disclaimer = Disclaimer };
    }

    private async Task<object> FreedomAsync(ulong userId, CancellationToken ct)
    {
        var account = (await Get<IAccountRepository>().GetByUserIdAndBrokerAsync(userId, Brokers.Freedom)).OrderBy(a => a.Id).FirstOrDefault();
        if (account is null) return new { note = "Счёт Freedom не подключён" };
        var holdings = await Get<BrokerHoldingsBuilder>().BuildAsync(account.Id, Today(), ct);
        return new { units = "поля без суффикса — валюта строки; *Rub — рубли; *Usd — доллары", holdings, disclaimer = Disclaimer };
    }

    private async Task<object> ProposePlanAsync(ulong userId, ulong runId, JsonElement args, CancellationToken ct)
    {
        if (args.ValueKind != JsonValueKind.Object) throw new McpToolInputException("Нужны аргументы kind и rationale");
        var rationale = (args.TryGetProperty("rationale", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null)?.Trim();
        if (string.IsNullOrWhiteSpace(rationale)) throw new McpToolInputException("rationale обязателен: объясните, зачем план");
        if (rationale.Length > 1000) rationale = rationale[..1000];

        // Только разбор события и вопрос пользователя (вид — из bearer запуска): дайджест и ежемесячный обзор планов не предлагают.
        var tokens = Get<AgentMcpTokens>();
        if (!AgentMcpTokens.KindMayPropose(tokens.KindOf(runId)))
            throw new McpToolInputException("propose_plan недоступен в этой задаче: дайджест и ежемесячный обзор — только чтение");

        // Не больше AgentOptions.MaxProposalsPerRun планов за запуск (атомарная бронь слота; не создан — слот возвращается): инъекция в данных не должна
        // заваливать пользователя планами, в том числе параллельными вызовами.
        if (!tokens.TryReserveProposal(runId, _options.CurrentValue.MaxProposalsPerRun))
            throw new McpToolInputException("В этом запуске план уже предложен — больше нельзя");

        TradePlan? plan;
        PolicyOutcome outcome;
        try
        {
            (plan, outcome) = await CreateProposedPlanAsync(userId, args, rationale, ct);
        }
        catch
        {
            tokens.ReleaseProposal(runId);
            throw;
        }

        if (plan is null)
        {
            tokens.ReleaseProposal(runId);
            return new { created = false, notes = outcome.Notes };
        }

        // Тот же путь подтверждения, что у любого плана: уведомление с кнопками в бот; решение — только пользователь.
        if (_sp.GetService(typeof(IPlanNotifier)) is IPlanNotifier notifier) await notifier.NotifyPlanCreatedAsync(plan, ct);
        return new
        {
            created = true,
            planId = plan.Id,
            status = plan.Status,
            dryRun = plan.DryRun,
            note = "План сохранён и ждёт решения пользователя. Агент не может его подтвердить или исполнить.",
        };
    }

    /// <summary>Разбор аргументов и создание плана через <see cref="PolicyService.CreateManualAsync"/> (та же валидация, что у ручного плана).</summary>
    private async Task<(TradePlan? Plan, PolicyOutcome Outcome)> CreateProposedPlanAsync(ulong userId, JsonElement args, string rationale, CancellationToken ct)
    {
        string? Str(string key) => args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        decimal? Num(string key) => args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;

        var portfolio = Str("portfolio");
        var (rule, parameters) = Str("kind") switch
        {
            "inflow" => (PlanRules.P2Inflow, new PlanParams(Amount: RequirePositive(Num("amount"), "amount"), Portfolio: portfolio)),
            "withdraw" => (PlanRules.P5Withdraw, new PlanParams(Amount: Num("amount"), Fraction: Num("fraction"), Portfolio: portfolio)),
            "exit_basket" => (PlanRules.Manual, new PlanParams(Code: RequireCode(Str("basketCode")))),
            _ => throw new McpToolInputException("kind должен быть inflow, withdraw или exit_basket"),
        };

        var now = _time.GetUtcNow().UtcDateTime;
        return await Get<PolicyService>().CreateManualAsync(userId, rule, parameters, now, ct, new PlanOrigin(PlanSources.Agent, rationale));
    }

    private static decimal RequirePositive(decimal? value, string name) =>
        value is > 0m ? value.Value : throw new McpToolInputException(name + " должен быть положительным числом");

    private static string RequireCode(string? code)
    {
        var normalized = code?.Trim().ToLowerInvariant();
        return normalized is not null && BasketCodePattern().IsMatch(normalized) ? normalized : throw new McpToolInputException("basketCode — код корзины (латиница, цифры, _ и -)");
    }
}

/// <summary>Ошибка аргументов инструмента — текст уходит агенту как <c>isError</c>.</summary>
public sealed class McpToolInputException : Exception
{
    public McpToolInputException(string message) : base(message) { }
}

