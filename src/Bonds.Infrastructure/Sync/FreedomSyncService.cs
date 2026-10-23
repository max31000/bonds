using System.Collections.Concurrent;
using Bonds.Core.Brokers;
using Bonds.Core.Credentials;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Core.Time;
using Bonds.Infrastructure.Connectors.Freedom;
using Bonds.Infrastructure.Connectors.Freedom.Models;
using Microsoft.Extensions.Logging;

namespace Bonds.Infrastructure.Sync;

/// <summary>Итог <see cref="FreedomSyncService.SyncAllAsync"/> (для лога и тестов). <see cref="Errors"/> — частичные сбои (не исключения).</summary>
public sealed class FreedomSyncResult
{
    public int UsersSynced { get; set; }
    public int PositionsSynced { get; set; }
    public int PositionsClosed { get; set; }
    public int CashRowsSynced { get; set; }
    public int OperationsUpserted { get; set; }
    public List<string> Errors { get; } = [];
}

/// <summary>
/// Задача 60: синк счёта Freedom (read-only) для КАЖДОГО пользователя с валидным секретом <c>freedom</c>: профиль → счёт
/// <c>accounts(broker='freedom')</c> → позиции и деньги → журнал (сделки с 2020-01-01 + последние 500 движений денег). Отдельный домен
/// <c>broker_*</c> по образцу <see cref="FundSyncService"/>: облигационные таблицы и движок не затрагиваются.
/// <para>
/// <b>Устойчивость.</b> Сбой шага (портфель / сделки / движения денег) или одного пользователя — <c>LogWarning</c>, остальное продолжается;
/// метод не бросает (кроме отмены). Ключи берутся только через <see cref="IUserCredentialStore"/> и нигде не логируются.
/// </para>
/// <para>
/// <b>Сверка позиций</b> через <see cref="SyncGuards"/>: пустой ответ брокера при непустых локальных позициях удаляет их только со 2-го синка
/// подряд. Остатки денег: нулевые не хранятся (отсутствие строки = 0), валюты, которых нет в ответе, убираются — но только если брокер
/// вернул хотя бы одну строку остатков (пустой <c>acc</c> — признак деградации ответа, деньги не трогаем).
/// </para>
/// </summary>
public sealed class FreedomSyncService
{
    /// <summary>Начало запрашиваемой истории сделок.</summary>
    public static readonly DateOnly TradesHistoryStart = new(2020, 1, 1);

    /// <summary>Сколько последних движений денег просим у брокера.</summary>
    public const int CashFlowsTake = 500;

    /// <summary>Лимит сделок в одном ответе <c>getTradesHistory</c>; ответ такого размера считается усечённым и окно делится пополам.</summary>
    internal const int TradesPageLimit = 1000;

    // Счета, по которым предыдущий синк получил от брокера пустой список позиций при непустых локальных.
    internal static readonly ConcurrentDictionary<ulong, byte> EmptyPositionsSeen = new();

    private readonly IFreedomApiClient _api;
    private readonly IUserCredentialStore _credentials;
    private readonly IAccountRepository _accounts;
    private readonly IBrokerPositionRepository _positions;
    private readonly IBrokerCashRepository _cash;
    private readonly IBrokerOperationRepository _operations;
    private readonly ILogger<FreedomSyncService> _logger;

    public FreedomSyncService(
        IFreedomApiClient api,
        IUserCredentialStore credentials,
        IAccountRepository accounts,
        IBrokerPositionRepository positions,
        IBrokerCashRepository cash,
        IBrokerOperationRepository operations,
        ILogger<FreedomSyncService> logger)
    {
        _api = api;
        _credentials = credentials;
        _accounts = accounts;
        _positions = positions;
        _cash = cash;
        _operations = operations;
        _logger = logger;
    }

