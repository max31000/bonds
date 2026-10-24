using Bonds.Core.Capital;
using Bonds.Core.Credentials;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Core.Policy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bonds.Infrastructure.Policy;

/// <summary>Итог прогона правил для лога и тестов: сколько планов создано и почему остальных нет.</summary>
public sealed class PolicyRunResult
{
    public List<ulong> CreatedPlanIds { get; } = [];
    public List<string> Notes { get; } = [];
    public int Expired { get; set; }
}

/// <summary>
/// Шаг <c>SyncCycleService</c> ПОСЛЕ синка всех брокеров (задача 63). Для каждого пользователя: просроченные <c>PendingApproval</c> и <c>AutoApproved</c> → <c>Expired</c>, затем правила
/// P2 (по НОВЫМ пополнениям), P1, P7, P3b (в день недели из опций, по умолчанию понедельник), P4. Порядок P2 до P1 намеренный: пополнение сначала делится по
/// дефициту корзин, и только оставшиеся свободные деньги паркует P1 (иначе P1 увёл бы всё в «ликвидность»). Дубликат не создаётся, если открыт план того же
/// правила и причины; деньги уже созданных планов резервируются для следующих правил. Сбой одного пользователя/правила пишется в лог и не прерывает остальных.
/// ПЛАНЫ — ТОЛЬКО ДАННЫЕ (<c>dry_run</c> = 1): заявки брокерам не отправляются.
/// Пауза пользователя (<c>user_settings.execution_paused</c>, команда <c>/stop</c>) — AutoApproved не создаётся, только PendingApproval. Каждый сохранённый план уходит в
/// <see cref="IPlanNotifier"/> (Telegram: PendingApproval — с кнопками, AutoApproved — информационно).
/// </summary>
public sealed class PolicyRunner
{
    private readonly PolicyInputBuilder _builder;
    private readonly PolicyService _service;
    private readonly ITradePlanRepository _plans;
    private readonly IAccountCashMovementRepository _movements;
    private readonly IUserRepository _users;
    private readonly IUserCredentialStore _credentials;
    private readonly IUserSettingsRepository _settings;
    private readonly IPlanNotifier _notifier;
    private readonly PolicyOptions _options;
    private readonly ILogger<PolicyRunner> _logger;

    public PolicyRunner(
        PolicyInputBuilder builder,
        PolicyService service,
        ITradePlanRepository plans,
        IAccountCashMovementRepository movements,
        IUserRepository users,
        IUserCredentialStore credentials,
        IUserSettingsRepository settings,
        IPlanNotifier notifier,
        IOptions<PolicyOptions> options,
        ILogger<PolicyRunner> logger)
    {
        _builder = builder;
        _service = service;
        _plans = plans;
        _movements = movements;
        _users = users;
        _credentials = credentials;
        _settings = settings;
        _notifier = notifier;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Прогон для всех пользователей с брокерскими счетами (основной пользователь + владельцы ключей Freedom).</summary>
    public async Task<PolicyRunResult> RunAllAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var result = new PolicyRunResult { Expired = await _plans.ExpirePendingAsync(nowUtc) };

        var userIds = new HashSet<ulong>();
        if (await _users.GetPrimaryUserIdAsync() is { } primary) userIds.Add(primary);
        foreach (var id in await _credentials.ListUserIdsWithValidAsync(CredentialProviders.Freedom)) userIds.Add(id);
        // Задача 67 (#5): владельцы tinvest_read (их счета синкаются своими токенами).
        foreach (var id in await _credentials.ListUserIdsWithValidAsync(CredentialProviders.TInvestRead) ?? []) userIds.Add(id);

        foreach (var userId in userIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await RunForUserAsync(userId, nowUtc, result, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Policy run failed for user {UserId}", userId);
                result.Notes.Add($"Пользователь {userId}: ошибка прогона правил ({ex.GetType().Name})");
            }
        }

        return result;
    }

