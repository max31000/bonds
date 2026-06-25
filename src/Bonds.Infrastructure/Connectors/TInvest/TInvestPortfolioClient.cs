using Bonds.Infrastructure.Connectors.Moex;
using Bonds.Infrastructure.Services;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tinkoff.InvestApi;
using Tinkoff.InvestApi.V1;

namespace Bonds.Infrastructure.Connectors.TInvest;

/// <summary>
/// Реализация <see cref="ITInvestPortfolioClient"/> над официальным gRPC SDK
/// <c>Tinkoff.InvestApi</c>. Контракт верифицирован отражением сборки SDK 0.6.22.1
/// (см. README.md в этом каталоге для деталей §12.2). Только облигации (InstrumentType
/// "bond") интересны этому продукту — другие типы инструментов отфильтровываются здесь же,
/// чтобы Bonds.Core не знал о деталях контракта T-Invest.
/// <para>
/// <b>Токен резолвится лениво через <see cref="ITInvestTokenProvider"/></b>, а не передаётся
/// готовым <c>InvestApiClient</c> через DI (как было до пересмотра при подготовке к этапу 10) —
/// токен хранится ТОЛЬКО в БД на пользователя (<c>PUT /api/settings/tinvest-token</c>), без
/// ENV-фолбэка (см. <see cref="ITInvestTokenProvider"/> doc-comment и `BACKEND_DECISIONS.md`,
/// решение про single-account-only хранение). gRPC-клиент создаётся через
/// <c>InvestApiClientFactory.Create(InvestApiSettings)</c> один раз на скоуп (на цикл синка) и
/// кэшируется в этом экземпляре — пересоздавать канал на каждый вызов было бы избыточно, а сам
/// экземпляр класса живёт ровно один scoped-вызов (один цикл синка/один HTTP-запрос).
/// </para>
/// </summary>
public sealed class TInvestPortfolioClient : ITInvestPortfolioClient
{
    private readonly ITInvestTokenProvider _tokenProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TInvestPortfolioClient> _logger;
    private InvestApiClient? _client;
    private readonly SemaphoreSlim _clientLock = new(1, 1);

    public TInvestPortfolioClient(
        ITInvestTokenProvider tokenProvider,
        IConfiguration configuration,
        ILogger<TInvestPortfolioClient> logger)
    {
        _tokenProvider = tokenProvider;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Резолвит токен (БД на пользователя — единственный источник, см. doc-comment класса) и
    /// лениво создаёт gRPC-клиент. Бросает <see cref="InvalidOperationException"/> с понятным
    /// сообщением, если токен не задан — <c>BondSyncService</c>/<c>SyncCycleService</c> уже умеют
    /// деградировать на ошибке одного шага синка без падения всего цикла (spec §4.4).
    /// </summary>
    private async Task<InvestApiClient> GetClientAsync(CancellationToken ct)
    {
        if (_client is not null) return _client;

        await _clientLock.WaitAsync(ct);
        try
        {
            if (_client is not null) return _client;

            var token = await _tokenProvider.GetTokenAsync(ct);
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException(
                    "T-Invest token is not configured. Set it via PUT /api/settings/tinvest-token before syncing.");
            }

            _client = InvestApiClientFactory.Create(new InvestApiSettings
            {
                AccessToken = token,
                AppName = "bonds-portfolio-analytics",
                Sandbox = _configuration.GetValue<bool?>("TInvest:Sandbox") ?? false,
            });
            return _client;
        }
        finally
        {
            _clientLock.Release();
        }
    }

    public async Task<string?> GetPrimaryAccountIdAsync(CancellationToken ct = default)
    {
        var client = await GetClientAsync(ct);
        var response = await client.Users.GetAccountsAsync(new GetAccountsRequest(), cancellationToken: ct);
        var account = response.Accounts.FirstOrDefault(a => a.Status == AccountStatus.Open);
        if (account is null)
        {
            _logger.LogWarning("T-Invest: no open account found for configured token");
            return null;
        }

        return account.Id;
    }

