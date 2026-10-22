using System.Collections.Concurrent;
using Bonds.Core.Currencies;
using Bonds.Core.Interfaces.Repositories;
using Bonds.Core.Models;
using Bonds.Infrastructure.Connectors.Moex;
using Bonds.Infrastructure.Connectors.TInvest;
using Microsoft.Extensions.Logging;

namespace Bonds.Infrastructure.Sync;

/// <summary>Синк валюты, вызываемый из <see cref="BondSyncService"/> на уже полученных данных брокера (задача 55).</summary>
public interface ICurrencySyncService
{
    /// <summary>
    /// Применяет валютные позиции (<paramref name="currencies"/>: количество — ЕДИНИЦЫ валюты, цены — РУБЛИ за единицу) и операции по
    /// валюте (<paramref name="currencyOps"/>, платёж УЖЕ пересчитан в рубли — это делает <see cref="BondSyncService"/>) к валютным
    /// таблицам. Не делает собственных вызовов GetPortfolio/GetOperations.
    /// </summary>
    Task<CurrencySyncResult> ApplyAsync(
        ulong accountId,
        IReadOnlyList<TInvestPortfolioPosition> currencies,
        IReadOnlyList<TInvestOperation> currencyOps,
        CancellationToken ct = default);
}

/// <summary>Итог <see cref="ICurrencySyncService.ApplyAsync"/>. <see cref="Errors"/> — частичные сбои (не исключения).</summary>
public sealed class CurrencySyncResult
{
    public int CurrenciesSynced { get; set; }
    public int OperationsUpserted { get; set; }
    public int PositionsClosed { get; set; }
    public List<string> Errors { get; } = [];
}

/// <summary>
/// Задача 55: синк валюты (юани и т.п.) — ОТДЕЛЬНЫЙ домен по образцу <see cref="FundSyncService"/>. Резолвит валюту по FIGI (справочник из
/// T-Invest <c>CurrencyBy</c> + FACEVALUE с MOEX, обновление раз в сутки), делает upsert позиций (рубли за ЕДИНИЦУ) и операций (по
/// <c>external_id</c>, знак брокера не переписывается) и сверяет позиции с брокером (<see cref="SyncGuards"/>: пустой ответ — удаляем со
/// 2-го раза). Проданная валюта исчезает из <c>currency_positions</c>, её операции остаются. Облигационные таблицы не трогает.
/// </summary>
public sealed class CurrencySyncService : ICurrencySyncService
{
    /// <summary>Справочник валюты (имя/FACEVALUE) обновляется не чаще раза в сутки.</summary>
    public static readonly TimeSpan CatalogRefreshInterval = TimeSpan.FromHours(24);

    // Счета, по которым предыдущий синк получил от брокера пустой список валют при непустых локальных позициях.
    internal static readonly ConcurrentDictionary<ulong, byte> EmptyCurrenciesSeen = new();

    private readonly ITInvestPortfolioClient _tInvest;
    private readonly IMoexIssClient _moex;
    private readonly ICurrencyRepository _currencies;
    private readonly ICurrencyPositionRepository _positions;
    private readonly ICurrencyOperationRepository _operations;
    private readonly ILogger<CurrencySyncService> _logger;

    public CurrencySyncService(
        ITInvestPortfolioClient tInvest,
        IMoexIssClient moex,
        ICurrencyRepository currencies,
        ICurrencyPositionRepository positions,
        ICurrencyOperationRepository operations,
        ILogger<CurrencySyncService> logger)
    {
        _tInvest = tInvest;
        _moex = moex;
        _currencies = currencies;
        _positions = positions;
        _operations = operations;
        _logger = logger;
    }