    public async Task RunForUserAsync(ulong userId, DateTime nowUtc, PolicyRunResult result, CancellationToken ct = default)
    {
        var inputs = new Dictionary<string, PolicyInput>(await _builder.BuildAsync(userId, nowUtc, ignoreHours: false, ct), StringComparer.Ordinal);

        // Пауза (/stop в Telegram): авто-подтверждение выключено, все планы остаются PendingApproval и ждут решения человека.
        var paused = await _settings.IsExecutionPausedAsync(userId);

        // P2: каждое новое пополнение T-Invest планируется один раз (в любом статусе плана). План не создан (рынок закрыт, нет цен) — пополнение
        // не «съедено», следующий цикл попробует снова, пока оно в окне DepositLookback.
        if (inputs.TryGetValue(CapitalPortfolios.Rub, out var rub))
        {
            foreach (var deposit in await NewDepositsAsync(rub.AccountId, nowUtc))
            {
                var trigger = $"Пополнение {deposit.Date:yyyy-MM-dd HH:mm} на {deposit.Amount:0.##} ₽ [{deposit.ExternalId}]";
                if (await _plans.ExistsAsync(userId, PlanRules.P2Inflow, trigger, openOnly: false, nowUtc)) continue;

                var input = inputs[CapitalPortfolios.Rub];
                // Неполные/устаревшие данные (задача 67, #7): правило само откажет с причиной — дорогие облигационные данные не собираем.
                if (PolicyDataGate.Problem(input) is null && input.Baskets.Any(b => b.FillRule == BasketFillRules.Engine))
                    input = await _builder.WithBondDataAsync(userId, input, ct);
                var outcome = InflowRule.Evaluate(input, deposit.Amount, trigger);
                inputs[CapitalPortfolios.Rub] = await ApplyAsync(userId, outcome, nowUtc, inputs[CapitalPortfolios.Rub], result, paused, dedupe: false, ct);
            }
        }

        if (inputs.TryGetValue(CapitalPortfolios.Rub, out var rubP1))
        {
            var outcome = SweepRule.Evaluate(rubP1);
            inputs[CapitalPortfolios.Rub] = await ApplyAsync(userId, outcome, nowUtc, rubP1, result, paused, dedupe: true, ct);
        }

        if (inputs.TryGetValue(CapitalPortfolios.Usd, out var usd))
        {
            var outcome = FreedomTopUpRule.Evaluate(usd);
            inputs[CapitalPortfolios.Usd] = await ApplyAsync(userId, outcome, nowUtc, usd, result, paused, dedupe: true, ct);
        }

        if (inputs.TryGetValue(CapitalPortfolios.Rub, out var rubWeekly))
        {
            // Дубликат проверяем ДО сбора облигационных данных (кандидаты, стаканы, метаданные брокера — дорого): открытый план или пауза повтора — выходим сразу.
            var isCheckDay = TradingHours.ToMoscow(nowUtc).DayOfWeek == _options.LiquidityCheckDay;
            var dataProblem = PolicyDataGate.Problem(rubWeekly); // задача 67 (#7): P3b и P4 на неполных/устаревших данных не строятся
            if (isCheckDay && LiquidityLooksOverfilled(rubWeekly) && dataProblem is not null)
            {
                Note(result, $"{PlanRules.P3bLiqToBonds}: {dataProblem} — перелив не строится");
            }
            else if (isCheckDay && LiquidityLooksOverfilled(rubWeekly))
            {
                if (await IsDuplicateAsync(userId, PlanRules.P3bLiqToBonds, LiquidityToBondsRule.TriggerFor(nowUtc), nowUtc))
                {
                    result.Notes.Add($"{PlanRules.P3bLiqToBonds}: план с той же причиной уже есть (открытый или созданный недавно) — данные по облигациям не собирались");
                }
                else
                {
                    var withBonds = await _builder.WithBondDataAsync(userId, rubWeekly, ct);
                    var outcome = LiquidityToBondsRule.Evaluate(withBonds);
                    inputs[CapitalPortfolios.Rub] = await ApplyAsync(userId, outcome, nowUtc, rubWeekly, result, paused, dedupe: true, ct);
                }
            }

            // P4 не дублирует P3b: пока открыт перелив ликвидности, продажа той же корзины ребалансом не предлагается.
            if (RebalanceRule.TriggerFor(inputs[CapitalPortfolios.Rub]) is not null && dataProblem is not null)
            {
                Note(result, $"{PlanRules.P4Rebalance}: {dataProblem} — ребаланс не строится");
            }
            else if (RebalanceRule.TriggerFor(inputs[CapitalPortfolios.Rub]) is { } p4Trigger
                && !await _plans.HasOpenRuleAsync(userId, PlanRules.P3bLiqToBonds, nowUtc))
            {
                if (await IsDuplicateAsync(userId, PlanRules.P4Rebalance, p4Trigger, nowUtc))
                {
                    result.Notes.Add($"{PlanRules.P4Rebalance}: план с той же причиной уже есть (открытый или созданный недавно) — данные по облигациям не собирались");
                }
                else
                {
                    var withBonds = await _builder.WithBondDataAsync(userId, inputs[CapitalPortfolios.Rub], ct);
                    var outcome = RebalanceRule.Evaluate(withBonds);
                    inputs[CapitalPortfolios.Rub] = await ApplyAsync(userId, outcome, nowUtc, inputs[CapitalPortfolios.Rub], result, paused, dedupe: true, ct);
                }
            }
        }

        if (inputs.TryGetValue(CapitalPortfolios.Usd, out var usdRebalance) && HasHardDeviation(usdRebalance))
        {
            var outcome = RebalanceRule.Evaluate(usdRebalance);
            inputs[CapitalPortfolios.Usd] = await ApplyAsync(userId, outcome, nowUtc, usdRebalance, result, paused, dedupe: true, ct);
        }
    }