    public async Task<IReadOnlyList<TInvestPortfolioPosition>> GetBondPositionsAsync(string accountId, CancellationToken ct = default)
    {
        var client = await GetClientAsync(ct);
        var response = await client.Operations.GetPortfolioAsync(
            new PortfolioRequest { AccountId = accountId }, cancellationToken: ct);

        var result = new List<TInvestPortfolioPosition>();
        foreach (var position in response.Positions)
        {
            // InstrumentType приходит как нижнерегистровая строка протокола ("bond", "share", ...) —
            // не enum (verified §12.2, см. README.md). Только облигации релевантны спеке (§3).
            if (!string.Equals(position.InstrumentType, "bond", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            result.Add(new TInvestPortfolioPosition
            {
                Figi = position.Figi,
                InstrumentUid = position.InstrumentUid,
                Quantity = position.Quantity.ToDecimal(),
                AveragePositionPrice = position.AveragePositionPrice.ToDecimal(),
                CurrentPrice = position.CurrentPrice.ToNullableDecimal(),
                CurrentNkd = position.CurrentNkd.ToNullableDecimal(),
            });
        }

        return result;
    }

    public async Task<IReadOnlyList<TInvestOperation>> GetOperationsAsync(string accountId, DateTime? from, CancellationToken ct = default)
    {
        var client = await GetClientAsync(ct);
        var result = new List<TInvestOperation>();
        string? cursor = null;

        // GetOperationsByCursor — пагинированный курсорный эндпоинт (предпочтительнее устаревшего
        // GetOperations без курсора, verified §12.2): поддерживает инкрементальный синк через
        // параметр From и постраничный обход через Cursor/HasNext (plan/04 Часть B п.6).
        do
        {
            var request = new GetOperationsByCursorRequest
            {
                AccountId = accountId,
                From = from.HasValue ? Timestamp.FromDateTime(DateTime.SpecifyKind(from.Value, DateTimeKind.Utc)) : null,
                Limit = 1000,
            };
            if (cursor is not null)
            {
                request.Cursor = cursor;
            }

            var response = await client.Operations.GetOperationsByCursorAsync(request, cancellationToken: ct);

            foreach (var item in response.Items)
            {
                // Только исполненные операции — отменённые/в обработке не должны попадать в журнал
                // истины для XIRR (spec §6.9).
                if (item.State != OperationState.Executed)
                {
                    continue;
                }

                result.Add(new TInvestOperation
                {
                    Id = item.Id,
                    OperationType = item.Type.ToString(),
                    Figi = string.IsNullOrEmpty(item.Figi) ? null : item.Figi,
                    InstrumentUid = string.IsNullOrEmpty(item.InstrumentUid) ? null : item.InstrumentUid,
                    Date = item.Date.ToDateTime(),
                    PaymentRub = item.Payment.ToDecimal(),
                    Quantity = item.Quantity == 0 ? null : item.Quantity,
                    AccruedInterest = item.AccruedInt.ToNullableDecimal(),
                });
            }

            cursor = response.HasNext ? response.NextCursor : null;
        } while (!string.IsNullOrEmpty(cursor));

        return result;
    }

    public async Task<string?> GetIsinByFigiAsync(string figi, CancellationToken ct = default)
    {
        try
        {
            var client = await GetClientAsync(ct);
            var response = await client.Instruments.BondByAsync(
                new InstrumentRequest { IdType = InstrumentIdType.Figi, Id = figi }, cancellationToken: ct);
            return string.IsNullOrEmpty(response.Instrument.Isin) ? null : response.Instrument.Isin;
        }
        catch (Grpc.Core.RpcException ex)
        {
            _logger.LogWarning(ex, "T-Invest: failed to resolve ISIN for FIGI {Figi}", figi);
            return null;
        }
    }

    public async Task<IReadOnlyDictionary<string, TInvestQuote>> GetQuotesAsync(IReadOnlyCollection<string> figis, CancellationToken ct = default)
    {
        var result = new Dictionary<string, TInvestQuote>();
        if (figis.Count == 0) return result;

        var client = await GetClientAsync(ct);
        var lastPricesRequest = new GetLastPricesRequest();
        // InstrumentId (не устаревший Figi) принимает FIGI как валидное значение в этом контракте —
        // используем его, чтобы не зависеть от deprecated поля (verified §12.2, см. README.md).
        lastPricesRequest.InstrumentId.AddRange(figis);
        var lastPrices = await client.MarketData.GetLastPricesAsync(lastPricesRequest, cancellationToken: ct);

        var lastByFigi = lastPrices.LastPrices.ToDictionary(p => p.Figi, p => p.Price.ToNullableDecimal());

        foreach (var figi in figis)
        {
            decimal? bestBid = null, bestAsk = null;
            try
            {
                var orderBook = await client.MarketData.GetOrderBookAsync(
                    new GetOrderBookRequest { InstrumentId = figi, Depth = 1 }, cancellationToken: ct);
                bestBid = orderBook.Bids.Count > 0 ? orderBook.Bids[0].Price.ToNullableDecimal() : null;
                bestAsk = orderBook.Asks.Count > 0 ? orderBook.Asks[0].Price.ToNullableDecimal() : null;
            }
            catch (Grpc.Core.RpcException ex)
            {
                // Стакан может быть недоступен (бумага не торгуется сегодня и т.п.) — это не повод
                // валить весь синк котировок, последняя цена всё равно может быть полезна.
                _logger.LogWarning(ex, "T-Invest: order book unavailable for {Figi}", figi);
            }

            result[figi] = new TInvestQuote
            {
                Figi = figi,
                LastPrice = lastByFigi.GetValueOrDefault(figi),
                BestBid = bestBid,
                BestAsk = bestAsk,
            };
        }

        return result;
    }
}