    public async Task<CurrencySyncResult> ApplyAsync(
        ulong accountId,
        IReadOnlyList<TInvestPortfolioPosition> currencies,
        IReadOnlyList<TInvestOperation> currencyOps,
        CancellationToken ct = default)
    {
        var result = new CurrencySyncResult();
        var cache = new Dictionary<string, Currency?>();

        // --- Позиции ---
        var heldIds = new HashSet<ulong>();
        var reconciliationSafe = true;
        var nowUtc = DateTime.UtcNow;
        foreach (var tip in currencies)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var currency = await ResolveCurrencyAsync(tip.Figi, cache, ct);
                if (currency is null)
                {
                    reconciliationSafe = false;
                    result.Errors.Add($"Не удалось определить валюту для FIGI {tip.Figi} — позиция пропущена");
                    continue;
                }

                heldIds.Add(currency.Id);
                var currentPrice = tip.CurrentPrice is > 0m ? tip.CurrentPrice : null;
                await _positions.UpsertAsync(new CurrencyPosition
                {
                    AccountId = accountId,
                    CurrencyId = currency.Id,
                    Quantity = tip.Quantity, // единицы валюты (юани), не лоты
                    // 0 = брокер не знает цену входа: хранить «0 ₽ за юань» как факт нельзя.
                    AvgPurchasePriceRub = tip.AveragePositionPrice > 0m ? tip.AveragePositionPrice : null,
                    CurrentPriceRub = currentPrice,
                    PriceAsOf = currentPrice is null ? null : nowUtc,
                });
                result.CurrenciesSynced++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to sync currency position for FIGI {Figi}", tip.Figi);
                result.Errors.Add($"Валюта FIGI {tip.Figi}: ошибка синка ({ex.GetType().Name})");
                reconciliationSafe = false;
            }
        }

        // --- Операции (не теряем деньги: нерезолвленная валюта -> currency_id NULL) ---
        try
        {
            var domainOps = new List<CurrencyOperation>();
            foreach (var op in currencyOps)
            {
                var mapped = TInvestCurrencyOperationMapper.Map(op.OperationType);
                if (mapped is null) continue;

                Currency? currency = null;
                if (!string.IsNullOrEmpty(op.Figi))
                {
                    try
                    {
                        currency = await ResolveCurrencyAsync(op.Figi, cache, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Failed to resolve currency for operation FIGI {Figi}", op.Figi);
                    }
                }

                domainOps.Add(new CurrencyOperation
                {
                    AccountId = accountId,
                    CurrencyId = currency?.Id,
                    Type = mapped.Value,
                    Date = op.Date,
                    AmountRub = op.PaymentRub, // РУБЛИ, знак брокера — как есть (валюта платежа проверена выше по стеку)
                    Quantity = mapped is CurrencyOperationType.Buy or CurrencyOperationType.Sell
                        ? CurrencyOperationUnits.Resolve(op.Quantity, op.PaymentRub, op.UnitPrice)
                        : null,
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
            _logger.LogError(ex, "Failed to sync currency operations journal");
            result.Errors.Add("Не удалось синхронизировать журнал операций по валюте");
        }

        // --- Сверка (образец — фонды/облигации): пустой ответ брокера при непустых локальных позициях — удаляем со 2-го раза.
        if (await SyncGuards.ShouldDeferEmptyReconciliationAsync(
                EmptyCurrenciesSeen, accountId, reconciliationSafe, currencies.Count,
                async () => (await _positions.GetByAccountAsync(accountId)).Count,
                localCount => _logger.LogWarning(
                    "Currency sync: broker returned no currencies while {Count} local position(s) exist — deferring deletion to the next sync",
                    localCount)))
        {
            reconciliationSafe = false;
        }

        if (reconciliationSafe)
        {
            try
            {
                result.PositionsClosed = await _positions.DeleteAllExceptAsync(accountId, heldIds);
                if (result.PositionsClosed > 0)
                {
                    _logger.LogInformation("Currency sync: closed {Count} currency position(s) no longer held at the broker", result.PositionsClosed);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to reconcile closed currency positions");
                result.Errors.Add("Не удалось убрать закрытые позиции по валюте");
            }
        }

        return result;
    }

    /// <summary>
    /// Валюта по FIGI: из справочника (если моложе суток), иначе обновление/создание из T-Invest (+ FACEVALUE с MOEX). Null — валюту не
    /// удалось определить. Результат кэшируется на время синка.
    /// </summary>
    private async Task<Currency?> ResolveCurrencyAsync(string figi, Dictionary<string, Currency?> cache, CancellationToken ct)
    {
        if (cache.TryGetValue(figi, out var cached)) return cached;

        var existing = await _currencies.GetByFigiAsync(figi);
        if (existing is not null && DateTime.UtcNow - existing.UpdatedAt < CatalogRefreshInterval)
        {
            cache[figi] = existing;
            return existing;
        }

        Currency? resolved = existing;
        var info = await _tInvest.GetCurrencyInfoAsync(figi, ct);
        if (info is not null)
        {
            var currency = existing ?? new Currency { Figi = figi };
            currency.IsoCode = info.IsoCode;
            currency.Ticker = info.Ticker ?? currency.Ticker;
            currency.Name = info.Name ?? currency.Name;
            currency.Lot = info.Lot ?? currency.Lot;
            currency.MoexSecid = CurrencyMoexSecid.Derive(currency.Ticker, currency.IsoCode) ?? currency.MoexSecid;
            currency.FaceValue = await ResolveFaceValueAsync(currency, ct);
            currency.Id = await _currencies.UpsertAsync(currency);
            resolved = currency;
        }

        cache[figi] = resolved;
        return resolved;
    }

    /// <summary>FACEVALUE с MOEX (за сколько единиц котируется CLOSE); недоступен — прежнее значение справочника, иначе 1.</summary>
    private async Task<decimal> ResolveFaceValueAsync(Currency currency, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(currency.MoexSecid))
        {
            try
            {
                var face = await _moex.GetCurrencyFaceValueAsync(currency.MoexSecid, ct);
                if (face is > 0m) return face.Value;
                _logger.LogWarning("MOEX FACEVALUE for {Secid} not found — keeping {Face}", currency.MoexSecid, currency.FaceValue);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "MOEX FACEVALUE lookup failed for {Secid} — keeping {Face}", currency.MoexSecid, currency.FaceValue);
            }
        }

        return currency.FaceValue > 0m ? currency.FaceValue : 1m;
    }
}
