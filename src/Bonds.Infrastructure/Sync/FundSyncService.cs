using System.Collections.Concurrent;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Infrastructure.Connectors.Moex;
using Bonds.Infrastructure.Connectors.TInvest;
using Microsoft.Extensions.Logging;

namespace Bonds.Infrastructure.Sync;

/// <summary>Синк фондов, вызываемый из <see cref="BondSyncService"/> на уже полученных данных брокера.</summary>
public interface IFundSyncService
{
    /// <summary>
    /// Применяет позиции фондов (<paramref name="funds"/>, рубли за пай) и операции по фондам (<paramref name="fundOps"/>)
    /// брокера к фондовым таблицам. Не делает собственных вызовов GetPortfolio/GetOperations.
    /// </summary>
    Task<FundSyncResult> ApplyAsync(
        ulong accountId,
        IReadOnlyList<TInvestPortfolioPosition> funds,
        IReadOnlyList<TInvestOperation> fundOps,
        CancellationToken ct = default);
}

/// <summary>Итог <see cref="IFundSyncService.ApplyAsync"/>. <see cref="Errors"/> — частичные сбои (не исключения).</summary>
public sealed class FundSyncResult
{
    public int FundsSynced { get; set; }
    public int OperationsUpserted { get; set; }
    public int PositionsClosed { get; set; }
    public List<string> Errors { get; } = [];
}

/// <summary>
/// Задача 51: синк фондов (БПИФ/ETF) — ОТДЕЛЬНЫЙ домен от облигаций. Резолвит фонд по FIGI (справочник из T-Invest,
/// обновление раз в сутки), делает upsert позиций (рубли за пай) и операций (по <c>external_id</c>, знак брокера
/// не переписывается) и сверяет позиции с брокером. Закрытые фонды исчезают из <c>fund_positions</c>, их операции остаются.
/// Облигационные таблицы не трогает.
/// </summary>
public sealed class FundSyncService : IFundSyncService
{
    /// <summary>Справочник фонда (TER/имя) обновляется не чаще раза в сутки.</summary>
    public static readonly TimeSpan CatalogRefreshInterval = TimeSpan.FromHours(24);

    // Счета, по которым предыдущий синк получил от брокера пустой список фондов при непустых локальных позициях
    // (аналог BondSyncService.EmptyPortfolioSeen, отдельный словарь: домены сверяются независимо).
    internal static readonly ConcurrentDictionary<ulong, byte> EmptyFundsSeen = new();

    private readonly ITInvestPortfolioClient _tInvest;
    private readonly IMoexIssClient _moex;
    private readonly IFundRepository _funds;
    private readonly IFundPositionRepository _positions;
    private readonly IFundOperationRepository _operations;
    private readonly ILogger<FundSyncService> _logger;

    public FundSyncService(
        ITInvestPortfolioClient tInvest,
        IMoexIssClient moex,
        IFundRepository funds,
        IFundPositionRepository positions,
        IFundOperationRepository operations,
        ILogger<FundSyncService> logger)
    {
        _tInvest = tInvest;
        _moex = moex;
        _funds = funds;
        _positions = positions;
        _operations = operations;
        _logger = logger;
    }

    public async Task<FundSyncResult> ApplyAsync(
        ulong accountId,
        IReadOnlyList<TInvestPortfolioPosition> funds,
        IReadOnlyList<TInvestOperation> fundOps,
        CancellationToken ct = default)
    {
        var result = new FundSyncResult();
        // Кэш FIGI в пределах синка: фонд с десятком операций не должен давать десять походов в БД/T-Invest.
        var cache = new Dictionary<string, Fund?>();

        // --- Позиции ---
        var heldFundIds = new HashSet<ulong>();
        var reconciliationSafe = true;
        var nowUtc = DateTime.UtcNow;
        foreach (var tip in funds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var fund = await ResolveFundAsync(tip.Figi, cache, ct);
                if (fund is null)
                {
                    reconciliationSafe = false;
                    result.Errors.Add($"Не удалось определить фонд для FIGI {tip.Figi} — позиция пропущена");
                    continue;
                }

                heldFundIds.Add(fund.Id);
                await _positions.UpsertAsync(new FundPosition
                {
                    AccountId = accountId,
                    FundId = fund.Id,
                    Quantity = tip.Quantity,
                    AvgPurchasePriceRub = tip.AveragePositionPrice,
                    CurrentPriceRub = tip.CurrentPrice,
                    PriceAsOf = tip.CurrentPrice is null ? null : nowUtc,
                });
                result.FundsSynced++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to sync fund position for FIGI {Figi}", tip.Figi);
                result.Errors.Add($"Фонд FIGI {tip.Figi}: ошибка синка ({ex.GetType().Name})");
                reconciliationSafe = false;
            }
        }