    public async Task<FreedomSyncResult> SyncAllAsync(CancellationToken ct = default)
    {
        var result = new FreedomSyncResult();
        IReadOnlyList<ulong> userIds;
        try
        {
            userIds = await _credentials.ListUserIdsWithValidAsync(CredentialProviders.Freedom);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Freedom sync: failed to list users with a valid credential");
            result.Errors.Add("Freedom: не удалось получить список пользователей с ключами");
            return result;
        }

        foreach (var userId in userIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await SyncUserAsync(userId, result, ct)) result.UsersSynced++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Freedom sync failed for user {UserId}", userId);
                result.Errors.Add($"Freedom (пользователь {userId}): ошибка синка ({ex.GetType().Name})");
            }
        }

        return result;
    }

    /// <returns>true — счёт пользователя найден/создан и хотя бы профиль получен.</returns>
    private async Task<bool> SyncUserAsync(ulong userId, FreedomSyncResult result, CancellationToken ct)
    {
        var secret = await _credentials.GetSecretResultAsync(userId, CredentialProviders.Freedom);
        if (secret.Outcome != CredentialReadOutcome.Ok
            || secret.Fields is null
            || !secret.Fields.TryGetValue("publicKey", out var publicKey)
            || !secret.Fields.TryGetValue("privateKey", out var privateKey)
            || string.IsNullOrWhiteSpace(publicKey)
            || string.IsNullOrWhiteSpace(privateKey))
        {
            _logger.LogWarning("Freedom sync: credential for user {UserId} is unreadable ({Outcome}) — skipped", userId, secret.Outcome);
            return false;
        }

        var credentials = new FreedomCredentials(publicKey, privateKey);

        var info = await _api.GetUserInfoAsync(credentials, ct);
        var account = await EnsureAccountAsync(userId, info);

        await RunStepAsync("portfolio", result, () => SyncPortfolioAsync(account.Id, credentials, result, ct));
        await RunStepAsync("trades", result, () => SyncTradesAsync(account.Id, credentials, result, ct));
        await RunStepAsync("cashflows", result, () => SyncCashFlowsAsync(account.Id, credentials, result, ct));
        return true;
    }

    private async Task RunStepAsync(string step, FreedomSyncResult result, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Freedom sync step {Step} failed", step);
            result.Errors.Add($"Freedom {step}: ошибка синка ({ex.GetType().Name})");
        }
    }

    private async Task<Account> EnsureAccountAsync(ulong userId, FreedomUserInfo info)
    {
        var number = string.IsNullOrWhiteSpace(info.AccountNumber) ? null : info.AccountNumber.Trim();
        var existing = await _accounts.GetByUserIdAndBrokerAsync(userId, Brokers.Freedom);
        var account = existing.FirstOrDefault(a => a.BrokerAccountId == number) ?? existing.FirstOrDefault();
        if (account is null)
        {
            account = new Account { UserId = userId, Broker = Brokers.Freedom, BrokerAccountId = number, Name = "Freedom" };
            account.Id = await _accounts.CreateAsync(account);
            return account;
        }

        if (number is not null && account.BrokerAccountId != number)
        {
            account.BrokerAccountId = number;
            await _accounts.UpdateAsync(account);
        }

        return account;
    }

    private async Task SyncPortfolioAsync(ulong accountId, FreedomCredentials credentials, FreedomSyncResult result, CancellationToken ct)
    {
        var portfolio = await _api.GetPortfolioAsync(credentials, ct);
        var nowUtc = DateTime.UtcNow;

        // --- Позиции (цены и стоимость — в валюте бумаги как есть, без пересчёта) ---
        var heldTickers = new List<string>();
        var reconciliationSafe = true;
        foreach (var p in portfolio.Positions)
        {
            var currency = BrokerAssetClassifier.NormalizeCurrency(p.Currency);
            if (string.IsNullOrWhiteSpace(p.Ticker) || currency is null)
            {
                reconciliationSafe = false;
                _logger.LogWarning("Freedom sync: position without ticker or currency skipped (ticker '{Ticker}')", p.Ticker);
                result.Errors.Add("Freedom: позиция без тикера или валюты пропущена");
                continue;
            }

            if (p.Quantity <= 0m) continue; // закрытая позиция: по тикеру не держим

            await _positions.UpsertAsync(new BrokerPosition
            {
                AccountId = accountId,
                Ticker = p.Ticker.Trim(),
                Name = p.Name,
                AssetClass = BrokerAssetClassifier.Classify(p.Name),
                Currency = currency,
                Quantity = p.Quantity,
                AvgPrice = p.AveragePrice,
                LastPrice = p.MarketPrice,
                MarketValue = p.MarketValue,
                SyncedAt = nowUtc,
            });
            heldTickers.Add(p.Ticker.Trim());
            result.PositionsSynced++;
        }

        if (await SyncGuards.ShouldDeferEmptyReconciliationAsync(
                EmptyPositionsSeen, accountId, reconciliationSafe, heldTickers.Count,
                async () => (await _positions.GetByAccountAsync(accountId)).Count,
                localCount => _logger.LogWarning(
                    "Freedom sync: broker returned no positions while {Count} local position(s) exist — deferring deletion to the next sync",
                    localCount)))
        {
            reconciliationSafe = false;
        }

        if (reconciliationSafe)
        {
            var closed = await _positions.DeleteAllExceptAsync(accountId, heldTickers);
            result.PositionsClosed += closed;
            if (closed > 0) _logger.LogInformation("Freedom sync: closed {Count} position(s) no longer held", closed);
        }

        // --- Деньги: остаток в своей валюте; RUR → RUB; нули не храним ---
        var keepCurrencies = new List<string>();
        // Две строки одной валюты (RUR и RUB) суммируются, а не затирают друг друга.
        var balances = portfolio.Cash
            .Select(c => (Currency: BrokerAssetClassifier.NormalizeCurrency(c.Currency), c.Amount))
            .Where(c => c.Currency is not null)
            .GroupBy(c => c.Currency!)
            .Select(g => (Currency: g.Key, Amount: g.Sum(x => x.Amount)))
            .Where(c => c.Amount != 0m);
        foreach (var (currency, amount) in balances)
        {
            await _cash.UpsertAsync(new BrokerCash { AccountId = accountId, Currency = currency, Amount = amount, SyncedAt = nowUtc });
            keepCurrencies.Add(currency);
            result.CashRowsSynced++;
        }

        if (portfolio.Cash.Count > 0) await _cash.DeleteAllExceptAsync(accountId, keepCurrencies);
    }

    private async Task SyncTradesAsync(ulong accountId, FreedomCredentials credentials, FreedomSyncResult result, CancellationToken ct)
    {
        var today = BusinessClock.MoscowToday();
        var trades = new List<FreedomTrade>();
        for (var year = TradesHistoryStart.Year; year <= today.Year; year++)
        {
            var from = year == TradesHistoryStart.Year ? TradesHistoryStart : new DateOnly(year, 1, 1);
            var to = year == today.Year ? today : new DateOnly(year, 12, 31);
            trades.AddRange(await FetchTradesAsync(credentials, from, to, 0, ct));
        }

        var mapped = trades.Select(t => FreedomOperationMapper.MapTrade(accountId, t)).ToList();
        result.OperationsUpserted += await _operations.UpsertManyAsync(mapped);
    }

    /// <summary>Сделки окна; ответ в лимит страницы считается усечённым — окно делится пополам (глубина ограничена).</summary>
    private async Task<IReadOnlyList<FreedomTrade>> FetchTradesAsync(
        FreedomCredentials credentials, DateOnly from, DateOnly to, int depth, CancellationToken ct)
    {
        var page = await _api.GetTradesAsync(credentials, from, to, ct);
        if (page.Count < TradesPageLimit || to <= from || depth >= 12) return page;

        var mid = from.AddDays((to.DayNumber - from.DayNumber) / 2);
        var left = await FetchTradesAsync(credentials, from, mid, depth + 1, ct);
        var right = await FetchTradesAsync(credentials, mid.AddDays(1), to, depth + 1, ct);
        return left.Concat(right).ToList();
    }

    private async Task SyncCashFlowsAsync(ulong accountId, FreedomCredentials credentials, FreedomSyncResult result, CancellationToken ct)
    {
        var flows = await _api.GetCashFlowsAsync(credentials, CashFlowsTake, ct);
        if (flows.Count >= CashFlowsTake)
        {
            _logger.LogInformation("Freedom sync: cash flow response hit the {Take}-row window — older rows stay from earlier syncs", CashFlowsTake);
        }

        var mapped = FreedomOperationMapper.MapCashFlows(accountId, flows);
        result.OperationsUpserted += await _operations.UpsertManyAsync(mapped);
    }
}