    private void Note(PolicyRunResult result, string note)
    {
        _logger.LogInformation("Policy: {Note}", note);
        result.Notes.Add(note);
    }

    private bool LiquidityLooksOverfilled(PolicyInput input)
    {
        var row = input.RowByCode("liquidity");
        return row is { State: BasketState.Above or BasketState.HardAbove } && row.Weight > _options.LiquidityOverflowWeight;
    }

    private static bool HasHardDeviation(PolicyInput input) =>
        input.Drift.Rows.Any(r => !r.IsSystem && r.State is BasketState.HardAbove or BasketState.HardBelow);

    private async Task<IReadOnlyList<(DateTime Date, decimal Amount, string ExternalId)>> NewDepositsAsync(ulong accountId, DateTime nowUtc)
    {
        var since = nowUtc - _options.DepositLookback;
        var recent = await _movements.GetRecentAsync(accountId, 10);
        return recent
            .Where(m => m.Type == CashMovementType.Deposit && m.Currency == "RUB" && m.Amount > 0m && m.Date >= since)
            .OrderBy(m => m.Date)
            .Select(m => (m.Date, m.Amount, m.ExternalId))
            .ToList();
    }

    /// <summary>Есть ли открытый план того же правила и причины либо созданный в паузу повтора (<see cref="PolicyOptions.RepeatCooldown"/>) в любом статусе.</summary>
    private async Task<bool> IsDuplicateAsync(ulong userId, string rule, string trigger, DateTime nowUtc) =>
        await _plans.ExistsAsync(userId, rule, trigger, openOnly: true, nowUtc)
        || await _plans.ExistsAsync(userId, rule, trigger, openOnly: false, nowUtc, createdSince: nowUtc - _options.RepeatCooldown);

    /// <summary>Сохраняет план (если он есть и не дублирует открытый) и возвращает вход с учётом зарезервированных планом денег и авто-лимита дня.</summary>
    private async Task<PolicyInput> ApplyAsync(
        ulong userId, PolicyOutcome outcome, DateTime nowUtc, PolicyInput input, PolicyRunResult result, bool paused, bool dedupe, CancellationToken ct = default)
    {
        foreach (var note in outcome.Notes) _logger.LogInformation("Policy: {Note}", note);
        result.Notes.AddRange(outcome.Notes);

        var draft = outcome.Plan;
        if (draft is null) return input;
        if (dedupe && await IsDuplicateAsync(userId, draft.Rule, draft.Trigger, nowUtc))
        {
            result.Notes.Add($"{draft.Rule}: план с той же причиной уже есть (открытый или созданный недавно) — дубликат не создан");
            return input;
        }

        var plan = await _service.SaveAsync(userId, draft, nowUtc, forcePending: paused);
        result.CreatedPlanIds.Add(plan.Id);
        await _notifier.NotifyPlanCreatedAsync(plan, ct);
        return CarryForward(input, draft, plan.Status);
    }

    /// <summary>
    /// Вход для СЛЕДУЮЩЕГО правила того же цикла после сохранения плана: деньги плана резервируются (<see cref="TradePlanSummary.ReservedBuy"/> — покупки + комиссия в
    /// валюте портфеля) и, если план сохранён как <c>AutoApproved</c>, суточный счётчик <see cref="PolicyInput.AutoApprovedTodayRub"/> растёт на Σ покупок в RUB.
    /// Иначе (AutoApproved в dry-run в суточную сумму из БД не входит) два авто-плана одного цикла по 40k обошли бы лимит 50k/день.
    /// </summary>
    internal static PolicyInput CarryForward(PolicyInput input, TradePlanDraft draft, string savedStatus)
    {
        var spent = draft.Summary.ReservedBuy;
        var autoRub = savedStatus == PlanStatuses.AutoApproved
            ? draft.Lines.Where(l => l.Side == PlanSides.Buy && string.Equals(l.Currency, "RUB", StringComparison.OrdinalIgnoreCase)).Sum(l => l.EstAmount)
            : 0m;
        return input with
        {
            FreeCash = Math.Max(0m, input.FreeCash - spent),
            ReservedCash = input.ReservedCash + spent,
            AutoApprovedTodayRub = input.AutoApprovedTodayRub + autoRub,
        };
    }
}