        // --- Операции (не теряем деньги: нерезолвленный фонд -> fund_id NULL) ---
        try
        {
            var domainOps = new List<FundOperation>();
            foreach (var op in fundOps)
            {
                var mapped = TInvestFundOperationMapper.Map(op.OperationType);
                if (mapped is null) continue;

                Fund? fund = null;
                if (!string.IsNullOrEmpty(op.Figi))
                {
                    try
                    {
                        fund = await ResolveFundAsync(op.Figi, cache, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Failed to resolve fund for operation FIGI {Figi}", op.Figi);
                    }
                }

                domainOps.Add(new FundOperation
                {
                    AccountId = accountId,
                    FundId = fund?.Id,
                    Type = mapped.Value,
                    Date = op.Date,
                    AmountRub = op.PaymentRub, // знак брокера — как есть
                    Quantity = op.Quantity,
                    ExternalId = op.Id,
                });
            }

            if (domainOps.Count > 0)
            {
                result.OperationsUpserted = await _operations.UpsertManyByExternalIdAsync(domainOps);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to sync fund operations journal");
            result.Errors.Add("Не удалось синхронизировать журнал операций по фондам");
        }

        // --- Сверка (образец — облигационная): пустой ответ брокера при непустых локальных позициях удаляем
        // только со второго подряд синка.
        if (reconciliationSafe && funds.Count == 0)
        {
            var localCount = (await _positions.GetByAccountAsync(accountId)).Count;
            if (localCount > 0 && !EmptyFundsSeen.ContainsKey(accountId))
            {
                EmptyFundsSeen[accountId] = 0;
                reconciliationSafe = false;
                _logger.LogWarning(
                    "Fund sync: broker returned no funds while {Count} local position(s) exist — deferring deletion to the next sync",
                    localCount);
            }
        }
        else
        {
            EmptyFundsSeen.TryRemove(accountId, out _);
        }

        if (reconciliationSafe)
        {
            try
            {
                result.PositionsClosed = await _positions.DeleteAllExceptAsync(accountId, heldFundIds);
                if (result.PositionsClosed > 0)
                {
                    _logger.LogInformation("Fund sync: closed {Count} fund position(s) no longer held at the broker", result.PositionsClosed);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to reconcile closed fund positions");
                result.Errors.Add("Не удалось убрать закрытые позиции по фондам");
            }
        }

        return result;
    }

    /// <summary>
    /// Фонд по FIGI: из справочника (если моложе суток), иначе обновление/создание из T-Invest. Null — фонд не удалось
    /// определить (нет в справочнике и T-Invest его не отдал). Результат кэшируется на время синка.
    /// </summary>
    private async Task<Fund?> ResolveFundAsync(string figi, Dictionary<string, Fund?> cache, CancellationToken ct)
    {
        if (cache.TryGetValue(figi, out var cached)) return cached;

        var existing = await _funds.GetByFigiAsync(figi);
        if (existing is not null && DateTime.UtcNow - existing.UpdatedAt < CatalogRefreshInterval)
        {
            cache[figi] = existing;
            return existing;
        }

        Fund? resolved = existing;
        var info = await _tInvest.GetFundInfoAsync(figi, ct);
        if (info is not null)
        {
            var secid = await ResolveSecidAsync(info, ct);
            var fund = existing ?? new Fund { Figi = figi };
            fund.Isin = info.Isin;
            fund.Ticker = info.Ticker ?? fund.Ticker;
            fund.Secid = secid ?? fund.Secid;
            fund.Name = info.Name ?? fund.Name;
            fund.ManagementCompany = info.ManagementCompany ?? fund.ManagementCompany;
            fund.FocusType = info.FocusType ?? fund.FocusType;
            // TER, который T-Invest не отдал, не затираем прежним значением.
            fund.TotalExpenseFraction = info.TotalExpenseFraction ?? fund.TotalExpenseFraction;
            fund.Id = await _funds.UpsertAsync(fund);
            resolved = fund;
        }

        cache[figi] = resolved;
        return resolved;
    }

    /// <summary>SECID пая на MOEX: поиск по ISIN (общий ISS-поиск), иначе тикер (для TPAY/TOFZ SECID == тикер).</summary>
    private async Task<string?> ResolveSecidAsync(TInvestFundInfo info, CancellationToken ct)
    {
        try
        {
            var secid = await _moex.ResolveSecidByIsinAsync(info.Isin, ct);
            if (!string.IsNullOrWhiteSpace(secid)) return secid;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "MOEX SECID lookup failed for fund ISIN {Isin} — falling back to ticker", info.Isin);
        }

        return info.Ticker;
    }
}
